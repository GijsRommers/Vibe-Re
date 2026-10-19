using System.Buffers;
using System.Text;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Protocol;
using Microsoft.Extensions.Options;
using VibeRe.Contracts;
using VibeRe.Web.Services;

namespace VibeRe.Web.Tests;

public class BrowserSignalRJsonTests
{
    [Fact]
    public void AuthStatusCompletionDeserializesWithGeneratedMetadataOnly()
    {
        var protocol = CreateWithoutReflectionFallback();
        var payload = Frame("""{"type":3,"invocationId":"status","result":{"state":1,"message":"Signed in."}}""");
        Assert.True(protocol.TryParseMessage(ref payload, new Binder(typeof(AuthStatusReply)), out var message));
        var reply = Assert.IsType<AuthStatusReply>(Assert.IsType<CompletionMessage>(message).Result);
        Assert.Equal(AuthState.Authenticated, reply.State);
        Assert.Equal("Signed in.", reply.Message);
    }

    [Fact]
    public void BrowserLoginCompletionAndStreamRetainNestedStatusWithoutReflection()
    {
        var protocol = CreateWithoutReflectionFallback();
        const string replyJson = """{"attemptId":"attempt","signInUrl":"https://console.mistral.ai/login","expiresAtUnixSeconds":1792280000,"state":1,"message":"Done.","authStatus":{"state":1,"message":"Signed in."}}""";
        var completion = Frame("{\"type\":3,\"invocationId\":\"login\",\"result\":" + replyJson + "}");
        Assert.True(protocol.TryParseMessage(ref completion, new Binder(typeof(BrowserLoginReply)), out var message));
        var reply = Assert.IsType<BrowserLoginReply>(Assert.IsType<CompletionMessage>(message).Result);
        Assert.Equal("attempt", reply.AttemptId);
        Assert.Equal(1792280000, reply.ExpiresAtUnixSeconds);
        Assert.Equal(AuthState.Authenticated, reply.AuthStatus!.State);

        var stream = Frame("{\"type\":2,\"invocationId\":\"watch\",\"item\":" + replyJson + "}");
        Assert.True(protocol.TryParseMessage(ref stream, new Binder(typeof(BrowserLoginReply)), out message));
        Assert.Equal(reply, Assert.IsType<BrowserLoginReply>(Assert.IsType<StreamItemMessage>(message).Item));
    }

    [Theory]
    [InlineData("Login")]
    [InlineData("WatchBrowserLogin")]
    [InlineData("CancelBrowserLogin")]
    public void StringInvocationArgumentsRoundTripWithoutReflection(string method)
    {
        var protocol = CreateWithoutReflectionFallback();
        var bytes = protocol.GetMessageBytes(new InvocationMessage("invocation", method, ["argument"]));
        var payload = new ReadOnlySequence<byte>(bytes);
        Assert.True(protocol.TryParseMessage(ref payload, new Binder(typeof(string)), out var message));
        var invocation = Assert.IsType<InvocationMessage>(message);
        Assert.Equal(method, invocation.Target);
        Assert.Equal("argument", Assert.Single(invocation.Arguments));
    }

    [Fact]
    public void ParameterlessInvocationsRoundTripWithoutReflection()
    {
        var protocol = CreateWithoutReflectionFallback();
        var payload = new ReadOnlySequence<byte>(protocol.GetMessageBytes(new InvocationMessage("id", "GetStatus", [])));
        Assert.True(protocol.TryParseMessage(ref payload, new Binder(typeof(AuthStatusReply), parameterless: true), out var message));
        Assert.Empty(Assert.IsType<InvocationMessage>(message).Arguments);
    }

    private static JsonHubProtocol CreateWithoutReflectionFallback()
    {
        var options = new JsonHubProtocolOptions();
        options.PayloadSerializerOptions.TypeInfoResolverChain.Clear();
        BrowserServerConnector.ConfigureJsonProtocol(options);
        Assert.Same(BrowserJsonContext.Default, Assert.Single(options.PayloadSerializerOptions.TypeInfoResolverChain));
        return new JsonHubProtocol(Options.Create(options));
    }

    private static ReadOnlySequence<byte> Frame(string json) => new(Encoding.UTF8.GetBytes(json + "\u001e"));

    private sealed class Binder(Type resultType, bool parameterless = false) : IInvocationBinder
    {
        public Type GetReturnType(string invocationId) => resultType;
        public Type GetStreamItemType(string streamId) => resultType;
        public IReadOnlyList<Type> GetParameterTypes(string methodName) => parameterless ? [] : [typeof(string)];
    }
}
