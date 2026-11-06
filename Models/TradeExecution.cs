namespace DnevnikSdelok.Models;

public sealed class TradeExecution
{
    public int ConnectionId { get; set; }
    public string ExchangeTradeId { get; set; } = "";
    public string ExchangeOrderId { get; set; } = "";
    public string OrderType { get; set; } = "";
    public bool? IsMaker { get; set; }
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
    public string OrderTypeDisplay => OrderType switch
    {
        "MARKET" => "Рыночный",
        "LIMIT" or "LIMIT_MAKER" => "Лимитный",
        "STOP" or "STOP_LOSS" => "Стоп",
        "STOP_LOSS_LIMIT" => "Стоп-лимитный",
        "STOP_MARKET" or "STOP_LOSS_MARKET" => "Стоп-рыночный",
        "TAKE_PROFIT" => "Тейк-профит",
        "TAKE_PROFIT_LIMIT" => "Тейк-профит · лимит",
        "TAKE_PROFIT_MARKET" => "Тейк-профит · рынок",
        "TRAILING_STOP_MARKET" => "Трейлинг-стоп",
        _ => string.IsNullOrWhiteSpace(OrderType)
            ? IsMaker == true ? "Лимитный · maker" : IsMaker == false
                ? "Тейкер · тип не загружен"
                : "Тип не загружен"
            : OrderType
    };

    public string MarketLabel => Market == "spot" ? "Спот" : "Фьючерсы";
    public string SideLabel => Side == "BUY" ? "Покупка" : "Продажа";
}
