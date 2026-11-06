using DnevnikSdelok.Models;

namespace DnevnikSdelok.Services;

public sealed record PerformanceSummary(
    decimal NetPnl,
    int WinningTrades,
    int ClosedTrades,
    IReadOnlyList<PerformancePoint> Points,
    IReadOnlyList<ExecutionRow> Rows,
    bool HasUnconvertedFees);

public static class PerformanceCalculator
{
    private sealed class OpenFill
    {
        public required (int ConnectionId, string Symbol, string TradeId) Key { get; init; }
        public required DateTime EntryTimeUtc { get; init; }
        public required int Direction { get; init; }
        public required decimal RemainingQuantity { get; set; }
        public required string PositionId { get; init; }
        public decimal? ExposureMultiple { get; init; }
    }

    private sealed class PositionLifecycle
    {
        public List<OpenFill> OpenFills { get; } = [];
    }

    private sealed class LifecycleMetrics
    {
        public DateTime? EntryTimeUtc { get; set; }
        public decimal WeightedDurationTicks { get; set; }
        public decimal WeightedQuantity { get; set; }
        public TimeSpan? PositionDuration { get; set; }
        public decimal WeightedExposure { get; set; }
        public decimal ExposureQuantity { get; set; }
        public string? PositionId { get; set; }
        public bool IsPositionEntry { get; set; }
        public bool IsPositionExit { get; set; }

        public void AddDuration(DateTime entryTimeUtc, DateTime endTimeUtc, decimal quantity)
        {
            EntryTimeUtc ??= entryTimeUtc;
            if (quantity <= 0)
            {
                return;
            }
            WeightedDurationTicks += (decimal)Math.Max(0, (endTimeUtc - entryTimeUtc).Ticks) * quantity;
            WeightedQuantity += quantity;
        }

        public TimeSpan? GetAverageDuration() =>
            PositionDuration ??
            (WeightedQuantity > 0
                ? TimeSpan.FromTicks((long)(WeightedDurationTicks / WeightedQuantity))
                : null);

        public void SetPositionDuration(DateTime entryTimeUtc, DateTime endTimeUtc)
        {
            if (!EntryTimeUtc.HasValue || entryTimeUtc < EntryTimeUtc.Value)
            {
                EntryTimeUtc = entryTimeUtc;
            }

            PositionDuration = TimeSpan.FromTicks(
                Math.Max(0, (endTimeUtc - EntryTimeUtc.Value).Ticks));
        }

        public void AddExposure(decimal? exposure, decimal quantity)
        {
            if (exposure.HasValue && quantity > 0)
            {
                WeightedExposure += exposure.Value * quantity;
                ExposureQuantity += quantity;
            }
        }

        public decimal? GetAverageExposure() =>
            ExposureQuantity > 0 ? WeightedExposure / ExposureQuantity : null;
    }

    private sealed class SpotPosition
    {
        public decimal Quantity { get; set; }
        public decimal Cost { get; set; }
        public decimal RealizedPnl { get; set; }
    }

    private sealed class Position
    {
        public decimal Size { get; set; }
        public decimal Pnl { get; set; }
    }

