namespace DnevnikSdelok.Models;

public sealed class ExecutionRow
{
    public TradeExecution Execution { get; init; } = new();
    public string ConnectionName { get; init; } = "";
    public decimal? NetPnl { get; init; }

    public int ConnectionId => Execution.ConnectionId;
    public DateTime ExecutedAtLocal => Execution.ExecutedAtUtc.ToLocalTime();
    public string MarketLabel => Execution.MarketLabel;
    public string SideLabel => Execution.SideLabel;
    public string Symbol => Execution.Symbol;
    public decimal Price => Execution.Price;
    public decimal Quantity => Execution.Quantity;
    public decimal Commission => Execution.Commission;
    public string CommissionAsset => Execution.CommissionAsset;
    public string PnlDisplay => NetPnl.HasValue
        ? $"{NetPnl.Value:N4} {Execution.QuoteAsset}"
        : "—";
}
