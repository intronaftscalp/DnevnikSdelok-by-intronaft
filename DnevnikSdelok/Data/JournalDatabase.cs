using DnevnikSdelok.Models;
using Microsoft.Data.Sqlite;
using System.Globalization;
using System.IO;
using System.Text;

namespace DnevnikSdelok.Data;

public sealed class JournalDatabase
{
    private readonly string _connectionString;

    public string DatabasePath { get; }

    public JournalDatabase()
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DnevnikSdelok");
        Directory.CreateDirectory(directory);
        DatabasePath = Path.Combine(directory, "journal.sqlite3");
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString();
        Initialize();
    }

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    private void Initialize()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode = WAL;
            PRAGMA foreign_keys = ON;
            CREATE TABLE IF NOT EXISTS ApiConnections (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Name TEXT NOT NULL,
                Market TEXT NOT NULL CHECK (Market IN ('spot', 'futures')),
                ProtectedApiKey BLOB NOT NULL,
                ProtectedApiSecret BLOB NOT NULL,
                LastSyncedUtc TEXT NULL,
                LastStatus TEXT NULL,
                UNIQUE(Name, Market)
            );
            CREATE TABLE IF NOT EXISTS Executions (
                ConnectionId INTEGER NOT NULL,
                ExchangeTradeId TEXT NOT NULL,
                Symbol TEXT NOT NULL,
                BaseAsset TEXT NOT NULL,
                QuoteAsset TEXT NOT NULL,
                Side TEXT NOT NULL,
                PositionSide TEXT NOT NULL,
                Market TEXT NOT NULL,
                Price TEXT NOT NULL,
                Quantity TEXT NOT NULL,
                QuoteQuantity TEXT NOT NULL,
                Commission TEXT NOT NULL,
                CommissionAsset TEXT NOT NULL,
                RealizedPnl TEXT NOT NULL,
                ExecutedAtUtc TEXT NOT NULL,
                PRIMARY KEY (ConnectionId, Symbol, ExchangeTradeId),
                FOREIGN KEY (ConnectionId) REFERENCES ApiConnections(Id) ON DELETE CASCADE
            );
            CREATE INDEX IF NOT EXISTS IX_Executions_ExecutedAtUtc
                ON Executions(ExecutedAtUtc DESC);
            """;
        command.ExecuteNonQuery();
    }

    public List<ApiConnection> GetConnections()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, Name, Market, ProtectedApiKey, ProtectedApiSecret,
                   LastSyncedUtc, LastStatus
            FROM ApiConnections
            ORDER BY Id;
            """;
        using var reader = command.ExecuteReader();
        var result = new List<ApiConnection>();
        while (reader.Read())
        {
            result.Add(ReadConnection(reader));
        }
        return result;
    }

    public ApiConnection? GetConnection(int id)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, Name, Market, ProtectedApiKey, ProtectedApiSecret,
                   LastSyncedUtc, LastStatus
            FROM ApiConnections
            WHERE Id = $id;
            """;
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadConnection(reader) : null;
    }

    private static ApiConnection ReadConnection(SqliteDataReader reader)
    {
        return new ApiConnection
        {
            Id = reader.GetInt32(0),
            Name = reader.GetString(1),
            Market = reader.GetString(2),
            ProtectedApiKey = (byte[])reader["ProtectedApiKey"],
            ProtectedApiSecret = (byte[])reader["ProtectedApiSecret"],
            LastSyncedUtc = reader.IsDBNull(5)
                ? null
                : DateTime.Parse(
                    reader.GetString(5),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind),
            LastStatus = reader.IsDBNull(6) ? null : reader.GetString(6)
        };
    }

    public void AddConnection(ApiConnection connection)
    {
        using var db = OpenConnection();
        using var command = db.CreateCommand();
        command.CommandText = """
            INSERT INTO ApiConnections (Name, Market, ProtectedApiKey, ProtectedApiSecret)
            VALUES ($name, $market, $apiKey, $apiSecret);
            """;
        command.Parameters.AddWithValue("$name", connection.Name);
        command.Parameters.AddWithValue("$market", connection.Market);
        command.Parameters.AddWithValue("$apiKey", connection.ProtectedApiKey);
        command.Parameters.AddWithValue("$apiSecret", connection.ProtectedApiSecret);
        command.ExecuteNonQuery();
    }

    public void UpdateConnectionStatus(int id, DateTime? syncedUtc, string status)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE ApiConnections
            SET LastSyncedUtc = COALESCE($synced, LastSyncedUtc), LastStatus = $status
            WHERE Id = $id;
            """;
        command.Parameters.AddWithValue(
            "$synced",
            syncedUtc?.ToString("O", CultureInfo.InvariantCulture) ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    public void SaveExecutions(IEnumerable<TradeExecution> executions)
    {
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO Executions (
                ConnectionId, ExchangeTradeId, Symbol, BaseAsset, QuoteAsset, Side,
                PositionSide, Market, Price, Quantity, QuoteQuantity, Commission,
                CommissionAsset, RealizedPnl, ExecutedAtUtc
            ) VALUES (
                $connectionId, $tradeId, $symbol, $baseAsset, $quoteAsset, $side,
                $positionSide, $market, $price, $quantity, $quoteQuantity, $commission,
                $commissionAsset, $realizedPnl, $executedAt
            )
            ON CONFLICT(ConnectionId, Symbol, ExchangeTradeId) DO NOTHING;
            """;

        var parameters = new Dictionary<string, SqliteParameter>();
        foreach (var name in new[]
        {
            "$connectionId", "$tradeId", "$symbol", "$baseAsset", "$quoteAsset", "$side",
            "$positionSide", "$market", "$price", "$quantity", "$quoteQuantity",
            "$commission", "$commissionAsset", "$realizedPnl", "$executedAt"
        })
        {
            parameters[name] = command.Parameters.Add(name, SqliteType.Text);
        }

        foreach (var trade in executions)
        {
            parameters["$connectionId"].Value = trade.ConnectionId;
            parameters["$tradeId"].Value = trade.ExchangeTradeId;
            parameters["$symbol"].Value = trade.Symbol;
            parameters["$baseAsset"].Value = trade.BaseAsset;
            parameters["$quoteAsset"].Value = trade.QuoteAsset;
            parameters["$side"].Value = trade.Side;
            parameters["$positionSide"].Value = trade.PositionSide;
            parameters["$market"].Value = trade.Market;
            parameters["$price"].Value = Format(trade.Price);
            parameters["$quantity"].Value = Format(trade.Quantity);
            parameters["$quoteQuantity"].Value = Format(trade.QuoteQuantity);
            parameters["$commission"].Value = Format(trade.Commission);
            parameters["$commissionAsset"].Value = trade.CommissionAsset;
            parameters["$realizedPnl"].Value = Format(trade.RealizedPnl);
            parameters["$executedAt"].Value = trade.ExecutedAtUtc.ToString("O", CultureInfo.InvariantCulture);
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    public List<TradeExecution> GetExecutions(int limit = 100_000)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT ConnectionId, ExchangeTradeId, Symbol, BaseAsset, QuoteAsset,
                   Side, PositionSide, Market, Price, Quantity, QuoteQuantity,
                   Commission, CommissionAsset, RealizedPnl, ExecutedAtUtc
            FROM Executions
            ORDER BY ExecutedAtUtc DESC, ExchangeTradeId DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", limit);
        using var reader = command.ExecuteReader();
        var result = new List<TradeExecution>();
        while (reader.Read())
        {
            result.Add(new TradeExecution
            {
                ConnectionId = reader.GetInt32(0),
                ExchangeTradeId = reader.GetString(1),
                Symbol = reader.GetString(2),
                BaseAsset = reader.GetString(3),
                QuoteAsset = reader.GetString(4),
                Side = reader.GetString(5),
                PositionSide = reader.GetString(6),
                Market = reader.GetString(7),
                Price = Parse(reader.GetString(8)),
                Quantity = Parse(reader.GetString(9)),
                QuoteQuantity = Parse(reader.GetString(10)),
                Commission = Parse(reader.GetString(11)),
                CommissionAsset = reader.GetString(12),
                RealizedPnl = Parse(reader.GetString(13)),
                ExecutedAtUtc = DateTime.Parse(
                    reader.GetString(14),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind)
            });
        }
        return result;
    }

    public void DeleteConnection(int id)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM ApiConnections WHERE Id = $id;";
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    public void ExportCsv(string filePath)
    {
        var executions = GetExecutions(int.MaxValue);
        var connectionNames = GetConnections().ToDictionary(item => item.Id, item => item.Name);
        using var writer = new StreamWriter(filePath, false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        writer.WriteLine("ConnectionName,Market,TradeId,Symbol,BaseAsset,QuoteAsset,Side,PositionSide,Price,Quantity,QuoteQuantity,Commission,CommissionAsset,RealizedPnl,ExecutedAtUtc");
        foreach (var item in executions)
        {
            writer.WriteLine(string.Join(",", new[]
            {
                Csv(connectionNames.GetValueOrDefault(item.ConnectionId, "Binance")),
                Csv(item.Market),
                Csv(item.ExchangeTradeId),
                Csv(item.Symbol),
                Csv(item.BaseAsset),
                Csv(item.QuoteAsset),
                Csv(item.Side),
                Csv(item.PositionSide),
                Csv(Format(item.Price)),
                Csv(Format(item.Quantity)),
                Csv(Format(item.QuoteQuantity)),
                Csv(Format(item.Commission)),
                Csv(item.CommissionAsset),
                Csv(Format(item.RealizedPnl)),
                Csv(item.ExecutedAtUtc.ToString("O", CultureInfo.InvariantCulture))
            }));
        }
    }

    public void BackupTo(string filePath)
    {
        using var source = OpenConnection();
        using var destination = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = filePath,
                Mode = SqliteOpenMode.ReadWriteCreate
            }.ToString());
        destination.Open();
        source.BackupDatabase(destination);
    }

    private static string Csv(string value) =>
        "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    private static string Format(decimal value) => value.ToString(CultureInfo.InvariantCulture);

    private static decimal Parse(string value) =>
        decimal.Parse(value, NumberStyles.Number, CultureInfo.InvariantCulture);
}