    public static PerformanceSummary Calculate(
        IEnumerable<TradeExecution> executions,
        IReadOnlyDictionary<int, string> connectionNames,
        IReadOnlyDictionary<(int ConnectionId, string Symbol, string TradeId), decimal>? balanceAtEntry = null)
    {
        var ordered = executions
            .OrderBy(item => item.ExecutedAtUtc)
            .ThenBy(item => long.TryParse(item.ExchangeTradeId, out var id) ? id : 0)
            .ToList();
        var spotInventory = new Dictionary<(int ConnectionId, string Symbol), SpotPosition>();
        var futuresPositions = new Dictionary<(int ConnectionId, string Symbol, string Side), Position>();
        var pnlByKey = new Dictionary<(int ConnectionId, string Symbol, string TradeId), decimal?>();
        var lifecycleMetrics = CalculateLifecycleMetrics(ordered, balanceAtEntry);
        var points = new List<PerformancePoint>();
        var totalPnl = 0m;
        var wins = 0;
        var closes = 0;
        var hasUnconvertedFees = false;

        foreach (var execution in ordered)
        {
            if (execution.Market == "futures")
            {
                var fee = ConvertFee(execution, out var feeIsConverted);
                hasUnconvertedFees |= !feeIsConverted;
                var netPnl = execution.RealizedPnl - fee;
                pnlByKey[(execution.ConnectionId, execution.Symbol, execution.ExchangeTradeId)] = netPnl;
                if (execution.QuoteAsset == "USDT")
                {
                    totalPnl += netPnl;
                    points.Add(new PerformancePoint(execution.ConnectionId, execution.ExecutedAtUtc, netPnl));
                }

                var positionSide = execution.PositionSide == "SHORT" ? "SHORT" : "LONG";
                var key = (execution.ConnectionId, execution.Symbol, positionSide);
                if (!futuresPositions.TryGetValue(key, out var position))
                {
                    position = new Position();
                    futuresPositions[key] = position;
                }

                var signedQuantity = execution.Side == "BUY"
                    ? (positionSide == "LONG" ? execution.Quantity : -execution.Quantity)
                    : (positionSide == "LONG" ? -execution.Quantity : execution.Quantity);
                var previousSize = position.Size;
                position.Size += signedQuantity;
                position.Pnl += netPnl;
                if (position.Size == 0 || previousSize != 0 && Math.Sign(previousSize) != Math.Sign(position.Size))
                {
                    closes++;
                    if (position.Pnl > 0)
                    {
                        wins++;
                    }
                    position.Pnl = 0;
                }
                continue;
            }

            var spotKey = (execution.ConnectionId, execution.Symbol);
            if (!spotInventory.TryGetValue(spotKey, out var inventory))
            {
                inventory = new SpotPosition();
                spotInventory[spotKey] = inventory;
            }
            decimal? spotPnl = null;
            if (execution.Side == "BUY")
            {
                var quantity = execution.Quantity;
                var cost = execution.QuoteQuantity;
                if (execution.CommissionAsset == execution.BaseAsset)
                {
                    quantity -= execution.Commission;
                }
                else if (execution.CommissionAsset == execution.QuoteAsset)
                {
                    cost += execution.Commission;
                }
                else if (execution.Commission > 0)
                {
                    hasUnconvertedFees = true;
                }
                inventory.Quantity += quantity;
                inventory.Cost += cost;
            }
            else if (inventory.Quantity > 0 && execution.Quantity <= inventory.Quantity)
            {
                var averageCost = inventory.Cost / inventory.Quantity;
                var consumedQuantity = execution.Quantity;
                var proceeds = execution.QuoteQuantity;
                if (execution.CommissionAsset == execution.BaseAsset)
                {
                    consumedQuantity += execution.Commission;
                    consumedQuantity = Math.Min(consumedQuantity, inventory.Quantity);
                }
                else if (execution.CommissionAsset == execution.QuoteAsset)
                {
                    proceeds -= execution.Commission;
                }
                else if (execution.Commission > 0)
                {
                    hasUnconvertedFees = true;
                }
                spotPnl = proceeds - consumedQuantity * averageCost;
                inventory.RealizedPnl += spotPnl.Value;
                inventory.Quantity -= consumedQuantity;
                inventory.Cost = Math.Max(0, inventory.Cost - consumedQuantity * averageCost);
                if (inventory.Quantity == 0)
                {
                    closes++;
                    if (inventory.RealizedPnl > 0)
                    {
                        wins++;
                    }
                    inventory.RealizedPnl = 0;
                }
            }
            pnlByKey[(execution.ConnectionId, execution.Symbol, execution.ExchangeTradeId)] = spotPnl;
            if (spotPnl.HasValue)
            {
                if (execution.QuoteAsset == "USDT")
                {
                    totalPnl += spotPnl.Value;
                    points.Add(new PerformancePoint(execution.ConnectionId, execution.ExecutedAtUtc, spotPnl.Value));
                }
            }
        }

        var rows = ordered.AsEnumerable().Reverse().Select(execution => new ExecutionRow
        {
            Execution = execution,
            ConnectionName = connectionNames.TryGetValue(execution.ConnectionId, out var name) ? name : "Binance",
            NetPnl = pnlByKey[(execution.ConnectionId, execution.Symbol, execution.ExchangeTradeId)],
            EntryTimeUtc = lifecycleMetrics
                .GetValueOrDefault((execution.ConnectionId, execution.Symbol, execution.ExchangeTradeId))?.EntryTimeUtc,
            TimeInDeal = lifecycleMetrics
                .GetValueOrDefault((execution.ConnectionId, execution.Symbol, execution.ExchangeTradeId))?
                .GetAverageDuration(),
            ExposureMultiple = lifecycleMetrics
                .GetValueOrDefault((execution.ConnectionId, execution.Symbol, execution.ExchangeTradeId))?
                .GetAverageExposure(),
            IsPositionEntry = lifecycleMetrics
                .GetValueOrDefault((execution.ConnectionId, execution.Symbol, execution.ExchangeTradeId))?
                .IsPositionEntry ?? false,
            IsPositionExit = lifecycleMetrics
                .GetValueOrDefault((execution.ConnectionId, execution.Symbol, execution.ExchangeTradeId))?
                .IsPositionExit ?? false,
            PositionId = lifecycleMetrics
                .GetValueOrDefault((execution.ConnectionId, execution.Symbol, execution.ExchangeTradeId))?
                .PositionId
        }).ToList();

        return new PerformanceSummary(
            totalPnl,
            wins,
            closes,
            points,
            rows,
            hasUnconvertedFees);
    }

