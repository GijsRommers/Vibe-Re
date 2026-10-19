using Microsoft.Data.Sqlite;
using VibeRe.Client.Application;
using VibeRe.Client.Application.Configuration;
using VibeRe.Client.Infrastructure.Adapters;

namespace VibeRe.Client.Tests;

public sealed class ServerTokenTransactionTests : IDisposable
{
    private readonly string _database = Path.Combine(Path.GetTempPath(), $"vibere-token-write-{Guid.NewGuid()}.db");
    private readonly Tokens _tokens = new();
    private ClientConnectionDatabase Store => new(_database);
    private ServerConnectionManager Manager => new(Store, _tokens);

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task StaleManualTokenCannotReplaceOrClearAnEditedOrRecreatedServer(bool recreate, bool clear)
    {
        var original = (await Manager.AddAsync("work", "https://original.example")).Find("work");
        if (recreate)
        {
            await Manager.RemoveAsync("work");
            await Manager.AddAsync("work", original.Address);
        }
        else
        {
            await Manager.UpdateAddressAsync("work", "https://changed.example");
        }
        var current = (await Manager.LoadAsync()).Find("work");
        current = (await Manager.SetTokenAsync(current, "current-token")).Find("work");

        await Assert.ThrowsAsync<ServerConnectionException>(() => Manager.SetTokenAsync(original, clear ? "" : "stale-token"));

        Assert.Equal(current, (await Manager.LoadAsync()).Find("work"));
        var stored = Assert.Single(_tokens.Values);
        Assert.Equal(current.Id, stored.Key);
        Assert.Equal("current-token", stored.Value);
    }

    [Fact]
    public async Task MatchingIdentityCannotAuthorizeATokenForADifferentOrigin()
    {
        var original = (await Manager.AddAsync("work", "https://original.example")).Find("work");
        await Store.UpdateAsync(config => Task.FromResult(config with
        {
            Servers = config.Servers.Select(server => server.Id == original.Id
                ? server with { Address = "https://changed.example" } : server).ToList()
        }));

        await Assert.ThrowsAsync<ServerConnectionException>(() => Manager.SetTokenAsync(original, "original-token"));

        Assert.Empty(_tokens.Values);
        Assert.False((await Manager.LoadAsync()).Find("work").HasAccessToken);
    }

    [Fact]
    public async Task EquivalentNormalizedOriginIsAccepted()
    {
        var original = (await Manager.AddAsync("work", "https://work.example")).Find("work");
        var expected = original with { Address = "https://WORK.EXAMPLE:443/" };

        var saved = (await Manager.SetTokenAsync(expected, "work-token")).Find("work");

        Assert.Equal(original.Address, saved.Address);
        Assert.NotEqual(original.Id, saved.Id);
        Assert.Equal("work-token", _tokens.Values[saved.Id]);
    }

    [Fact]
    public async Task ReplacingAndClearingTokensRotateIdentityWithoutChangingSelection()
    {
        var original = (await Manager.AddAsync("work", "https://work.example")).Find("work");
        var first = (await Manager.SetTokenAsync(original, "first-token")).Find("work");
        var replaced = await Manager.SetTokenAsync(first, "second-token");
        var second = replaced.Find("work");
        Assert.Equal("local", replaced.ActiveServer);
        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(new KeyValuePair<Guid, string>(second.Id, "second-token"), Assert.Single(_tokens.Values));
        await Assert.ThrowsAsync<ServerConnectionException>(() => Manager.SetTokenAsync(first, "late-token"));

        var cleared = await Manager.SetTokenAsync(second, "");

        Assert.Equal("local", cleared.ActiveServer);
        Assert.NotEqual(second.Id, cleared.Find("work").Id);
        Assert.False(cleared.Find("work").HasAccessToken);
        Assert.Empty(_tokens.Values);
    }

