namespace DnevnikSdelok.Models;

public sealed class DashboardPreferences
{
    public string WindowBackground { get; set; } = "#000000";
    public string Foreground { get; set; } = "#00FF41";
    public string MutedForeground { get; set; } = "#00AA2B";
    public string AccentColor { get; set; } = "#00FF41";
    public string ChartGridColor { get; set; } = "#064D1B";
    public string ChartBullColor { get; set; } = "#00FF41";
    public string ChartBearColor { get; set; } = "#FF3158";
    public string TimeZoneId { get; set; } = TimeZoneInfo.Local.Id;
    public bool ShowChartGrid { get; set; } = true;
    public List<WidgetPreference> Widgets { get; set; } = CreateDefaultWidgets();
    public List<ColumnPreference> Columns { get; set; } = CreateDefaultColumns();

    public static DashboardPreferences CreateDefault() => new();

    public void MigrateLegacyColors()
    {
        WindowBackground = ReplaceLegacyColor(WindowBackground, "#0B1118", "#000000");
        Foreground = ReplaceLegacyColor(Foreground, "#E8EDF2", "#00FF41");
        MutedForeground = ReplaceLegacyColor(MutedForeground, "#91A0AD", "#00AA2B");
        AccentColor = ReplaceLegacyColor(AccentColor, "#72D6A0", "#00FF41");
        ChartGridColor = ReplaceLegacyColor(ChartGridColor, "#25323E", "#064D1B");
        ChartBullColor = ReplaceLegacyColor(ChartBullColor, "#31C48D", "#00FF41");
        ChartBearColor = ReplaceLegacyColor(ChartBearColor, "#F06464", "#FF3158");
        ChartBearColor = ReplaceLegacyColor(ChartBearColor, "#008F11", "#FF3158");

        foreach (var widget in Widgets)
        {
            widget.Background = ReplaceLegacyColor(widget.Background, "#121B24", "#030A04");
            widget.Foreground = ReplaceLegacyColor(widget.Foreground, "#E8EDF2", "#00FF41");
            widget.Accent = ReplaceLegacyColor(widget.Accent, "#72D6A0", "#00FF41");
        }
    }

    private static string ReplaceLegacyColor(string value, string oldColor, string newColor) =>
        value.Equals(oldColor, StringComparison.OrdinalIgnoreCase) ? newColor : value;

    private static List<WidgetPreference> CreateDefaultWidgets() =>
    [
        new() { Id = "pnl", Title = "Кумулятивная прибыль", Width = "medium", Height = "small", OrderIndex = 0, Background = "#030A04", Foreground = "#00FF41", Accent = "#00FF41" },
        new() { Id = "winrate", Title = "Винрейт", Width = "small", Height = "small", OrderIndex = 1, Background = "#030A04", Foreground = "#00FF41", Accent = "#00FF41" },
        new() { Id = "count", Title = "Исполнений", Width = "small", Height = "small", OrderIndex = 2, Background = "#030A04", Foreground = "#00FF41", Accent = "#00FF41" },
        new() { Id = "sync", Title = "Синхронизация", Width = "small", Height = "small", OrderIndex = 3, Background = "#030A04", Foreground = "#00FF41", Accent = "#00FF41" },
        new() { Id = "chart", Title = "График кумулятивной прибыли", Width = "wide", Height = "large", OrderIndex = 4, Background = "#030A04", Foreground = "#00FF41", Accent = "#00FF41" }
    ];

    private static List<ColumnPreference> CreateDefaultColumns()
    {
        var defaults = new[]
        {
            "ticker", "side", "entry", "inDeal", "entryReason", "category",
            "rating", "percent", "profitUsd", "commission", "funding", "depositPercent"
        };
        return TradeColumnDefinition.All.Select((column, index) => new ColumnPreference
        {
            Id = column.Id,
            Title = column.Title,
            Visible = defaults.Contains(column.Id, StringComparer.Ordinal),
            Width = column.DefaultWidth,
            Order = Array.IndexOf(defaults, column.Id) is var order && order >= 0 ? order : index + defaults.Length
        }).OrderBy(column => column.Order).ToList();
    }

