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
        IReadOnlyDictionary<int, string> connectionNames)
    {
        var ordered = executions
            .OrderBy(item => item.ExecutedAtUtc)
            .ThenBy(item => long.TryParse(item.ExchangeTradeId, out var id) ? id : 0)
            .ToList();
        var spotInventory = new Dictionary<(int ConnectionId, string Symbol), SpotPosition>();
        var futuresPositions = new Dictionary<(int ConnectionId, string Symbol, string Side), Position>();
        var pnlByKey = new Dictionary<(int ConnectionId, string Symbol, string TradeId), decimal?>();
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
            NetPnl = pnlByKey[(execution.ConnectionId, execution.Symbol, execution.ExchangeTradeId)]
        }).ToList();

        return new PerformanceSummary(
            totalPnl,
            wins,
            closes,
            points,
            rows,
            hasUnconvertedFees);
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
