using Microsoft.Data.Sqlite;

namespace VibeRe.Client.Infrastructure.Adapters;

// Client-owned storage: opening/managing servers never requires a running VibeRe server.
public sealed class ClientConnectionDatabase(string path) : IClientConfigurationStore
{
    public string FilePath { get; } = Path.GetFullPath(path);
    public static string DesktopPath => Environment.GetEnvironmentVariable("VIBE_RE_CLIENT_DATABASE")
        ?? Path.Combine(NativeClientPlatform.DataDirectory, "client.db");

    public Task<ClientConfiguration> LoadAsync(CancellationToken cancellationToken = default) =>
        AccessAsync(null, cancellationToken);

    public Task<ClientConfiguration> UpdateAsync(Func<ClientConfiguration, Task<ClientConfiguration>> update,
        CancellationToken cancellationToken = default) => AccessAsync(update, cancellationToken);

    private Task<ClientConfiguration> AccessAsync(Func<ClientConfiguration, Task<ClientConfiguration>>? update,
        CancellationToken cancellationToken) => Task.Run(async () =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = FilePath, ForeignKeys = true, Pooling = false, DefaultTimeout = 10
            }.ToString());
            await connection.OpenAsync(cancellationToken);
            // BEGIN IMMEDIATE serializes mutations across CLI/desktop instances, avoiding lost updates.
            using var transaction = connection.BeginTransaction(deferred: false);
            await InitializeAsync(connection, transaction, cancellationToken);
            var current = await ReadAsync(connection, transaction, cancellationToken);
            var updated = update is null ? current ?? ClientConfiguration.Default(NativeClientPlatform.Capabilities.SupportsLocalServer)
                : await update(current ?? ClientConfiguration.Default(NativeClientPlatform.Capabilities.SupportsLocalServer));
            Validate(updated);
            if (current is null || update is not null)
                await WriteAsync(connection, transaction, updated, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return updated;
        }, cancellationToken);

    private static async Task InitializeAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken ct)
    {
        using var version = Command(connection, transaction, "PRAGMA user_version;");
        var schemaVersion = Convert.ToInt32(await version.ExecuteScalarAsync(ct));
        if (schemaVersion > 3)
            throw new InvalidDataException("Client database was created by a newer VibeRe version.");
        if (schemaVersion == 1)
        {
            using var migration = Command(connection, transaction, """
                ALTER TABLE ClientServers ADD COLUMN StartLocally INTEGER NOT NULL DEFAULT 0 CHECK (StartLocally IN (0, 1));
                UPDATE ClientServers SET StartLocally = 1 WHERE Name = 'local' AND Address LIKE 'http://localhost:%' AND $desktop;
                """);
            migration.Parameters.AddWithValue("$desktop", NativeClientPlatform.Capabilities.SupportsLocalServer);
            await migration.ExecuteNonQueryAsync(ct);
        }
        if (schemaVersion is 1 or 2)
        {
            using var migration = Command(connection, transaction,
                "ALTER TABLE ClientSettings ADD COLUMN ServerRequired INTEGER NOT NULL DEFAULT 1 CHECK (ServerRequired IN (0, 1));");
            await migration.ExecuteNonQueryAsync(ct);
        }
        using var schema = Command(connection, transaction, """
            CREATE TABLE IF NOT EXISTS ClientServers (
                Id TEXT PRIMARY KEY NOT NULL,
                Name TEXT NOT NULL COLLATE NOCASE UNIQUE,
                Address TEXT NOT NULL,
                HasAccessToken INTEGER NOT NULL CHECK (HasAccessToken IN (0, 1)),
                StartLocally INTEGER NOT NULL CHECK (StartLocally IN (0, 1)),
                Position INTEGER NOT NULL
            );
            CREATE TABLE IF NOT EXISTS ClientSettings (
                Id INTEGER PRIMARY KEY CHECK (Id = 1),
                ActiveServerId TEXT REFERENCES ClientServers(Id) ON DELETE SET NULL,
                ServerRequired INTEGER NOT NULL DEFAULT 1 CHECK (ServerRequired IN (0, 1))
            );
            PRAGMA user_version = 3;
            """);
        await schema.ExecuteNonQueryAsync(ct);
    }

    private static async Task<ClientConfiguration?> ReadAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken ct)
    {
        using var settings = Command(connection, transaction, "SELECT ActiveServerId, ServerRequired FROM ClientSettings WHERE Id = 1;");
        using var settingsReader = await settings.ExecuteReaderAsync(ct);
        if (!await settingsReader.ReadAsync(ct)) return null;
        var activeId = settingsReader.IsDBNull(0) ? null : settingsReader.GetString(0);
        var serverRequired = settingsReader.GetBoolean(1);
        await settingsReader.CloseAsync();
        var servers = new List<ServerConnection>();
        using var query = Command(connection, transaction,
            "SELECT Id, Name, Address, HasAccessToken, StartLocally FROM ClientServers ORDER BY Position;");
        using var reader = await query.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            servers.Add(new(Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.GetString(2), reader.GetBoolean(3), reader.GetBoolean(4)));
        return new(servers.FirstOrDefault(x => x.Id.ToString("D") == activeId)?.Name, servers, serverRequired);
    }

    private static async Task WriteAsync(SqliteConnection connection, SqliteTransaction transaction, ClientConfiguration config, CancellationToken ct)
    {
        using var clear = Command(connection, transaction, "DELETE FROM ClientSettings; DELETE FROM ClientServers;");
        await clear.ExecuteNonQueryAsync(ct);
        for (var position = 0; position < config.Servers.Count; position++)
        {
            var server = config.Servers[position];
            using var insert = Command(connection, transaction,
                "INSERT INTO ClientServers (Id, Name, Address, HasAccessToken, StartLocally, Position) VALUES ($id, $name, $address, $token, $local, $position);");
            insert.Parameters.AddWithValue("$id", server.Id.ToString("D"));
            insert.Parameters.AddWithValue("$name", server.Name);
            insert.Parameters.AddWithValue("$address", server.Address);
            insert.Parameters.AddWithValue("$token", server.HasAccessToken);
            insert.Parameters.AddWithValue("$local", server.StartLocally);
            insert.Parameters.AddWithValue("$position", position);
            await insert.ExecuteNonQueryAsync(ct);
        }
        using var settings = Command(connection, transaction, "INSERT INTO ClientSettings (Id, ActiveServerId, ServerRequired) VALUES (1, $active, $required);");
        settings.Parameters.AddWithValue("$active", config.ActiveServer is null ? DBNull.Value : config.Find().Id.ToString("D"));
        settings.Parameters.AddWithValue("$required", config.ServerRequired);
        await settings.ExecuteNonQueryAsync(ct);
    }

    private static void Validate(ClientConfiguration config)
    {
        if (config.Servers is null || config.Servers.Any(x => x is null || x.Id == Guid.Empty) ||
            config.Servers.Select(x => x.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != config.Servers.Count ||
            config.Servers.Select(x => x.Id).Distinct().Count() != config.Servers.Count)
            throw new InvalidDataException("Client database contains invalid or duplicate servers.");
        foreach (var server in config.Servers)
        {
            ServerConnection.ValidateName(server.Name);
            ServerAddress.Normalize(server.Address);
            if (server.StartLocally && !ServerAddress.IsLoopback(server.Address))
                throw new InvalidDataException("Remote connections cannot launch a local server.");
        }
        if (config.ActiveServer is not null) config.Find();
    }

    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return command;
    }
}