    private static Dictionary<(int ConnectionId, string Symbol, string TradeId), LifecycleMetrics>
        CalculateLifecycleMetrics(
            IReadOnlyList<TradeExecution> ordered,
            IReadOnlyDictionary<(int ConnectionId, string Symbol, string TradeId), decimal>? balanceAtEntry)
    {
        var positions = new Dictionary<(int ConnectionId, string Symbol, string Market, string Side), PositionLifecycle>();
        var metrics = new Dictionary<(int ConnectionId, string Symbol, string TradeId), LifecycleMetrics>();
        var nowUtc = DateTime.UtcNow;

        foreach (var execution in ordered)
        {
            var executionKey = (execution.ConnectionId, execution.Symbol, execution.ExchangeTradeId);
            var side = execution.Market == "futures"
                ? execution.PositionSide is "LONG" or "SHORT" ? execution.PositionSide : "BOTH"
                : "";
            var positionKey = (execution.ConnectionId, execution.Symbol, execution.Market, side);
            if (!positions.TryGetValue(positionKey, out var position))
            {
                position = new PositionLifecycle();
                positions[positionKey] = position;
            }

            var quantity = execution.Quantity;
            if (execution.Market == "spot")
            {
                if (execution.Side == "BUY" && execution.CommissionAsset == execution.BaseAsset)
                {
                    quantity -= execution.Commission;
                }
                else if (execution.Side == "SELL" && execution.CommissionAsset == execution.BaseAsset)
                {
                    quantity += execution.Commission;
                }
            }
            var signedQuantity = execution.Side == "BUY" ? quantity : -quantity;
            if (signedQuantity == 0)
            {
                continue;
            }

            var direction = Math.Sign(signedQuantity);
            var remainingQuantity = Math.Abs(signedQuantity);
            if (execution.Market == "spot" && direction < 0 && position.OpenFills.Count == 0)
            {
                continue;
            }
            decimal? exposure = null;
            if (balanceAtEntry is not null &&
                balanceAtEntry.TryGetValue(executionKey, out var depositAtEntry) &&
                depositAtEntry > 0 &&
                execution.QuoteAsset == "USDT")
            {
                exposure = execution.QuoteQuantity / depositAtEntry;
            }
            if (position.OpenFills.Count == 0 || position.OpenFills[0].Direction == direction)
            {
                var positionId = position.OpenFills.Count == 0
                    ? execution.ExchangeTradeId
                    : position.OpenFills[0].PositionId;
                AddOpenFill(
                    position, metrics, executionKey, execution.ExecutedAtUtc,
                    direction, remainingQuantity, exposure, positionId);
                continue;
            }

            while (remainingQuantity > 0 && position.OpenFills.Count > 0)
            {
                var openFill = position.OpenFills[0];
                var matchedQuantity = Math.Min(remainingQuantity, openFill.RemainingQuantity);
                var closeMetrics = GetMetrics(metrics, executionKey);
                closeMetrics.SetPositionDuration(openFill.EntryTimeUtc, execution.ExecutedAtUtc);
                closeMetrics.PositionId ??= openFill.PositionId;
                closeMetrics.IsPositionExit = true;
                closeMetrics.AddExposure(openFill.ExposureMultiple, matchedQuantity);
                GetMetrics(metrics, openFill.Key)
                    .AddDuration(openFill.EntryTimeUtc, execution.ExecutedAtUtc, matchedQuantity);
                openFill.RemainingQuantity -= matchedQuantity;
                remainingQuantity -= matchedQuantity;
                if (openFill.RemainingQuantity == 0)
                {
                    position.OpenFills.RemoveAt(0);
                }
            }

            if (remainingQuantity > 0 && execution.Market == "futures")
            {
                AddOpenFill(
                    position, metrics, executionKey, execution.ExecutedAtUtc,
                    direction, remainingQuantity, exposure,
                    $"{execution.ExchangeTradeId}:reversal");
            }
        }

        foreach (var openFill in positions.Values.SelectMany(item => item.OpenFills))
        {
            GetMetrics(metrics, openFill.Key)
                .AddDuration(openFill.EntryTimeUtc, nowUtc, openFill.RemainingQuantity);
        }

        return metrics;
    }

