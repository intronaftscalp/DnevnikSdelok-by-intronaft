namespace DnevnikSdelok.Models;

public sealed class ExecutionRow
{
    public TradeExecution Execution { get; init; } = new();
    public string ConnectionName { get; init; } = "";
    public decimal? NetPnl { get; init; }

    public int ConnectionId => Execution.ConnectionId;
    public string TimeZoneId { get; set; } = TimeZoneInfo.Local.Id;
    public DateTime ExecutedAtLocal => ConvertUtcToDisplayTime(Execution.ExecutedAtUtc);
    public string MarketLabel => Execution.MarketLabel;
    public string SideLabel => Execution.SideLabel;
    public string Symbol => Execution.Symbol;
    public decimal Price => Execution.Price;
    public decimal Quantity => Execution.Quantity;
    public decimal Commission => PositionCommission ?? Execution.Commission;
    public string CommissionDisplay => Commission.ToString(
        "0.####",
        System.Globalization.CultureInfo.GetCultureInfo("ru-RU"));
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
    public List<string> ScreenshotRelativePaths { get; set; } = [];
    public string ChartDrawingsJson { get; set; } = "[]";
    public string ExecutionLabel => "1";
    public DateTime? EntryTimeUtc { get; init; }
    public TimeSpan? TimeInDeal { get; init; }
    public decimal? ExposureMultiple { get; init; }
    public decimal? PositionVolume { get; init; }
    public decimal? PositionEntryQuoteQuantity { get; init; }
    public decimal? PositionExitQuoteQuantity { get; init; }
    public decimal? PositionExitPrice { get; init; }
    public decimal? PositionPercent { get; init; }
    public decimal? PositionCommission { get; init; }
    public string PositionKey { get; init; } = "";
    public bool IsSelected { get; set; }
    public DateTime? PositionSortTimeUtc { get; init; }
    public bool IsPositionEntry { get; init; }
    public bool IsPositionExit { get; init; }
    public string? PositionId { get; init; }
    public string EntryDisplay => EntryTimeUtc.HasValue
        ? ConvertUtcToDisplayTime(EntryTimeUtc.Value).ToString(
            "ddd dd.MM.yyyy HH:mm:ss",
            System.Globalization.CultureInfo.GetCultureInfo("ru-RU"))
        : "—";
    public string ExitDisplay => IsPositionExit
        ? (PositionExitPrice ?? Execution.Price).ToString("0.####", System.Globalization.CultureInfo.GetCultureInfo("ru-RU"))
        : "—";
    public string EntryUsdDisplay => IsPositionEntry
        ? (PositionEntryQuoteQuantity ?? Execution.QuoteQuantity).ToString("0.####", System.Globalization.CultureInfo.GetCultureInfo("ru-RU"))
        : "—";
    public string ExitUsdDisplay => IsPositionExit
        ? (PositionExitQuoteQuantity ?? Execution.QuoteQuantity).ToString("0.####", System.Globalization.CultureInfo.GetCultureInfo("ru-RU"))
        : "—";
    public string InDealDisplay => TimeInDeal.HasValue ? FormatDuration(TimeInDeal.Value) : "—";
    public string FundingDisplay => "—";
    public string ExcursionDisplay => "—";
    public string VolumeDisplay => (PositionVolume ?? Execution.QuoteQuantity).ToString(
        "0.####",
        System.Globalization.CultureInfo.GetCultureInfo("ru-RU"));
    public decimal? ProfitPercentValue => PositionKey.Length > 0
        ? PositionPercent
        : NetPnl.HasValue && (PositionEntryQuoteQuantity ?? Execution.QuoteQuantity) != 0
            ? NetPnl.Value / (PositionEntryQuoteQuantity ?? Execution.QuoteQuantity) * 100m
            : null;
    public bool IsProfitPositive => ProfitPercentValue.GetValueOrDefault() >= 0;
    public string ProfitPercentDisplay => ProfitPercentValue.HasValue
        ? $"{ProfitPercentValue.Value.ToString("0.####", System.Globalization.CultureInfo.GetCultureInfo("ru-RU"))}%"
        : "—";
    public string LeverageDisplay => ExposureMultiple.HasValue
        ? $"{ExposureMultiple.Value.ToString("0.####", System.Globalization.CultureInfo.GetCultureInfo("ru-RU"))}x"
        : "—";

    public DateTime ConvertUtcToDisplayTime(DateTime utcTime) =>
        TimeZoneInfo.ConvertTimeFromUtc(
            DateTime.SpecifyKind(utcTime, DateTimeKind.Utc),
            TimeZoneInfo.FindSystemTimeZoneById(TimeZoneId));

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
    public List<string> ScreenshotRelativePaths { get; set; } = [];
    public string ChartDrawingsJson { get; set; } = "[]";
}
