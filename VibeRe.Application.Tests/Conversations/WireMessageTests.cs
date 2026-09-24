using System.Text;
using System.Text.Json.Nodes;
using VibeRe.Domain.Conversations;

namespace VibeRe.Application.Tests.Conversations;

public class WireMessageTests
{
    [Fact]
    public void Equal_content_serializes_to_equal_bytes_regardless_of_key_order()
    {
        var first = WireMessage.System("sys", [new JsonObject { ["type"] = "function", ["name"] = "read" }]);
        var second = WireMessage.System("sys", [new JsonObject { ["name"] = "read", ["type"] = "function" }]);

        Assert.Equal(first.Bytes.ToArray(), second.Bytes.ToArray());
        Assert.Equal(first.Hash, second.Hash);
    }

    [Fact]
    public void Tool_call_arguments_and_non_ascii_text_are_kept_verbatim()
    {
        const string arguments = "{ \"path\" :\"é.txt\" }";
        var message = WireMessage.Assistant("héllo", "thinking", [new WireToolCall("call_1", "read_file", arguments)]);

        Assert.Equal(
            """{"content":"héllo","reasoning_content":"thinking","role":"assistant","tool_calls":[{"function":{"arguments":"{ \"path\" :\"é.txt\" }","name":"read_file"},"id":"call_1","type":"function"}]}""",
            message.ToString());
    }

    [Fact]
    public void FromCanonical_round_trips_and_rejects_non_canonical_bytes()
    {
        var message = WireMessage.Tool("call_1", "ok");

        Assert.Equal(message.Hash, WireMessage.FromCanonical(message.Bytes.Span).Hash);
        Assert.Throws<ArgumentException>(() =>
            WireMessage.FromCanonical(Encoding.UTF8.GetBytes("""{"role": "tool","tool_call_id":"call_1","content":"ok"}""")));
    }

    [Fact]
    public void Stripping_reasoning_and_tools_only_touches_those_fields()
    {
        var assistant = WireMessage.Assistant("answer", "thinking");
        var system = WireMessage.System("sys", [new JsonObject { ["name"] = "read" }]);

        Assert.Equal(WireMessage.Assistant("answer").Hash, assistant.WithoutReasoning().Hash);
        Assert.Equal(WireMessage.System("sys").Hash, system.WithoutTools().Hash);
        var plain = WireMessage.User("hi");
        Assert.Same(plain, plain.WithoutReasoning());
    }

    [Fact]
    public void Tools_are_only_allowed_on_the_system_message() =>
        Assert.Throws<ArgumentException>(() =>
            WireMessage.FromCanonical(Encoding.UTF8.GetBytes("""{"content":"hi","role":"user","tools":[]}""")));
}
