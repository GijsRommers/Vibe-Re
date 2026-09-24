using VibeRe.Domain.Conversations;

namespace VibeRe.Application.Tests.Conversations;

public class ContextNodeTests
{
    [Fact]
    public void Prefix_hash_depends_only_on_the_messages_on_the_path()
    {
        var first = ContextNode.Child(ContextNode.Root(Guid.NewGuid(), WireMessage.System("sys")), WireMessage.User("hi"));
        var second = ContextNode.Child(ContextNode.Root(Guid.NewGuid(), WireMessage.System("sys")), WireMessage.User("hi"));
        var other = ContextNode.Child(ContextNode.Root(Guid.NewGuid(), WireMessage.System("sys2")), WireMessage.User("hi"));

        Assert.Equal(first.PrefixHash, second.PrefixHash);
        Assert.NotEqual(first.PrefixHash, other.PrefixHash);
        Assert.Equal(first.PayloadHash, other.PayloadHash);
        Assert.Equal(1, first.Depth);
    }

    [Fact]
    public void User_messages_start_turns_and_later_messages_inherit_them()
    {
        var root = ContextNode.Root(Guid.NewGuid(), WireMessage.System("sys"));
        var turnId = Guid.NewGuid();
        var user = ContextNode.Child(root, WireMessage.User("hi"), turnId);
        var assistant = ContextNode.Child(user, WireMessage.Assistant("hello"));
        var next = ContextNode.Child(assistant, WireMessage.User("again"));

        Assert.Null(root.TurnId);
        Assert.Equal(turnId, user.TurnId);
        Assert.Equal(turnId, assistant.TurnId);
        Assert.NotNull(next.TurnId);
        Assert.NotEqual(turnId, next.TurnId);
    }

    [Fact]
    public void Tree_shape_is_validated()
    {
        var root = ContextNode.Root(Guid.NewGuid(), WireMessage.System("sys"));
        var user = ContextNode.Child(root, WireMessage.User("hi"));

        Assert.Throws<ArgumentException>(() => ContextNode.Root(Guid.NewGuid(), WireMessage.User("hi")));
        Assert.Throws<ArgumentException>(() => ContextNode.Child(root, WireMessage.Assistant("hello")));
        Assert.Throws<ArgumentException>(() => ContextNode.Child(user, WireMessage.System("sys")));
        Assert.Throws<ArgumentException>(() => ContextNode.Child(user, WireMessage.Assistant("hello"), Guid.NewGuid()));
    }
}
