using DnevnikSdelok.Models;
using DnevnikSdelok.Services;
using Microsoft.Data.Sqlite;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace DnevnikSdelok.Data;

public sealed class JournalDatabase
{
    private readonly string _connectionString;
    private readonly string _applicationDataDirectory;

    public string DatabasePath { get; }
    public string AttachmentsDirectory { get; }

    public JournalDatabase(string? dataDirectory = null)
    {
        var directory = dataDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DnevnikSdelok");
        Directory.CreateDirectory(directory);
        _applicationDataDirectory = directory;
        AttachmentsDirectory = Path.Combine(directory, "attachments");
        Directory.CreateDirectory(AttachmentsDirectory);
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
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys = ON;";
        command.ExecuteNonQuery();
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
                CurrentBalance TEXT NULL,
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
            CREATE TABLE IF NOT EXISTS ExecutionNotes (
                ConnectionId INTEGER NOT NULL,
                Symbol TEXT NOT NULL,
                ExchangeTradeId TEXT NOT NULL,
                Category TEXT NOT NULL DEFAULT '',
                EntryReason TEXT NOT NULL DEFAULT '',
                ExitReason TEXT NOT NULL DEFAULT '',
                AdditionalInfo TEXT NOT NULL DEFAULT '',
                CryptoScreener TEXT NOT NULL DEFAULT '',
                Rating TEXT NOT NULL DEFAULT '',
                JournalDescription TEXT NOT NULL DEFAULT '',
                JournalConclusion TEXT NOT NULL DEFAULT '',
                VideoUrl TEXT NOT NULL DEFAULT '',
                ScreenshotRelativePath TEXT NOT NULL DEFAULT '',
                ChartDrawingsJson TEXT NOT NULL DEFAULT '[]',
                PRIMARY KEY (ConnectionId, Symbol, ExchangeTradeId),
                FOREIGN KEY (ConnectionId, Symbol, ExchangeTradeId)
                    REFERENCES Executions(ConnectionId, Symbol, ExchangeTradeId)
                    ON DELETE CASCADE
            );
            CREATE TABLE IF NOT EXISTS AppSettings (
                SettingKey TEXT PRIMARY KEY,
                SettingValue TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS AccountBalanceSnapshots (
                ConnectionId INTEGER NOT NULL,
                CapturedAtUtc TEXT NOT NULL,
                Balance TEXT NOT NULL,
                PRIMARY KEY (ConnectionId, CapturedAtUtc),
                FOREIGN KEY (ConnectionId) REFERENCES ApiConnections(Id) ON DELETE CASCADE
            );
            CREATE TABLE IF NOT EXISTS AccountBalanceChanges (
                ConnectionId INTEGER NOT NULL,
                EventId TEXT NOT NULL,
                ChangedAtUtc TEXT NOT NULL,
                Amount TEXT NOT NULL,
                Asset TEXT NOT NULL,
                ChangeType TEXT NOT NULL,
                PRIMARY KEY (ConnectionId, EventId),
                FOREIGN KEY (ConnectionId) REFERENCES ApiConnections(Id) ON DELETE CASCADE
            );
            CREATE INDEX IF NOT EXISTS IX_AccountBalanceChanges_ChangedAtUtc
                ON AccountBalanceChanges(ConnectionId, ChangedAtUtc);
            CREATE TABLE IF NOT EXISTS AccountBalanceHistoryStatus (
                ConnectionId INTEGER PRIMARY KEY,
                FetchedThroughUtc TEXT NOT NULL,
                FOREIGN KEY (ConnectionId) REFERENCES ApiConnections(Id) ON DELETE CASCADE
            );
            """;
        command.ExecuteNonQuery();
        EnsureConnectionBalanceColumn(connection);
        EnsureExecutionNoteColumns(connection);
    }

    private static void EnsureExecutionNoteColumns(SqliteConnection connection)
    {
        var existing = new HashSet<string>(StringComparer.Ordinal);
        using (var check = connection.CreateCommand())
        {
            check.CommandText = "PRAGMA table_info(ExecutionNotes);";
            using var reader = check.ExecuteReader();
            while (reader.Read())
            {
                existing.Add(reader.GetString(1));
            }
        }

        var migrations = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["JournalDescription"] = "TEXT NOT NULL DEFAULT ''",
            ["JournalConclusion"] = "TEXT NOT NULL DEFAULT ''",
            ["VideoUrl"] = "TEXT NOT NULL DEFAULT ''",
            ["ScreenshotRelativePath"] = "TEXT NOT NULL DEFAULT ''",
            ["ChartDrawingsJson"] = "TEXT NOT NULL DEFAULT '[]'"
        };
        foreach (var (column, definition) in migrations)
        {
            if (existing.Contains(column))
            {
                continue;
            }

            using var alter = connection.CreateCommand();
            alter.CommandText = $"ALTER TABLE ExecutionNotes ADD COLUMN {column} {definition};";
            alter.ExecuteNonQuery();
        }
    }

    private static void EnsureConnectionBalanceColumn(SqliteConnection connection)
    {
        using var check = connection.CreateCommand();
        check.CommandText = "PRAGMA table_info(ApiConnections);";
        using var reader = check.ExecuteReader();
        var exists = false;
        while (reader.Read())
        {
            if (reader.GetString(1) == "CurrentBalance")
            {
                exists = true;
                break;
            }
        }
        reader.Close();
        if (exists)
        {
            return;
        }

        using var alter = connection.CreateCommand();
        alter.CommandText = "ALTER TABLE ApiConnections ADD COLUMN CurrentBalance TEXT NULL;";
        alter.ExecuteNonQuery();
    }

    public DashboardPreferences GetDashboardPreferences()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT SettingValue FROM AppSettings WHERE SettingKey = 'dashboard';";
        var value = command.ExecuteScalar() as string;
        DashboardPreferences preferences;
        try
        {
            preferences = string.IsNullOrWhiteSpace(value)
                ? DashboardPreferences.CreateDefault()
                : JsonSerializer.Deserialize<DashboardPreferences>(value) ?? DashboardPreferences.CreateDefault();
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Настройки интерфейса повреждены и не могут быть прочитаны.", exception);
        }
        preferences.EnsureCatalog();
        preferences.MigrateLegacyColors();
        return preferences;
    }

    public void SaveDashboardPreferences(DashboardPreferences preferences)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO AppSettings (SettingKey, SettingValue)
            VALUES ('dashboard', $value)
            ON CONFLICT(SettingKey) DO UPDATE SET SettingValue = excluded.SettingValue;
            """;
        command.Parameters.AddWithValue("$value", JsonSerializer.Serialize(preferences));
        command.ExecuteNonQuery();
    }

    public List<ApiConnection> GetConnections()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, Name, Market, ProtectedApiKey, ProtectedApiSecret,
                   LastSyncedUtc, LastStatus, CurrentBalance
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

    public Dictionary<(int ConnectionId, string Symbol, string TradeId), ExecutionNote> GetExecutionNotes()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT ConnectionId, Symbol, ExchangeTradeId, Category, EntryReason,
                   ExitReason, AdditionalInfo, CryptoScreener, Rating,
                   JournalDescription, JournalConclusion, VideoUrl,
                   ScreenshotRelativePath, ChartDrawingsJson
            FROM ExecutionNotes;
            """;
        using var reader = command.ExecuteReader();
        var result = new Dictionary<(int, string, string), ExecutionNote>();
        while (reader.Read())
        {
            var note = new ExecutionNote
            {
                Category = reader.GetString(3),
                EntryReason = reader.GetString(4),
                ExitReason = reader.GetString(5),
                AdditionalInfo = reader.GetString(6),
                CryptoScreener = reader.GetString(7),
                Rating = reader.GetString(8),
                JournalDescription = reader.GetString(9),
                JournalConclusion = reader.GetString(10),
                VideoUrl = reader.GetString(11),
                ScreenshotRelativePath = reader.GetString(12),
                ChartDrawingsJson = reader.GetString(13)
            };
            result[(reader.GetInt32(0), reader.GetString(1), reader.GetString(2))] = note;
        }
        return result;
    }

    public void SaveExecutionNote(ExecutionRow row)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO ExecutionNotes (
                ConnectionId, Symbol, ExchangeTradeId, Category, EntryReason,
                ExitReason, AdditionalInfo, CryptoScreener, Rating,
                JournalDescription, JournalConclusion, VideoUrl,
                ScreenshotRelativePath, ChartDrawingsJson
            ) VALUES (
                $connectionId, $symbol, $tradeId, $category, $entryReason,
                $exitReason, $additionalInfo, $cryptoScreener, $rating,
                $journalDescription, $journalConclusion, $videoUrl,
                $screenshotRelativePath, $chartDrawingsJson
            )
            ON CONFLICT(ConnectionId, Symbol, ExchangeTradeId) DO UPDATE SET
                Category = excluded.Category,
                EntryReason = excluded.EntryReason,
                ExitReason = excluded.ExitReason,
                AdditionalInfo = excluded.AdditionalInfo,
                CryptoScreener = excluded.CryptoScreener,
                Rating = excluded.Rating,
                JournalDescription = excluded.JournalDescription,
                JournalConclusion = excluded.JournalConclusion,
                VideoUrl = excluded.VideoUrl,
                ScreenshotRelativePath = excluded.ScreenshotRelativePath,
                ChartDrawingsJson = excluded.ChartDrawingsJson;
            """;
        command.Parameters.AddWithValue("$connectionId", row.ConnectionId);
        command.Parameters.AddWithValue("$symbol", row.Symbol);
        command.Parameters.AddWithValue("$tradeId", row.Execution.ExchangeTradeId);
        command.Parameters.AddWithValue("$category", row.Category);
        command.Parameters.AddWithValue("$entryReason", row.EntryReason);
        command.Parameters.AddWithValue("$exitReason", row.ExitReason);
        command.Parameters.AddWithValue("$additionalInfo", row.AdditionalInfo);
        command.Parameters.AddWithValue("$cryptoScreener", row.CryptoScreener);
        command.Parameters.AddWithValue("$rating", row.Rating);
        command.Parameters.AddWithValue("$journalDescription", row.JournalDescription);
        command.Parameters.AddWithValue("$journalConclusion", row.JournalConclusion);
        command.Parameters.AddWithValue("$videoUrl", row.VideoUrl);
        command.Parameters.AddWithValue("$screenshotRelativePath", row.ScreenshotRelativePath);
        command.Parameters.AddWithValue("$chartDrawingsJson", row.ChartDrawingsJson);
        command.ExecuteNonQuery();
    }

