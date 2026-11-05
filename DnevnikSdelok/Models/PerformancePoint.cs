namespace DnevnikSdelok.Models;

public sealed record PerformancePoint(int ConnectionId, DateTime DateUtc, decimal? NetPnl);