    private static void AddOpenFill(
        PositionLifecycle position,
        IDictionary<(int ConnectionId, string Symbol, string TradeId), LifecycleMetrics> metrics,
        (int ConnectionId, string Symbol, string TradeId) executionKey,
        DateTime entryTimeUtc,
        int direction,
        decimal quantity,
        decimal? exposureMultiple,
        string positionId)
    {
        var executionMetrics = GetMetrics(metrics, executionKey);
        executionMetrics.EntryTimeUtc ??= entryTimeUtc;
        executionMetrics.PositionId ??= positionId;
        executionMetrics.IsPositionEntry = true;
        executionMetrics.AddExposure(exposureMultiple, quantity);
        position.OpenFills.Add(new OpenFill
        {
            Key = executionKey,
            EntryTimeUtc = entryTimeUtc,
            Direction = direction,
            RemainingQuantity = quantity,
            PositionId = positionId,
            ExposureMultiple = exposureMultiple
        });
    }

    private static LifecycleMetrics GetMetrics(
        IDictionary<(int ConnectionId, string Symbol, string TradeId), LifecycleMetrics> metrics,
        (int ConnectionId, string Symbol, string TradeId) key)
    {
        if (!metrics.TryGetValue(key, out var value))
        {
            value = new LifecycleMetrics();
            metrics[key] = value;
        }
        return value;
    }

    private static decimal ConvertFee(TradeExecution execution, out bool converted)
    {
        if (execution.CommissionAsset == execution.QuoteAsset ||
            execution.CommissionAsset is "USDT" or "USDC" or "FDUSD")
        {
            converted = true;
            return execution.Commission;
        }
        if (execution.CommissionAsset == execution.BaseAsset)
        {
            converted = true;
            return execution.Commission * execution.Price;
        }

        converted = execution.Commission == 0;
        return 0;
    }
}