    public string CopyTradeImage(string sourcePath)
    {
        var extension = Path.GetExtension(sourcePath).ToLowerInvariant();
        if (extension is not (".png" or ".jpg" or ".jpeg"))
        {
            throw new InvalidDataException("Допустимы только изображения PNG или JPG.");
        }
        var info = new FileInfo(sourcePath);
        if (!info.Exists || info.Length is <= 0 or > 3 * 1024 * 1024)
        {
            throw new InvalidDataException("Размер изображения должен быть от 1 байта до 3 МБ.");
        }

        var fileName = $"{Guid.NewGuid():N}{extension}";
        var destination = Path.Combine(AttachmentsDirectory, fileName);
        File.Copy(sourcePath, destination, overwrite: false);
        return Path.Combine("attachments", fileName);
    }

    public string? GetTradeImagePath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return null;
        }

        var fullPath = Path.GetFullPath(Path.Combine(_applicationDataDirectory, relativePath));
        var attachmentRoot = Path.GetFullPath(AttachmentsDirectory) + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(attachmentRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Путь к изображению сделки выходит за папку вложений.");
        }
        return File.Exists(fullPath) ? fullPath : null;
    }

    public ApiConnection? GetConnection(int id)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, Name, Market, ProtectedApiKey, ProtectedApiSecret,
                   LastSyncedUtc, LastStatus, CurrentBalance
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
            LastStatus = reader.IsDBNull(6) ? null : reader.GetString(6),
            CurrentBalance = reader.IsDBNull(7) ? null : Parse(reader.GetString(7))
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

    public void UpdateConnectionStatus(int id, DateTime? syncedUtc, string status, decimal? currentBalance = null)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE ApiConnections
            SET LastSyncedUtc = COALESCE($synced, LastSyncedUtc),
                LastStatus = $status,
                CurrentBalance = COALESCE($balance, CurrentBalance)
            WHERE Id = $id;
            """;
        command.Parameters.AddWithValue(
            "$synced",
            syncedUtc?.ToString("O", CultureInfo.InvariantCulture) ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue(
            "$balance",
            currentBalance.HasValue ? Format(currentBalance.Value) : (object)DBNull.Value);
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    public void SaveBalanceSnapshots(IEnumerable<BalanceSnapshot> snapshots)
    {
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO AccountBalanceSnapshots (ConnectionId, CapturedAtUtc, Balance)
            VALUES ($connectionId, $capturedAtUtc, $balance)
            ON CONFLICT(ConnectionId, CapturedAtUtc) DO UPDATE SET Balance = excluded.Balance;
            """;
        var connectionId = command.Parameters.Add("$connectionId", SqliteType.Integer);
        var capturedAtUtc = command.Parameters.Add("$capturedAtUtc", SqliteType.Text);
        var balance = command.Parameters.Add("$balance", SqliteType.Text);
        foreach (var snapshot in snapshots)
        {
            connectionId.Value = snapshot.ConnectionId;
            capturedAtUtc.Value = snapshot.CapturedAtUtc.ToString("O", CultureInfo.InvariantCulture);
            balance.Value = Format(snapshot.Balance);
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    public List<BalanceSnapshot> GetBalanceSnapshots(int connectionId)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT CapturedAtUtc, Balance
            FROM AccountBalanceSnapshots
            WHERE ConnectionId = $connectionId
            ORDER BY CapturedAtUtc;
            """;
        command.Parameters.AddWithValue("$connectionId", connectionId);
        using var reader = command.ExecuteReader();
        var snapshots = new List<BalanceSnapshot>();
        while (reader.Read())
        {
            snapshots.Add(new BalanceSnapshot(
                connectionId,
                DateTime.Parse(
                    reader.GetString(0),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind),
                Parse(reader.GetString(1))));
        }
        return snapshots;
    }

    public void SaveBalanceChanges(IEnumerable<BalanceChange> changes)
    {
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO AccountBalanceChanges (
                ConnectionId, EventId, ChangedAtUtc, Amount, Asset, ChangeType
            ) VALUES (
                $connectionId, $eventId, $changedAtUtc, $amount, $asset, $changeType
            )
            ON CONFLICT(ConnectionId, EventId) DO UPDATE SET
                ChangedAtUtc = excluded.ChangedAtUtc,
                Amount = excluded.Amount,
                Asset = excluded.Asset,
                ChangeType = excluded.ChangeType;
            """;
        var connectionId = command.Parameters.Add("$connectionId", SqliteType.Integer);
        var eventId = command.Parameters.Add("$eventId", SqliteType.Text);
        var changedAtUtc = command.Parameters.Add("$changedAtUtc", SqliteType.Text);
        var amount = command.Parameters.Add("$amount", SqliteType.Text);
        var asset = command.Parameters.Add("$asset", SqliteType.Text);
        var changeType = command.Parameters.Add("$changeType", SqliteType.Text);
        foreach (var change in changes)
        {
            connectionId.Value = change.ConnectionId;
            eventId.Value = change.EventId;
            changedAtUtc.Value = change.ChangedAtUtc.ToString("O", CultureInfo.InvariantCulture);
            amount.Value = Format(change.Amount);
            asset.Value = change.Asset;
            changeType.Value = change.Type;
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    public List<BalanceChange> GetBalanceChanges(int connectionId)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT EventId, ChangedAtUtc, Amount, Asset, ChangeType
            FROM AccountBalanceChanges
            WHERE ConnectionId = $connectionId
            ORDER BY ChangedAtUtc;
            """;
        command.Parameters.AddWithValue("$connectionId", connectionId);
        using var reader = command.ExecuteReader();
        var changes = new List<BalanceChange>();
        while (reader.Read())
        {
            changes.Add(new BalanceChange(
                connectionId,
                reader.GetString(0),
                DateTime.Parse(
                    reader.GetString(1),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind),
                Parse(reader.GetString(2)),
                reader.GetString(3),
                reader.GetString(4)));
        }
        return changes;
    }

    public DateTime? GetBalanceHistoryFetchedThroughUtc(int connectionId)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT FetchedThroughUtc
            FROM AccountBalanceHistoryStatus
            WHERE ConnectionId = $connectionId;
            """;
        command.Parameters.AddWithValue("$connectionId", connectionId);
        var value = command.ExecuteScalar() as string;
        return value is null
            ? null
            : DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    }

    public void UpdateBalanceHistoryFetchedThroughUtc(int connectionId, DateTime fetchedThroughUtc)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO AccountBalanceHistoryStatus (ConnectionId, FetchedThroughUtc)
            VALUES ($connectionId, $fetchedThroughUtc)
            ON CONFLICT(ConnectionId) DO UPDATE SET
                FetchedThroughUtc = excluded.FetchedThroughUtc;
            """;
        command.Parameters.AddWithValue("$connectionId", connectionId);
        command.Parameters.AddWithValue(
            "$fetchedThroughUtc",
            fetchedThroughUtc.ToString("O", CultureInfo.InvariantCulture));
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
        var notes = GetExecutionNotes();
        var connections = GetConnections();
        var connectionNames = connections.ToDictionary(item => item.Id, item => item.Name);
        var balanceAtEntry = DepositExposureCalculator.Calculate(
            executions,
            connections,
            connections.SelectMany(item => GetBalanceSnapshots(item.Id)).ToList(),
            connections.SelectMany(item => GetBalanceChanges(item.Id)).ToList());
        var rowsByKey = PerformanceCalculator.Calculate(executions, connectionNames, balanceAtEntry)
            .Rows
            .ToDictionary(
                item => (item.ConnectionId, item.Symbol, item.Execution.ExchangeTradeId));
        using var writer = new StreamWriter(filePath, false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        writer.WriteLine("ConnectionName,Market,TradeId,Symbol,BaseAsset,QuoteAsset,Side,PositionSide,Price,Quantity,QuoteQuantity,Commission,CommissionAsset,RealizedPnl,ExecutedAtUtc,PositionEntryAtUtc,TimeInDealSeconds,ExposureFromDeposit,Category,EntryReason,ExitReason,AdditionalInfo,CryptoScreener,Rating,JournalDescription,JournalConclusion,VideoUrl,ScreenshotRelativePath");
        foreach (var item in executions)
        {
            notes.TryGetValue(
                (item.ConnectionId, item.Symbol, item.ExchangeTradeId),
                out var note);
            var calculatedRow = rowsByKey[(item.ConnectionId, item.Symbol, item.ExchangeTradeId)];
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
                Csv(item.ExecutedAtUtc.ToString("O", CultureInfo.InvariantCulture)),
                Csv(calculatedRow.EntryTimeUtc?.ToString("O", CultureInfo.InvariantCulture) ?? ""),
                Csv(calculatedRow.TimeInDeal is { } duration
                    ? duration.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture)
                    : ""),
                Csv(calculatedRow.ExposureMultiple is { } exposure ? Format(exposure) : ""),
                Csv(note?.Category ?? ""),
                Csv(note?.EntryReason ?? ""),
                Csv(note?.ExitReason ?? ""),
                Csv(note?.AdditionalInfo ?? ""),
                Csv(note?.CryptoScreener ?? ""),
                Csv(note?.Rating ?? ""),
                Csv(note?.JournalDescription ?? ""),
                Csv(note?.JournalConclusion ?? ""),
                Csv(note?.VideoUrl ?? ""),
                Csv(note?.ScreenshotRelativePath ?? "")
            }));
        }
    }

    public void BackupTo(string filePath)
    {
        var temporaryDatabase = Path.Combine(
            Path.GetTempPath(),
            $"DnevnikSdelok-{Guid.NewGuid():N}.sqlite3");
        try
        {
            using (var source = OpenConnection())
            using (var destination = new SqliteConnection(
                new SqliteConnectionStringBuilder
                {
                    DataSource = temporaryDatabase,
                    Mode = SqliteOpenMode.ReadWriteCreate,
                    Pooling = false
                }.ToString()))
            {
                destination.Open();
                source.BackupDatabase(destination);
            }

            using var archive = ZipFile.Open(filePath, ZipArchiveMode.Create);
            archive.CreateEntryFromFile(temporaryDatabase, "journal.sqlite3", CompressionLevel.Optimal);
            foreach (var attachment in Directory.EnumerateFiles(AttachmentsDirectory))
            {
                archive.CreateEntryFromFile(
                    attachment,
                    $"attachments/{Path.GetFileName(attachment)}",
                    CompressionLevel.Optimal);
            }
        }
        finally
        {
            if (File.Exists(temporaryDatabase))
            {
                File.Delete(temporaryDatabase);
            }
        }
    }

    private static string Csv(string value) =>
        "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    private static string Format(decimal value) => value.ToString(CultureInfo.InvariantCulture);

    private static decimal Parse(string value) =>
        decimal.Parse(value, NumberStyles.Number, CultureInfo.InvariantCulture);
}
