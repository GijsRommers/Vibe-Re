using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using VibeRe.Application.Conversations;
using VibeRe.Domain.Conversations;

namespace VibeRe.Application.Tests.Conversations;

public class ContextBuilderTests
{
    private static readonly JsonArray Tools = [new JsonObject { ["type"] = "function", ["name"] = "read_file" }];

    [Fact]
    public void Tools_are_lifted_out_of_the_system_message()
    {
        var context = ContextBuilder.Build(Path(WireMessage.System("sys", Tools), WireMessage.User("hi")));

        Assert.Equal(WireMessage.System("sys").Hash, context.Messages[0].Hash);
        Assert.Equal("""[{"name":"read_file","type":"function"}]""", Encoding.UTF8.GetString(context.Tools!.Value.Span));
        Assert.Null(ContextBuilder.Build(Path(WireMessage.System("sys"))).Tools);
    }

    [Fact]
    public void Each_tool_loop_request_extends_the_previous_one_byte_for_byte()
    {
        var path = ToolLoop();

        for (var length = 2; length < path.Count; length++)
        {
            var previous = Bytes(ContextBuilder.Build(path.Take(length).ToList()));
            var next = Bytes(ContextBuilder.Build(path.Take(length + 1).ToList()));
            Assert.Equal(previous, next.Take(previous.Length));
        }
    }

    [Fact]
    public void Current_turn_replay_drops_reasoning_from_earlier_turns_only()
    {
        var path = ToolLoop();
        var inTurn = ContextBuilder.Build(path);
        Assert.True(inTurn.Messages[2].HasReasoning);

        var nextTurn = ContextBuilder.Build(Path([.. path.Select(entry => entry.Message), WireMessage.User("thanks"), WireMessage.Assistant("np", "short")]));
        Assert.DoesNotContain(nextTurn.Messages.SkipLast(1), message => message.HasReasoning);
        Assert.True(nextTurn.Messages[^1].HasReasoning);

        Assert.All(ContextBuilder.Build(path, ReasoningReplay.All).Messages.Where(message => message.Role == "assistant"),
            message => Assert.True(message.HasReasoning));
        Assert.DoesNotContain(ContextBuilder.Build(path, ReasoningReplay.None).Messages, message => message.HasReasoning);
    }

    [Fact]
    public void WriteTo_emits_the_stored_bytes_into_the_request()
    {
        var context = ContextBuilder.Build(Path(WireMessage.System("sys", Tools), WireMessage.User("hi")));
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("model", "deepseek-chat");
            context.WriteTo(writer);
            writer.WriteEndObject();
        }

        Assert.Equal(
            """{"model":"deepseek-chat","messages":[{"content":"sys","role":"system"},{"content":"hi","role":"user"}],"tools":[{"name":"read_file","type":"function"}]}""",
            Encoding.UTF8.GetString(buffer.ToArray()));
    }

    private static List<ContextEntry> ToolLoop() => Path(
        WireMessage.System("sys", Tools),
        WireMessage.User("read a.txt"),
        WireMessage.Assistant(null, "I should read it", [new WireToolCall("call_1", "read_file", """{"path":"a.txt"}""")]),
        WireMessage.Tool("call_1", "contents"),
        WireMessage.Assistant("It says: contents", "Done"));

    private static List<ContextEntry> Path(params WireMessage[] messages)
    {
        var entries = new List<ContextEntry> { new(ContextNode.Root(Guid.NewGuid(), messages[0]), messages[0]) };
        foreach (var message in messages.Skip(1))
        {
            entries.Add(new ContextEntry(ContextNode.Child(entries[^1].Node, message), message));
        }

        return entries;
    }

    private static byte[] Bytes(ModelContext context) =>
        [.. context.Messages.SelectMany(message => message.Bytes.ToArray())];
}
