using DnevnikSdelok.Models;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DnevnikSdelok.Services;

public sealed class BinanceApiClient : IDisposable
{
    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    private readonly string _apiKey;
    private readonly string _apiSecret;
    private readonly string _market;
    private readonly string _restBase;
    private readonly SemaphoreSlim _requestThrottle = new(1, 1);
    private DateTime _nextRequestUtc;

    public BinanceApiClient(string apiKey, string apiSecret, string market)
    {
        _apiKey = apiKey;
        _apiSecret = apiSecret;
        _market = market;
        _restBase = market == "spot" ? "https://api.binance.com" : "https://fapi.binance.com";
    }

    public async Task TestConnectionAsync(CancellationToken cancellationToken)
    {
        var endpoint = _market == "spot" ? "/api/v3/account" : "/fapi/v2/account";
        using var document = await GetSignedJsonAsync(endpoint, "", cancellationToken);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("Биржа вернула неожиданный ответ проверки API.");
        }
    }

    public async Task<int> FetchExecutionsAsync(
        int connectionId,
        DateTime? lastSyncedUtc,
        Action<IReadOnlyCollection<TradeExecution>> saveBatch,
        IProgress<int>? progress,
        CancellationToken cancellationToken)
    {
        var from = lastSyncedUtc?.AddMinutes(-5) ?? DateTime.UtcNow.AddDays(-90);
        var to = DateTime.UtcNow;

        if (_market == "spot")
        {
            return await FetchSpotExecutionsAsync(
                connectionId, from, to, saveBatch, progress, cancellationToken);
        }
        return await FetchFuturesExecutionsAsync(
            connectionId, from, to, saveBatch, progress, cancellationToken);
    }

    private async Task<int> FetchSpotExecutionsAsync(
        int connectionId,
        DateTime from,
        DateTime to,
        Action<IReadOnlyCollection<TradeExecution>> saveBatch,
        IProgress<int>? progress,
        CancellationToken cancellationToken)
    {
        using var exchangeInfo = await GetPublicJsonAsync("/api/v3/exchangeInfo", cancellationToken);
        var symbols = exchangeInfo.RootElement.GetProperty("symbols")
            .EnumerateArray()
            .Where(item =>
                item.TryGetProperty("status", out var status) && status.GetString() == "TRADING" &&
                (!item.TryGetProperty("isSpotTradingAllowed", out var spotAllowed) || spotAllowed.GetBoolean()))
            .Select(item => new
            {
                Symbol = item.GetProperty("symbol").GetString()!,
                BaseAsset = item.GetProperty("baseAsset").GetString()!,
                QuoteAsset = item.GetProperty("quoteAsset").GetString()!
            })
            .ToList();

        var savedCount = 0;
        for (var index = 0; index < symbols.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var symbol = symbols[index];
            var trades = new List<TradeExecution>();
            await FetchSpotWindowAsync(
                connectionId,
                symbol.Symbol,
                symbol.BaseAsset,
                symbol.QuoteAsset,
                from,
                to,
                trades,
                0,
                cancellationToken);
            if (trades.Count > 0)
            {
                saveBatch(trades);
                savedCount += trades.Count;
            }
            progress?.Report((index + 1) * 100 / symbols.Count);
        }
        return savedCount;
    }

    private async Task FetchSpotWindowAsync(
        int connectionId,
        string symbol,
        string baseAsset,
        string quoteAsset,
        DateTime from,
        DateTime to,
        List<TradeExecution> trades,
        int depth,
        CancellationToken cancellationToken)
    {
        var parameters = $"symbol={Uri.EscapeDataString(symbol)}" +
                         $"&startTime={new DateTimeOffset(from).ToUnixTimeMilliseconds()}" +
                         $"&endTime={new DateTimeOffset(to).ToUnixTimeMilliseconds()}" +
                         "&limit=1000";
        using var document = await GetSignedJsonAsync("/api/v3/myTrades", parameters, cancellationToken);
        var rows = document.RootElement.EnumerateArray().ToList();
        if (rows.Count < 1000)
        {
            trades.AddRange(rows.Select(row => ParseSpotTrade(connectionId, symbol, baseAsset, quoteAsset, row)));
            return;
        }

        if (depth >= 18 || (to - from).TotalMilliseconds < 2)
        {
            throw new InvalidOperationException(
                $"Для {symbol} найдено не менее 1000 исполнений в очень коротком интервале. " +
                "Синхронизация остановлена, чтобы не пропустить сделки.");
        }

        var midpoint = from.AddTicks((to - from).Ticks / 2);
        await FetchSpotWindowAsync(
            connectionId, symbol, baseAsset, quoteAsset, from, midpoint, trades, depth + 1, cancellationToken);
        await FetchSpotWindowAsync(
            connectionId, symbol, baseAsset, quoteAsset, midpoint.AddMilliseconds(1), to, trades, depth + 1, cancellationToken);
    }

    private async Task<int> FetchFuturesExecutionsAsync(
        int connectionId,
        DateTime from,
        DateTime to,
        Action<IReadOnlyCollection<TradeExecution>> saveBatch,
        IProgress<int>? progress,
        CancellationToken cancellationToken)
    {
        var totalDays = Math.Max(1, (int)Math.Ceiling((to - from).TotalDays));
        var completedDays = 0;
        var savedCount = 0;
        var start = from;
        while (start < to)
        {
            var end = start.AddDays(7) < to ? start.AddDays(7) : to;
            var trades = new List<TradeExecution>();
            await FetchFuturesWindowAsync(connectionId, start, end, trades, 0, cancellationToken);
            if (trades.Count > 0)
            {
                saveBatch(trades);
                savedCount += trades.Count;
            }
            completedDays = Math.Min(totalDays, completedDays + 7);
            progress?.Report(completedDays * 100 / totalDays);
            start = end.AddMilliseconds(1);
        }
        return savedCount;
    }

    private async Task FetchFuturesWindowAsync(
        int connectionId,
        DateTime from,
        DateTime to,
        List<TradeExecution> trades,
        int depth,
        CancellationToken cancellationToken)
    {
        var parameters = $"startTime={new DateTimeOffset(from).ToUnixTimeMilliseconds()}" +
                         $"&endTime={new DateTimeOffset(to).ToUnixTimeMilliseconds()}" +
                         "&limit=1000";
        using var document = await GetSignedJsonAsync("/fapi/v1/userTrades", parameters, cancellationToken);
        var rows = document.RootElement.EnumerateArray().ToList();
        if (rows.Count < 1000)
        {
            trades.AddRange(rows.Select(row => ParseFuturesTrade(connectionId, row)));
            return;
        }

        if (depth >= 18 || (to - from).TotalMilliseconds < 2)
        {
            throw new InvalidOperationException(
                "Найдено не менее 1000 фьючерсных исполнений в очень коротком интервале. " +
                "Синхронизация остановлена, чтобы не пропустить сделки.");
        }

        var midpoint = from.AddTicks((to - from).Ticks / 2);
        await FetchFuturesWindowAsync(connectionId, from, midpoint, trades, depth + 1, cancellationToken);
        await FetchFuturesWindowAsync(
            connectionId, midpoint.AddMilliseconds(1), to, trades, depth + 1, cancellationToken);
    }

    private async Task<JsonDocument> GetSignedJsonAsync(
        string endpoint,
        string query,
        CancellationToken cancellationToken)
    {
        var signedQuery = string.IsNullOrEmpty(query) ? "" : query + "&";
        signedQuery += $"timestamp={DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}&recvWindow=10000";
        var keyBytes = Encoding.UTF8.GetBytes(_apiSecret);
        var queryBytes = Encoding.UTF8.GetBytes(signedQuery);
        string signature;
        try
        {
            signature = Convert.ToHexStringLower(HMACSHA256.HashData(keyBytes, queryBytes));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(keyBytes);
            CryptographicOperations.ZeroMemory(queryBytes);
        }
        var url = $"{_restBase}{endpoint}?{signedQuery}&signature={signature}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("X-MBX-APIKEY", _apiKey);
        return await SendJsonAsync(request, cancellationToken);
    }

    private async Task<JsonDocument> GetPublicJsonAsync(string endpoint, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{_restBase}{endpoint}");
        return await SendJsonAsync(request, cancellationToken);
    }

    private async Task<JsonDocument> SendJsonAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        await _requestThrottle.WaitAsync(cancellationToken);
        try
        {
            var delay = _nextRequestUtc - DateTime.UtcNow;
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, cancellationToken);
            }
            _nextRequestUtc = DateTime.UtcNow.AddMilliseconds(250);

            using var response = await HttpClient.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw FormatApiError(response.StatusCode, body);
            }

            try
            {
                return JsonDocument.Parse(body);
            }
            catch (JsonException exception)
            {
                throw new InvalidOperationException("Binance вернула ответ в неожиданном формате.", exception);
            }
        }
        finally
        {
            _requestThrottle.Release();
        }
    }

    private static Exception FormatApiError(HttpStatusCode statusCode, string body)
    {
        try
        {
            using var error = JsonDocument.Parse(body);
            var code = error.RootElement.TryGetProperty("code", out var codeValue)
                ? codeValue.GetInt32().ToString(CultureInfo.InvariantCulture)
                : ((int)statusCode).ToString(CultureInfo.InvariantCulture);
            var message = error.RootElement.TryGetProperty("msg", out var messageValue)
                ? messageValue.GetString()
                : "неизвестная ошибка API";
            return new InvalidOperationException($"Binance API ({code}): {message}");
        }
        catch (JsonException)
        {
            return new InvalidOperationException($"Binance API вернула HTTP {(int)statusCode}.");
        }
    }

    private static TradeExecution ParseSpotTrade(
        int connectionId,
        string symbol,
        string baseAsset,
        string quoteAsset,
        JsonElement row)
    {
        var quantity = ParseDecimal(row.GetProperty("qty"));
        var price = ParseDecimal(row.GetProperty("price"));
        return new TradeExecution
        {
            ConnectionId = connectionId,
            ExchangeTradeId = row.GetProperty("id").ToString(),
            Symbol = symbol,
            BaseAsset = baseAsset,
            QuoteAsset = quoteAsset,
            Side = row.GetProperty("isBuyer").GetBoolean() ? "BUY" : "SELL",
            PositionSide = "",
            Market = "spot",
            Price = price,
            Quantity = quantity,
            QuoteQuantity = row.TryGetProperty("quoteQty", out var quoteQty)
                ? ParseDecimal(quoteQty)
                : quantity * price,
            Commission = ParseDecimal(row.GetProperty("commission")),
            CommissionAsset = row.GetProperty("commissionAsset").GetString() ?? "",
            RealizedPnl = 0,
            ExecutedAtUtc = DateTimeOffset.FromUnixTimeMilliseconds(
                row.GetProperty("time").GetInt64()).UtcDateTime
        };
    }

    private static TradeExecution ParseFuturesTrade(int connectionId, JsonElement row)
    {
        var symbol = row.GetProperty("symbol").GetString() ?? "";
        var quantity = ParseDecimal(row.GetProperty("qty"));
        var price = ParseDecimal(row.GetProperty("price"));
        return new TradeExecution
        {
            ConnectionId = connectionId,
            ExchangeTradeId = row.GetProperty("id").ToString(),
            Symbol = symbol,
            BaseAsset = FindBaseAsset(symbol),
            QuoteAsset = "USDT",
            Side = row.GetProperty("side").GetString() ?? "",
            PositionSide = row.TryGetProperty("positionSide", out var positionSide)
                ? positionSide.GetString() ?? "BOTH"
                : "BOTH",
            Market = "futures",
            Price = price,
            Quantity = quantity,
            QuoteQuantity = row.TryGetProperty("quoteQty", out var quoteQty)
                ? ParseDecimal(quoteQty)
                : quantity * price,
            Commission = ParseDecimal(row.GetProperty("commission")),
            CommissionAsset = row.GetProperty("commissionAsset").GetString() ?? "",
            RealizedPnl = ParseDecimal(row.GetProperty("realizedPnl")),
            ExecutedAtUtc = DateTimeOffset.FromUnixTimeMilliseconds(
                row.GetProperty("time").GetInt64()).UtcDateTime
        };
    }

    private static string FindBaseAsset(string symbol)
    {
        foreach (var suffix in new[] { "USDT", "USDC", "FDUSD", "BUSD", "TUSD" })
        {
            if (symbol.EndsWith(suffix, StringComparison.Ordinal))
            {
                return symbol[..^suffix.Length];
            }
        }
        return symbol;
    }

    private static decimal ParseDecimal(JsonElement value) =>
        decimal.Parse(value.GetString() ?? value.GetRawText(), CultureInfo.InvariantCulture);

    public void Dispose() => _requestThrottle.Dispose();
}
