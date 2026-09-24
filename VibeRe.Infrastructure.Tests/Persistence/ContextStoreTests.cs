using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using VibeRe.Application.Conversations;
using VibeRe.Domain.Conversations;
using VibeRe.Infrastructure.Persistence;
using VibeRe.Infrastructure.Persistence.Repositories;

namespace VibeRe.Infrastructure.Tests.Persistence;

public sealed class ContextStoreTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private DbContextOptions<VibeReDbContext> _options = null!;

    public async Task InitializeAsync()
    {
        await _connection.OpenAsync();
        _options = new DbContextOptionsBuilder<VibeReDbContext>().UseSqlite(_connection).Options;
        await using var db = new VibeReDbContext(_options);
        await db.Database.EnsureCreatedAsync();
    }

    public Task DisposeAsync() => _connection.DisposeAsync().AsTask();

    [Fact]
    public async Task Forks_share_ancestors_and_survive_reopening()
    {
        var conversation = await AddConversationAsync();
        ContextBranch main, retry;
        ContextNode user;
        await using (var db = new VibeReDbContext(_options))
        {
            var store = new ContextStore(db);
            main = await store.StartAsync(conversation.Id, WireMessage.System("sys"));
            user = await store.AppendAsync(main.Id, main.HeadNodeId, WireMessage.User("read a.txt"));
            var call = await store.AppendAsync(main.Id, user.Id, WireMessage.Assistant(
                null, "need the file", [new WireToolCall("call_1", "read_file", """{"path":"a.txt"}""")]));
            var result = await store.AppendAsync(main.Id, call.Id, WireMessage.Tool("call_1", "contents"));
            await store.AppendAsync(main.Id, result.Id, WireMessage.Assistant("It says: contents"));

            retry = await store.ForkAsync(user.Id, "retry");
            await store.AppendAsync(retry.Id, user.Id, WireMessage.Assistant("I can't read files."));
        }

        await using (var db = new VibeReDbContext(_options))
        {
            var store = new ContextStore(db);
            var branches = await store.ListBranchesAsync(conversation.Id);
            Assert.Equal(["main", "retry"], branches.Select(branch => branch.Name).ToArray());
            Assert.Equal(user.Id, branches[1].ForkedFromNodeId);

            var mainPath = await store.GetPathAsync(branches[0].HeadNodeId);
            var retryPath = await store.GetPathAsync(branches[1].HeadNodeId);
            Assert.Equal(["system", "user", "assistant", "tool", "assistant"], mainPath.Select(entry => entry.Node.Role).ToArray());
            Assert.Equal(["system", "user", "assistant"], retryPath.Select(entry => entry.Node.Role).ToArray());
            Assert.Equal(mainPath.Take(2).Select(entry => entry.Node.Id), retryPath.Take(2).Select(entry => entry.Node.Id));
            Assert.Equal(user.PrefixHash, retryPath[1].Node.PrefixHash);
            Assert.NotEqual(mainPath[2].Node.PrefixHash, retryPath[2].Node.PrefixHash);
            Assert.Equal("need the file", mainPath[2].Message.ToJson()["reasoning_content"]!.GetValue<string>());
            Assert.All(mainPath.Skip(2), entry => Assert.Equal(user.TurnId, entry.Node.TurnId));
        }
    }

    [Fact]
    public async Task Append_with_a_stale_head_fails_and_leaves_the_branch_alone()
    {
        var conversation = await AddConversationAsync();
        await using var db = new VibeReDbContext(_options);
        var store = new ContextStore(db);
        var main = await store.StartAsync(conversation.Id, WireMessage.System("sys"));
        var user = await store.AppendAsync(main.Id, main.HeadNodeId, WireMessage.User("hi"));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.AppendAsync(main.Id, main.HeadNodeId, WireMessage.User("hi again")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.ForkAsync(user.Id, "main"));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => store.AppendAsync(Guid.NewGuid(), user.Id, WireMessage.User("?")));

        Assert.Equal(user.Id, (await store.GetBranchAsync(main.Id))!.HeadNodeId);
        Assert.Equal(2, await db.ContextNodes.CountAsync());
    }

    [Fact]
    public async Task Identical_payloads_are_stored_once_and_deleting_a_conversation_removes_its_tree()
    {
        var first = await AddConversationAsync();
        var second = await AddConversationAsync();
        await using var db = new VibeReDbContext(_options);
        var store = new ContextStore(db);
        var events = new ConversationEventStore(db);
        var main = await store.StartAsync(first.Id, WireMessage.System("shared preamble"));
        await store.StartAsync(second.Id, WireMessage.System("shared preamble"));
        var user = await store.AppendAsync(main.Id, main.HeadNodeId, WireMessage.User("hi"));
        await events.AppendAsync(first.Id, 1, "model/usage", """{"prompt_cache_hit_tokens":0}""", user.TurnId, nodeId: user.Id);

        Assert.Equal(2, await db.ContextBlobs.CountAsync());
        Assert.Equal(user.Id, (await events.ReadAsync(first.Id)).Single().NodeId);

        Assert.True(await new ConversationRepository(db).DeleteAsync(first.Id));
        Assert.Equal(1, await db.ContextNodes.CountAsync());
        Assert.Single(await store.ListBranchesAsync(second.Id));
        Assert.Empty(await store.ListBranchesAsync(first.Id));
        Assert.Empty(await events.ReadAsync(first.Id));
    }

    private async Task<Conversation> AddConversationAsync()
    {
        var conversation = new Conversation("Chat");
        await using var db = new VibeReDbContext(_options);
        await new ConversationRepository(db).AddAsync(conversation);
        return conversation;
    }
}
