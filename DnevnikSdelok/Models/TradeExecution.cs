namespace DnevnikSdelok.Models;

public sealed class TradeExecution
{
    public int ConnectionId { get; set; }
    public string ExchangeTradeId { get; set; } = "";
    public string Symbol { get; set; } = "";
    public string BaseAsset { get; set; } = "";
    public string QuoteAsset { get; set; } = "";
    public string Side { get; set; } = "";
    public string PositionSide { get; set; } = "";
    public string Market { get; set; } = "";
    public decimal Price { get; set; }
    public decimal Quantity { get; set; }
    public decimal QuoteQuantity { get; set; }
    public decimal Commission { get; set; }
    public string CommissionAsset { get; set; } = "";
    public decimal RealizedPnl { get; set; }
    public DateTime ExecutedAtUtc { get; set; }

    public string MarketLabel => Market == "spot" ? "Спот" : "Фьючерсы";
    public string SideLabel => Side == "BUY" ? "Покупка" : "Продажа";
}
