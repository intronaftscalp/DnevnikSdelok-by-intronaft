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
        ? $"{NetPnl.Value.ToString("0.####", System.Globalization.CultureInfo.GetCultureInfo("ru-RU"))} {Execution.QuoteAsset}"
        : "—";
    public string Category { get; set; } = "";
    public string EntryReason { get; set; } = "";
    public string ExitReason { get; set; } = "";
    public string AdditionalInfo { get; set; } = "";
    public string CryptoScreener { get; set; } = "";
    public string Rating { get; set; } = "";
    public string JournalDescription { get; set; } = "";
    public string JournalConclusion { get; set; } = "";
    public string VideoUrl { get; set; } = "";
    public string ScreenshotRelativePath { get; set; } = "";
    public string ChartDrawingsJson { get; set; } = "[]";
    public string ExecutionLabel => "1";
    public DateTime? EntryTimeUtc { get; init; }
    public TimeSpan? TimeInDeal { get; init; }
    public decimal? ExposureMultiple { get; init; }
    public bool IsPositionEntry { get; init; }
    public bool IsPositionExit { get; init; }
    public string EntryDisplay => EntryTimeUtc.HasValue
        ? EntryTimeUtc.Value.ToLocalTime().ToString(
            "ddd dd.MM.yyyy HH:mm:ss",
            System.Globalization.CultureInfo.GetCultureInfo("ru-RU"))
        : "—";
    public string ExitDisplay => IsPositionExit
        ? Execution.Price.ToString("0.####", System.Globalization.CultureInfo.GetCultureInfo("ru-RU"))
        : "—";
    public string EntryUsdDisplay => IsPositionEntry
        ? Execution.QuoteQuantity.ToString("0.####", System.Globalization.CultureInfo.GetCultureInfo("ru-RU"))
        : "—";
    public string ExitUsdDisplay => IsPositionExit
        ? Execution.QuoteQuantity.ToString("0.####", System.Globalization.CultureInfo.GetCultureInfo("ru-RU"))
        : "—";
    public string InDealDisplay => TimeInDeal.HasValue ? FormatDuration(TimeInDeal.Value) : "—";
    public string FundingDisplay => "—";
    public string ExcursionDisplay => "—";
    public string VolumeDisplay => Execution.QuoteQuantity.ToString(
        "0.####",
        System.Globalization.CultureInfo.GetCultureInfo("ru-RU"));
    public string ProfitPercentDisplay => NetPnl.HasValue && Execution.QuoteQuantity != 0
        ? $"{(NetPnl.Value / Execution.QuoteQuantity * 100m).ToString("0.####", System.Globalization.CultureInfo.GetCultureInfo("ru-RU"))}%"
        : "—";
    public string LeverageDisplay => ExposureMultiple.HasValue
        ? $"{ExposureMultiple.Value.ToString("0.####", System.Globalization.CultureInfo.GetCultureInfo("ru-RU"))}x"
        : "—";

    private static string FormatDuration(TimeSpan duration)
    {
        var totalSeconds = Math.Max(0, (long)duration.TotalSeconds);
        var days = totalSeconds / 86_400;
        var hours = totalSeconds / 3_600 % 24;
        var minutes = totalSeconds / 60 % 60;
        var seconds = totalSeconds % 60;
        if (days > 0)
        {
            return $"{days}д, {hours}ч {minutes}м {seconds}с";
        }
        if (totalSeconds >= 3_600)
        {
            return $"{totalSeconds / 3_600}ч, {minutes}м {seconds}с";
        }
        if (totalSeconds >= 60)
        {
            return $"{minutes}м, {seconds}с";
        }
        return $"{seconds}с";
    }
}

public sealed class ExecutionNote
{
    public string Category { get; set; } = "";
    public string EntryReason { get; set; } = "";
    public string ExitReason { get; set; } = "";
    public string AdditionalInfo { get; set; } = "";
    public string CryptoScreener { get; set; } = "";
    public string Rating { get; set; } = "";
    public string JournalDescription { get; set; } = "";
    public string JournalConclusion { get; set; } = "";
    public string VideoUrl { get; set; } = "";
    public string ScreenshotRelativePath { get; set; } = "";
    public string ChartDrawingsJson { get; set; } = "[]";
}
