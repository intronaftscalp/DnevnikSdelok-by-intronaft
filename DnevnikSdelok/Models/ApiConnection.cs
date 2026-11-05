namespace DnevnikSdelok.Models;

public sealed class ApiConnection
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Market { get; set; } = "";
    public byte[] ProtectedApiKey { get; set; } = [];
    public byte[] ProtectedApiSecret { get; set; } = [];
    public DateTime? LastSyncedUtc { get; set; }
    public string? LastStatus { get; set; }

    public string DisplayName => $"{Name} · Binance · {Market}";
}