    public void EnsureCatalog()
    {
        Widgets.RemoveAll(item => item.Id == "trades");
        foreach (var widget in DashboardWidgetDefinition.All)
        {
            if (Widgets.All(item => item.Id != widget.Id))
            {
                Widgets.Add(new WidgetPreference
                {
                    Id = widget.Id,
                    Title = widget.Title,
                    Width = widget.DefaultWidth,
                    Height = widget.DefaultHeight,
                    OrderIndex = Widgets.Count,
                    Background = "#030A04",
                    Foreground = Foreground,
                    Accent = AccentColor
                });
            }
        }

        foreach (var column in TradeColumnDefinition.All)
        {
            var existingColumn = Columns.FirstOrDefault(item => item.Id == column.Id);
            if (existingColumn is null)
            {
                Columns.Add(new ColumnPreference
                {
                    Id = column.Id,
                    Title = column.Title,
                    Visible = false,
                    Width = column.DefaultWidth,
                    Order = Columns.Count
                });
            }
            else
            {
                existingColumn.Title = column.Title;
                if (column.Id == "entry" && existingColumn.Width == 125)
                {
                    existingColumn.Width = column.DefaultWidth;
                }
                else if (column.Id == "depositPercent" && existingColumn.Width == 145)
                {
                    existingColumn.Width = column.DefaultWidth;
                }
            }
        }
    }
}

public sealed class WidgetPreference
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public bool Visible { get; set; } = true;
    public string Width { get; set; } = "small";
    public string Height { get; set; } = "small";
    public int OrderIndex { get; set; }
    public string Background { get; set; } = "#030A04";
    public string Foreground { get; set; } = "#00FF41";
    public string Accent { get; set; } = "#00FF41";
    public double? CustomWidth { get; set; }
    public double? CustomHeight { get; set; }
    public bool Pinned { get; set; }
    public bool Magnetized { get; set; } = true;
}

public sealed class ColumnPreference
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public bool Visible { get; set; }
    public double Width { get; set; } = 120;
    public int Order { get; set; }
}

public sealed record DashboardWidgetDefinition(string Id, string Title, string DefaultWidth, string DefaultHeight)
{
    public static IReadOnlyList<DashboardWidgetDefinition> All { get; } =
    [
        new("pnl", "Кумулятивная прибыль", "medium", "small"),
        new("winrate", "Винрейт", "small", "small"),
        new("count", "Исполнений", "small", "small"),
        new("sync", "Синхронизация", "small", "small"),
        new("chart", "График кумулятивной прибыли", "wide", "large")
    ];
}

public sealed record TradeColumnDefinition(string Id, string Title, double DefaultWidth)
{
    public static IReadOnlyList<TradeColumnDefinition> All { get; } =
    [
        new("ticker", "Тикер", 110),
        new("category", "Категория", 125),
        new("apiKey", "API ключ", 135),
        new("entryReason", "Причины входа", 180),
        new("exitReason", "Причины выхода", 180),
        new("additionalInfo", "Доп. информация", 180),
        new("cryptoScreener", "CryptoScreener", 155),
        new("execution", "Исполнение", 105),
        new("entry", "Вход", 205),
        new("depositPercent", "Плечо от депозита", 150),
        new("mpp", "МПП", 100),
        new("mpu", "МПУ", 100),
        new("entryUsd", "Вход ($)", 125),
        new("exit", "Выход", 125),
        new("exitUsd", "Выход ($)", 125),
        new("inDeal", "В сделке", 105),
        new("side", "Сторона", 105),
        new("profitPercent", "Прибыль (%)", 125),
        new("incomeUsd", "Доход ($)", 125),
        new("percent", "Процент", 110),
        new("profitUsd", "Прибыль ($)", 135),
        new("volume", "Объём", 125),
        new("maxVolume", "Макс. объём", 135),
        new("turnover", "Оборот", 125),
        new("commission", "Комиссия", 135),
        new("rating", "Рейтинг", 105),
        new("funding", "Фандинг", 120)
    ];
}
