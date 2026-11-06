using DnevnikSdelok.Models;

namespace DnevnikSdelok.Services;

public static class DepositExposureCalculator
{
    private const decimal SnapshotTolerance = 0.00000001m;
    private static readonly TimeSpan FuturesHistoryWindow = TimeSpan.FromDays(90);

    public static Dictionary<(int ConnectionId, string Symbol, string TradeId), decimal> Calculate(
        IReadOnlyList<TradeExecution> executions,
        IReadOnlyList<ApiConnection> connections,
        IReadOnlyList<BalanceSnapshot> snapshots,
        IReadOnlyList<BalanceChange> changes)
    {
        var result = new Dictionary<(int ConnectionId, string Symbol, string TradeId), decimal>();
        foreach (var connection in connections)
        {
            var connectionExecutions = executions
                .Where(item => item.ConnectionId == connection.Id && item.Market == connection.Market)
                .OrderBy(item => item.ExecutedAtUtc)
                .ThenBy(item => long.TryParse(item.ExchangeTradeId, out var id) ? id : 0)
                .ToList();
            var connectionSnapshots = snapshots
                .Where(item => item.ConnectionId == connection.Id)
                .OrderBy(item => item.CapturedAtUtc)
                .ToList();

            if (connection.Market == "futures")
            {
                AddFuturesExposures(
                    connectionExecutions,
                    connectionSnapshots,
                    changes.Where(item => item.ConnectionId == connection.Id).ToList(),
                    result);
            }
            else if (connection.Market == "spot")
            {
                AddSpotExposures(connectionExecutions, connectionSnapshots, result);
            }
        }
        return result;
    }

    private static void AddFuturesExposures(
        IReadOnlyList<TradeExecution> executions,
        IReadOnlyList<BalanceSnapshot> snapshots,
        IReadOnlyList<BalanceChange> changes,
        IDictionary<(int ConnectionId, string Symbol, string TradeId), decimal> result)
    {
        if (snapshots.Count == 0)
        {
            return;
        }

        var snapshot = snapshots[^1];
        var historyStartsAt = snapshot.CapturedAtUtc - FuturesHistoryWindow;
        var orderedChanges = changes
            .Where(item => item.ChangedAtUtc <= snapshot.CapturedAtUtc)
            .OrderBy(item => item.ChangedAtUtc)
            .ToList();
        var changeIndex = orderedChanges.Count - 1;
        var usdtChangesAfterEntry = 0m;
        var hasUnconvertedChangesAfterEntry = false;
        for (var executionIndex = executions.Count - 1; executionIndex >= 0; executionIndex--)
        {
            var execution = executions[executionIndex];
            while (changeIndex >= 0 &&
                   orderedChanges[changeIndex].ChangedAtUtc > execution.ExecutedAtUtc)
            {
                var change = orderedChanges[changeIndex--];
                if (change.Amount != 0 && change.Asset != "USDT")
                {
                    hasUnconvertedChangesAfterEntry = true;
                }
                else if (change.Asset == "USDT")
                {
                    usdtChangesAfterEntry += change.Amount;
                }
            }

            if (execution.QuoteAsset != "USDT" ||
                execution.ExecutedAtUtc < historyStartsAt ||
                execution.ExecutedAtUtc >= snapshot.CapturedAtUtc ||
                hasUnconvertedChangesAfterEntry)
            {
                continue;
            }

            var balanceAtEntry = snapshot.Balance - usdtChangesAfterEntry;
            if (balanceAtEntry > 0)
            {
                result[(execution.ConnectionId, execution.Symbol, execution.ExchangeTradeId)] =
                    balanceAtEntry;
            }
        }
    }

    private static void AddSpotExposures(
        IReadOnlyList<TradeExecution> executions,
        IReadOnlyList<BalanceSnapshot> snapshots,
        IDictionary<(int ConnectionId, string Symbol, string TradeId), decimal> result)
    {
        if (snapshots.Count < 2)
        {
            return;
        }

        var executionIndex = 0;
        while (executionIndex < executions.Count &&
               executions[executionIndex].ExecutedAtUtc <= snapshots[0].CapturedAtUtc)
        {
            executionIndex++;
        }

        for (var index = 1; index < snapshots.Count; index++)
        {
            var earlier = snapshots[index - 1];
            var later = snapshots[index];
            if (later.CapturedAtUtc <= earlier.CapturedAtUtc)
            {
                continue;
            }

            var intervalStart = executionIndex;
            var expectedDelta = 0m;
            while (executionIndex < executions.Count &&
                   executions[executionIndex].ExecutedAtUtc <= later.CapturedAtUtc)
            {
                expectedDelta += GetSpotUsdtChange(executions[executionIndex]);
                executionIndex++;
            }
            if (Math.Abs(later.Balance - earlier.Balance - expectedDelta) > SnapshotTolerance)
            {
                continue;
            }

            var balance = earlier.Balance;
            for (var tradeIndex = intervalStart; tradeIndex < executionIndex; tradeIndex++)
            {
                var execution = executions[tradeIndex];
                if (execution.QuoteAsset == "USDT" && balance > 0)
                {
                    result[(execution.ConnectionId, execution.Symbol, execution.ExchangeTradeId)] = balance;
                }
                balance += GetSpotUsdtChange(execution);
            }
        }
    }

    private static decimal GetSpotUsdtChange(TradeExecution execution)
    {
        var change = 0m;
        if (execution.QuoteAsset == "USDT")
        {
            change = execution.Side == "BUY"
                ? -execution.QuoteQuantity
                : execution.QuoteQuantity;
        }
        if (execution.CommissionAsset == "USDT")
        {
            change -= execution.Commission;
        }
        return change;
    }
}
