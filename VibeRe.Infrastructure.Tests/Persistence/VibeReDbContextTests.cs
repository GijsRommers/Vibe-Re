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
            await db.Database.EnsureCreatedAsync();
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
}
