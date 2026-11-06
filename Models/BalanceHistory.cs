namespace DnevnikSdelok.Models;

public sealed record BalanceSnapshot(int ConnectionId, DateTime CapturedAtUtc, decimal Balance);

public sealed record BalanceChange(
    int ConnectionId,
    string EventId,
    DateTime ChangedAtUtc,
    decimal Amount,
    string Asset,
    string Type);
