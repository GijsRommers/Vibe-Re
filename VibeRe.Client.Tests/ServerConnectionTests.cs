using VibeRe.Client.Configuration;
using VibeRe.Protocol;
using Microsoft.Data.Sqlite;

namespace VibeRe.Client.Tests;

public sealed class ServerConnectionTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "vibere-client-" + Guid.NewGuid());
    private string ConfigPath => Path.Combine(_directory, "client.db");

    [Fact]
    public async Task StartupMigrationPreservesRemoteSelectionAndExistingConnectionIds()
    {
        Directory.CreateDirectory(_directory);
        var localId = Guid.NewGuid();
        var remoteId = Guid.NewGuid();
        using (var db = new SqliteConnection($"Data Source={ConfigPath};Pooling=False"))
        {
            await db.OpenAsync();
            using var schema = db.CreateCommand();
            schema.CommandText = """
                CREATE TABLE ClientServers (Id TEXT PRIMARY KEY, Name TEXT UNIQUE, Address TEXT, HasAccessToken INTEGER, Position INTEGER);
                CREATE TABLE ClientSettings (Id INTEGER PRIMARY KEY, ActiveServerId TEXT REFERENCES ClientServers(Id));
                INSERT INTO ClientServers VALUES ($local, 'local', 'http://localhost:5067', 0, 0);
                INSERT INTO ClientServers VALUES ($remote, 'work', 'https://work.example', 1, 1);
                INSERT INTO ClientSettings VALUES (1, $remote);
                PRAGMA user_version = 1;
                """;
            schema.Parameters.AddWithValue("$local", localId.ToString("D"));
            schema.Parameters.AddWithValue("$remote", remoteId.ToString("D"));
            await schema.ExecuteNonQueryAsync();
        }
        var config = await new ClientConnectionDatabase(ConfigPath).LoadAsync();
        Assert.Equal("work", config.ActiveServer);
        Assert.Equal(remoteId, config.Find().Id);
        Assert.True(config.Find().HasAccessToken);
        Assert.False(config.Find().StartLocally);
        Assert.Equal(localId, config.Find("local").Id);
        Assert.True(config.Find("local").StartLocally);
    }

    [Fact]
    public async Task ClientDatabaseStartsWithLocalServerAndWorksWithoutRunningServer()
    {
        var manager = new ServerConnectionManager(new ClientConnectionDatabase(ConfigPath), new Tokens());
        var initial = await manager.LoadAsync();
        Assert.Equal("local", initial.ActiveServer);
        Assert.Equal("http://localhost:5067", initial.Find().Address);
        Assert.True(initial.Find().StartLocally);
        await manager.AddAsync("remote", "https://offline.example");
        await manager.SelectAsync("remote");
        var reopened = await new ServerConnectionManager(new ClientConnectionDatabase(ConfigPath), new Tokens()).LoadAsync();
        Assert.Equal("remote", reopened.ActiveServer);
        using var db = new SqliteConnection($"Data Source={ConfigPath};Pooling=False");
        await db.OpenAsync();
        using var query = db.CreateCommand();
        query.CommandText = "SELECT COUNT(*) FROM ClientServers;";
        Assert.Equal(2L, await query.ExecuteScalarAsync());
        await manager.RemoveAsync("remote");
        await manager.RemoveAsync("local");
        Assert.Empty((await manager.LoadAsync()).Servers);
    }

    [Fact]
    public async Task FailedDatabaseMutationRollsBackAndFutureSchemaIsPreserved()
    {
        var database = new ClientConnectionDatabase(ConfigPath);
        await database.LoadAsync();
        await Assert.ThrowsAsync<IOException>(() => database.UpdateAsync(_ => throw new IOException("simulated")));
        Assert.Equal("local", (await database.LoadAsync()).ActiveServer);
        using (var db = new SqliteConnection($"Data Source={ConfigPath};Pooling=False"))
        {
            await db.OpenAsync();
            using var query = db.CreateCommand();
            query.CommandText = "PRAGMA user_version = 3;";
            await query.ExecuteNonQueryAsync();
        }
        await Assert.ThrowsAsync<InvalidDataException>(() => database.LoadAsync());
    }

    [Fact]
    public async Task LocalStartupPreferenceIsSavedAndClearedWhenConnectionBecomesRemote()
    {
        var manager = new ServerConnectionManager(new ClientConnectionDatabase(ConfigPath), new Tokens());
        await manager.SetLocalStartupAsync("local", false);
        Assert.False((await manager.LoadAsync()).Find().StartLocally);
        await manager.SetLocalStartupAsync("local", true);
        var config = await manager.UpdateAddressAsync("local", "https://remote.example");
        Assert.False(config.Find().StartLocally);
        await Assert.ThrowsAsync<ArgumentException>(() => manager.SetLocalStartupAsync("local", true));
    }

    [Fact]
    public async Task MultipleClientsShareProfilesAndExplicitServerDoesNotChangeDefault()
    {
        var tokens = new Tokens();
        var first = new ServerConnectionManager(new ClientConnectionDatabase(ConfigPath), tokens);
        var second = new ServerConnectionManager(new ClientConnectionDatabase(ConfigPath), tokens);
        await first.LoadAsync();
        await Task.WhenAll(first.AddAsync("work", "https://work.example"), second.AddAsync("home", "https://home.example"));
        await first.SelectAsync("work");
        var loaded = await second.LoadAsync();
        Assert.Equal(3, loaded.Servers.Count);
        Assert.Equal("work", loaded.ActiveServer);
        using var explicitServer = await new ServerConnector(new ClientConnectionDatabase(ConfigPath), tokens,
            new LocalServerLauncher(new ServerReadinessProbe(), new LocalServerProcess())).ConnectAsync("home");
        Assert.Equal("home", explicitServer.Server.Name);
        Assert.Equal("work", (await first.LoadAsync()).ActiveServer);
        await second.RemoveAsync("work");
        Assert.Equal("local", (await first.LoadAsync()).ActiveServer);
    }

    [Fact]
    public async Task SecretsAreStoredSeparatelyAndNeverFollowAddressChanges()
    {
        var tokens = new Tokens();
        var manager = new ServerConnectionManager(new ClientConnectionDatabase(ConfigPath), tokens);
        await manager.AddAsync("remote", "https://one.example");
        var config = await manager.SetTokenAsync("remote", "private-token");
        Assert.DoesNotContain("private-token", System.Text.Encoding.UTF8.GetString(await File.ReadAllBytesAsync(ConfigPath)));
        Assert.Equal("private-token", tokens.Values[config.Find("remote").Id]);
        config = await manager.UpdateAddressAsync("remote", "https://two.example");
        Assert.False(config.Find("remote").HasAccessToken);
        Assert.Empty(tokens.Values);
        await manager.SetTokenAsync("remote", "replacement");
        await manager.RemoveAsync("remote");
        Assert.Empty(tokens.Values);
    }

    [Theory]
    [InlineData("http://remote.example")]
    [InlineData("https://user:secret@remote.example")]
    [InlineData("https://remote.example?token=secret")]
    [InlineData("file:///tmp/server")]
    public async Task UnsafeServerAddressesAreRejected(string address)
    {
        var manager = new ServerConnectionManager(new ClientConnectionDatabase(ConfigPath), new Tokens());
        await Assert.ThrowsAsync<ArgumentException>(async () => await manager.AddAsync("remote", address));
    }

    [Fact]
    public async Task DuplicateNamesAreRejectedAndMalformedDatabaseIsNotOverwritten()
    {
        var manager = new ServerConnectionManager(new ClientConnectionDatabase(ConfigPath), new Tokens());
        await manager.LoadAsync();
        await Assert.ThrowsAsync<ArgumentException>(() => manager.AddAsync("LOCAL", "https://other.example"));
        await File.WriteAllTextAsync(ConfigPath, "broken database");
        await Assert.ThrowsAsync<SqliteException>(() => manager.LoadAsync());
        Assert.Equal("broken database", await File.ReadAllTextAsync(ConfigPath));
    }

    [Theory]
    [InlineData(AuthState.SignedOut, true)]
    [InlineData(AuthState.InvalidCredential, true)]
    [InlineData(AuthState.Authenticated, false)]
    [InlineData(AuthState.SubscriptionRequired, false)]
    [InlineData(AuthState.Unavailable, false)]
    public void LoginIsProposedOnlyWhenAuthenticationIsMissing(AuthState state, bool expected) =>
        Assert.Equal(expected, VibeReClient.NeedsLogin(new() { State = state }));

    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }

    private sealed class Tokens : IServerTokenStore
    {
        public Dictionary<Guid, string> Values { get; } = [];
        public Task<string?> GetAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult(Values.GetValueOrDefault(id));
        public Task SetAsync(Guid id, string token, CancellationToken cancellationToken = default) { Values[id] = token; return Task.CompletedTask; }
        public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default) { Values.Remove(id); return Task.CompletedTask; }
    }
}
