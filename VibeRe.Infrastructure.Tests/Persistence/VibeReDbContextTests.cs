using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using VibeRe.Application.Conversations;
using VibeRe.Domain.Conversations;
using VibeRe.Infrastructure.Persistence;
using VibeRe.Infrastructure.Persistence.Repositories;

namespace VibeRe.Infrastructure.Tests.Persistence;

public class VibeReDbContextTests
{
    [Fact]
    public async Task Conversation_events_append_in_order_and_survive_reopening_sqlite()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<VibeReDbContext>()
            .UseSqlite(connection)
            .Options;

        var conversation = new Conversation("First chat");
        var turnId = Guid.NewGuid();
        var stepId = Guid.NewGuid();
        Assert.NotEqual(Guid.Empty, conversation.Id);

        await using (var db = new VibeReDbContext(options))
        {
            await db.Database.MigrateAsync();
            await new ConversationRepository(db).AddAsync(conversation);
            var events = new ConversationEventStore(db);
            await events.AppendAsync(conversation.Id, 1, "turn/start", "{}", turnId);
            await events.AppendAsync(conversation.Id, 2, "step/start", "{}", turnId, stepId);
            await events.AppendAsync(conversation.Id, 3, "tool/call", "{\"name\":\"read_file\"}", turnId, stepId);
            await events.AppendAsync(conversation.Id, 4, "tool/result", "{}", turnId, stepId);
            await events.AppendAsync(conversation.Id, 5, "step/end", "{}", turnId, stepId);
            await events.AppendAsync(conversation.Id, 6, "turn/end", "{}", turnId);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                events.AppendAsync(conversation.Id, 8, "turn/start", "{}", Guid.NewGuid()));
            await Assert.ThrowsAsync<ArgumentException>(() =>
                events.AppendAsync(conversation.Id, 7, "tool/call", "{}", stepId: Guid.NewGuid()));
        }

        await using (var db = new VibeReDbContext(options))
        {
            var conversations = new ConversationRepository(db);
            var events = new ConversationEventStore(db);
            Assert.Equal("First chat", (await conversations.GetByIdAsync(conversation.Id))?.Title);
            Assert.Equal(["turn/start", "step/start", "tool/call", "tool/result", "step/end", "turn/end"],
                (await events.ReadAsync(conversation.Id)).Select(entry => entry.Kind).ToArray());
            var afterFirst = await events.ReadAsync(conversation.Id, afterSequence: 1);
            Assert.Equal(new long[] { 2, 3, 4, 5, 6 }, afterFirst.Select(entry => entry.Sequence).ToArray());
            var turnEvents = await events.ReadTurnAsync(conversation.Id, turnId);
            Assert.Equal(6, turnEvents.Count);
            Assert.Equal(new long[] { 2, 3, 4, 5 },
                turnEvents.Where(entry => entry.StepId == stepId).Select(entry => entry.Sequence).ToArray());
            Assert.True(await conversations.DeleteAsync(conversation.Id));
            Assert.False(await conversations.DeleteAsync(conversation.Id));
            Assert.Null(await conversations.GetByIdAsync(conversation.Id));
            Assert.Empty(await events.ReadAsync(conversation.Id));
        }
    }

    [Fact]
    public async Task Migrations_are_in_sync_with_the_model()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new VibeReDbContext(new DbContextOptionsBuilder<VibeReDbContext>().UseSqlite(connection).Options);

        await db.Database.MigrateAsync();

        Assert.False(db.Database.HasPendingModelChanges(), "Run `dotnet ef migrations add` for the model changes.");
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
    }

    [Fact]
    public async Task Finished_conversations_leave_the_active_list_until_reopened()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new VibeReDbContext(new DbContextOptionsBuilder<VibeReDbContext>().UseSqlite(connection).Options);
        await db.Database.MigrateAsync();
        var finishedAt = new DateTimeOffset(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);
        var conversations = new ConversationRepository(db, new FixedTimeProvider(finishedAt));
        var older = new Conversation("Fix the login bug");
        var newer = new Conversation("Write release notes");
        await conversations.AddAsync(older);
        await conversations.AddAsync(newer);

        Assert.Equal([newer.Id, older.Id], (await conversations.ListAsync()).Select(conversation => conversation.Id).ToArray());

        Assert.True(await conversations.FinishAsync(older.Id));
        Assert.False(await conversations.FinishAsync(Guid.NewGuid()));
        Assert.Equal([newer.Id], (await conversations.ListAsync()).Select(conversation => conversation.Id).ToArray());
        var finished = Assert.Single(await conversations.ListAsync(ConversationStatus.Finished));
        Assert.Equal(older.Id, finished.Id);
        Assert.Equal(finishedAt.UtcDateTime, finished.FinishedAt);

        Assert.True(await conversations.ReopenAsync(older.Id));
        Assert.Equal(2, (await conversations.ListAsync()).Count);
        Assert.Null((await conversations.GetByIdAsync(older.Id))!.FinishedAt);
    }

    [Fact]
    public void Persistence_registration_resolves_repository_and_event_store()
    {
        using var provider = new ServiceCollection()
            .AddVibeRePersistence("Data Source=:memory:")
            .BuildServiceProvider();
        using var scope = provider.CreateScope();

        Assert.IsType<ConversationRepository>(scope.ServiceProvider.GetRequiredService<IConversationRepository>());
        Assert.IsType<ConversationEventStore>(scope.ServiceProvider.GetRequiredService<IConversationEventStore>());
        Assert.IsType<ContextStore>(scope.ServiceProvider.GetRequiredService<IContextStore>());
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
