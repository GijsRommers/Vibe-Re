using VibeRe.Client.Configuration;

namespace VibeRe.Client.Tests;

public class ServerConnectorTests
{
    [Fact]
    public async Task ConcurrentAddressChangeCannotSendReplacementTokenToOldDestination()
    {
        var path = Path.Combine(Path.GetTempPath(), $"vibere-token-race-{Guid.NewGuid()}.db");
        try
        {
            var store = new ClientConnectionDatabase(path);
            var tokens = new BlockingTokens();
            var connections = new ServerConnectionManager(store, tokens);
            await connections.AddAsync("work", "https://old.example");
            var original = (await connections.SetTokenAsync("work", "old-token")).Find("work");
            var connector = new ServerConnector(store, tokens, new UnusedLauncher());
            var pending = connector.ConnectAsync("work");
            await tokens.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

            await connections.UpdateAddressAsync("work", "https://new.example");
            var replacement = (await connections.SetTokenAsync("work", "new-token")).Find("work");
            tokens.ResumeRead.SetResult();

            await Assert.ThrowsAsync<InvalidOperationException>(() => pending);
            Assert.NotEqual(original.Id, replacement.Id);
            Assert.Equal("new-token", tokens.Values[replacement.Id]);
            Assert.DoesNotContain(original.Id, tokens.Values.Keys);
        }
        finally { File.Delete(path); }
    }

    private sealed class BlockingTokens : IServerTokenStore
    {
        public Dictionary<Guid, string> Values { get; } = [];
        public TaskCompletionSource ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ResumeRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<string?> GetAsync(Guid id, CancellationToken cancellationToken = default)
        {
            ReadStarted.TrySetResult();
            await ResumeRead.Task.WaitAsync(cancellationToken);
            return Values.GetValueOrDefault(id);
        }
        public Task SetAsync(Guid id, string token, CancellationToken cancellationToken = default)
        {
            Values[id] = token;
            return Task.CompletedTask;
        }
        public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
        {
            Values.Remove(id);
            return Task.CompletedTask;
        }
    }

    private sealed class UnusedLauncher : ILocalServerLauncher
    {
        public Task EnsureRunningAsync(ServerConnection server, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("A connection without a token must fail before startup.");
    }
}