    [Theory]
    [InlineData("new")]
    [InlineData("replace")]
    [InlineData("clear")]
    [InlineData("address")]
    [InlineData("remove")]
    public async Task FailedDatabaseWritePreservesThePreviousProfileAndCredential(string operation)
    {
        var original = (await Manager.AddAsync("work", "https://work.example")).Find("work");
        if (operation != "new")
            original = (await Manager.SetTokenAsync(original, "working-token")).Find("work");
        var before = await Manager.LoadAsync();
        var secrets = _tokens.Values.ToArray();
        await RejectDatabaseWritesAsync();

        await Assert.ThrowsAsync<SqliteException>(() => MutateAsync(operation, original));

        var after = await Manager.LoadAsync();
        Assert.Equal(before.ActiveServer, after.ActiveServer);
        Assert.Equal(before.Servers, after.Servers);
        Assert.Equal(secrets, _tokens.Values.ToArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedRollbackCleanupDoesNotMaskTheDatabaseError(bool existingToken)
    {
        var original = (await Manager.AddAsync("work", "https://work.example")).Find("work");
        if (existingToken)
            original = (await Manager.SetTokenAsync(original, "working-token")).Find("work");
        await RejectDatabaseWritesAsync();
        _tokens.Delete = _ => throw new InvalidOperationException("Fake keyring cleanup failure.");

        await Assert.ThrowsAsync<SqliteException>(() => Manager.SetTokenAsync(original, "uncommitted-token"));

        Assert.Equal(original, (await Manager.LoadAsync()).Find("work"));
        if (existingToken) Assert.Equal("working-token", _tokens.Values[original.Id]);
        Assert.DoesNotContain((await Manager.LoadAsync()).Servers, server =>
            _tokens.Values.GetValueOrDefault(server.Id) == "uncommitted-token");
    }

    [Theory]
    [InlineData("replace")]
    [InlineData("clear")]
    [InlineData("address")]
    [InlineData("remove")]
    public async Task FailedPostCommitCleanupStillReturnsTheCommittedSettings(string operation)
    {
        var original = (await Manager.AddAsync("work", "https://work.example")).Find("work");
        original = (await Manager.SetTokenAsync(original, "obsolete-token")).Find("work");
        _tokens.Delete = _ => throw new InvalidOperationException("Fake keyring cleanup failure.");

        var saved = await MutateAsync(operation, original);

        Assert.Equal(saved.Servers, (await Manager.LoadAsync()).Servers);
        Assert.DoesNotContain(saved.Servers, server => server.Id == original.Id);
        Assert.Equal("obsolete-token", _tokens.Values[original.Id]);
        if (operation == "replace")
            Assert.Equal("new-token", _tokens.Values[saved.Find("work").Id]);
    }

    [Fact]
    public async Task PartiallyWrittenCredentialIsCleanedWithoutChangingTheCurrentToken()
    {
        var original = (await Manager.LoadAsync()).Find();
        original = (await Manager.SetTokenAsync(original, "working-token")).Find();
        var error = new InvalidOperationException("Fake keyring write failure.");
        _tokens.AfterSet = () => throw error;

        Assert.Same(error, await Assert.ThrowsAsync<InvalidOperationException>(() => Manager.SetTokenAsync(original, "partial-token")));

        Assert.Equal(original, (await Manager.LoadAsync()).Find());
        Assert.Equal(new KeyValuePair<Guid, string>(original.Id, "working-token"), Assert.Single(_tokens.Values));
    }

    [Fact]
    public async Task CancellationAfterCredentialWriteRollsBackOnlyTheNewToken()
    {
        var original = (await Manager.LoadAsync()).Find();
        original = (await Manager.SetTokenAsync(original, "working-token")).Find();
        using var cancellation = new CancellationTokenSource();
        _tokens.AfterSet = cancellation.Cancel;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Manager.SetTokenAsync(original, "canceled-token", cancellation.Token));

        Assert.Equal(original, (await Manager.LoadAsync()).Find());
        Assert.Equal(new KeyValuePair<Guid, string>(original.Id, "working-token"), Assert.Single(_tokens.Values));
    }

    [Fact]
    public async Task UnresponsiveRollbackCleanupIsBoundedAndPreservesTheOriginalError()
    {
        var original = (await Manager.LoadAsync()).Find();
        await RejectDatabaseWritesAsync();
        _tokens.Delete = _ => new TaskCompletionSource().Task;

        await Assert.ThrowsAsync<SqliteException>(() => Manager.SetTokenAsync(original, "uncommitted-token").WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.Equal(original, (await Manager.LoadAsync()).Find());
    }

    [Fact]
    public async Task DelayedCleanupCannotDeleteAnotherWritersReplacementToken()
    {
        var original = (await Manager.LoadAsync()).Find();
        original = (await Manager.SetTokenAsync(original, "first-token")).Find();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _tokens.Delete = async id =>
        {
            if (id != original.Id) return;
            started.SetResult();
            await finish.Task;
        };
        var pending = Manager.SetTokenAsync(original, "second-token");
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            var second = (await Manager.LoadAsync()).Find();
            var third = (await Manager.SetTokenAsync(second, "third-token")).Find();
            finish.SetResult();
            await pending.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(third, (await Manager.LoadAsync()).Find());
            Assert.Equal(new KeyValuePair<Guid, string>(third.Id, "third-token"), Assert.Single(_tokens.Values));
        }
        finally
        {
            finish.TrySetResult();
        }
    }

    [Fact]
    public async Task ErrorAfterCommitDoesNotDeleteThePublishedCredential()
    {
        var original = new ServerConnection(Guid.NewGuid(), "work", "https://work.example", HasAccessToken: true);
        _tokens.Values[original.Id] = "working-token";
        var error = new IOException("Fake error after commit.");
        var store = new UncertainStore(new("work", [original]), error, commit: true);
        var manager = new ServerConnectionManager(store, _tokens);

        Assert.Same(error, await Assert.ThrowsAsync<IOException>(() => manager.SetTokenAsync(original, "committed-token")));

        var published = store.Current.Find();
        Assert.NotEqual(original.Id, published.Id);
        Assert.Equal("committed-token", _tokens.Values[published.Id]);
        Assert.Equal("working-token", _tokens.Values[original.Id]);
    }

    [Fact]
    public async Task UnreadableCommitOutcomeLeavesTheNewCredentialAndPreservesTheError()
    {
        var original = new ServerConnection(Guid.NewGuid(), "work", "https://work.example", HasAccessToken: true);
        _tokens.Values[original.Id] = "working-token";
        var error = new IOException("Fake uncertain commit error.");
        var store = new UncertainStore(new("work", [original]), error, commit: false, failRead: true);
        var manager = new ServerConnectionManager(store, _tokens);

        Assert.Same(error, await Assert.ThrowsAsync<IOException>(() => manager.SetTokenAsync(original, "uncertain-token")));

        Assert.Equal(original, store.Current.Find());
        Assert.Equal("working-token", _tokens.Values[original.Id]);
        Assert.Contains("uncertain-token", _tokens.Values.Values);
    }

    private Task<ClientConfiguration> MutateAsync(string operation, ServerConnection server) => operation switch
    {
        "new" or "replace" => Manager.SetTokenAsync(server, "new-token"),
        "clear" => Manager.SetTokenAsync(server, ""),
        "address" => Manager.UpdateAddressAsync(server.Name, "https://changed.example"),
        "remove" => Manager.RemoveAsync(server.Name),
        _ => throw new ArgumentException("Unknown test operation.")
    };

    private async Task RejectDatabaseWritesAsync()
    {
        using var db = new SqliteConnection($"Data Source={_database};Pooling=False");
        await db.OpenAsync();
        using var command = db.CreateCommand();
        command.CommandText = "CREATE TRIGGER RejectInsert BEFORE INSERT ON ClientServers BEGIN SELECT RAISE(ABORT, 'simulated write failure'); END;";
        await command.ExecuteNonQueryAsync();
    }

    public void Dispose() => File.Delete(_database);

    private sealed class UncertainStore(ClientConfiguration initial, Exception error, bool commit, bool failRead = false)
        : IClientConfigurationStore
    {
        public ClientConfiguration Current { get; private set; } = initial;
        public Task<ClientConfiguration> LoadAsync(CancellationToken cancellationToken = default) => failRead
            ? Task.FromException<ClientConfiguration>(new IOException("Fake unreadable configuration."))
            : Task.FromResult(Current);
        public async Task<ClientConfiguration> UpdateAsync(Func<ClientConfiguration, Task<ClientConfiguration>> update,
            CancellationToken cancellationToken = default)
        {
            var updated = await update(Current);
            if (commit) Current = updated;
            throw error;
        }
    }

    private sealed class Tokens : IServerTokenStore
    {
        public Dictionary<Guid, string> Values { get; } = [];
        public Action? AfterSet { get; set; }
        public Func<Guid, Task>? Delete { get; set; }
        public Task<string?> GetAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult(Values.GetValueOrDefault(id));
        public Task SetAsync(Guid id, string token, CancellationToken cancellationToken = default)
        {
            Values[id] = token;
            AfterSet?.Invoke();
            return Task.CompletedTask;
        }
        public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
        {
            if (Delete is not null) await Delete(id);
            Values.Remove(id);
        }
    }
}
