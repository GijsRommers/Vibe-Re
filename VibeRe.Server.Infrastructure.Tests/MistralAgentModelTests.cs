using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using VibeRe.Server.Application.Providers.Auth;
using VibeRe.Server.Application.Workspace;
using VibeRe.Server.Infrastructure.Workspace;

namespace VibeRe.Server.Infrastructure.Tests;

public sealed class MistralAgentModelTests
{
    [Fact]
    public async Task ProviderBoundarySendsToolResultsAndParsesDelegationWithoutExposingTheKey()
    {
        var transport = new Transport();
        using var http = new HttpClient(transport);
        var model = new MistralAgentModel(http, new Credentials(), "devstral-latest");
        var reply = await model.CompleteAsync([
            new("user", "Create a thread"),
            new("assistant", "", [new("call1", "list_files", "{\"path\":\".\"}")]),
            new("tool", "README.md", ToolCallId: "call1")
        ], CodingHarness.ToolDefinitions, default);
        Assert.Equal("Bearer", transport.Authentication?.Scheme);
        Assert.Equal("test-provider-key", transport.Authentication?.Parameter);
        Assert.DoesNotContain("test-provider-key", transport.Body);
        using var body = JsonDocument.Parse(transport.Body!);
        Assert.Equal("call1", body.RootElement.GetProperty("messages")[2].GetProperty("tool_call_id").GetString());
        Assert.Equal(4, body.RootElement.GetProperty("tools").GetArrayLength());
        Assert.Equal("write_file", Assert.Single(reply.Tools).Name);
        Assert.Equal("Ready", reply.Text);
    }
    [Theory]
    [InlineData(HttpStatusCode.PaymentRequired, "billing")]
    [InlineData(HttpStatusCode.Unauthorized, "denied")]
    [InlineData(HttpStatusCode.TooManyRequests, "rate limiting")]
    public async Task ProviderFailuresStayActionableWithoutEchoingResponseBodies(HttpStatusCode status, string expected)
    {
        using var http = new HttpClient(new Transport { Status = status });
        var error = await Assert.ThrowsAsync<WorkspaceException>(() => new MistralAgentModel(http, new Credentials(), "test")
            .CompleteAsync([new("user", "Hi")], [], default));
        Assert.Contains(expected, error.Message);
        Assert.DoesNotContain("test-provider-key", error.Message);
    }
    private sealed class Credentials : ICredentialResolver
    { public Task<string?> ResolveAsync(string provider, CancellationToken ct = default) => Task.FromResult<string?>("test-provider-key"); }
    private sealed class Transport : HttpMessageHandler
    {
        public HttpStatusCode Status = HttpStatusCode.OK;
        public string? Body;
        public AuthenticationHeaderValue? Authentication;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Authentication = request.Headers.Authorization;
            Body = await request.Content!.ReadAsStringAsync(ct);
            return new(Status) { Content = new StringContent("""
                {"choices":[{"message":{"content":"Ready","tool_calls":[{"id":"call2","function":{"name":"write_file","arguments":"{\"path\":\"hello.txt\",\"content\":\"hello\"}"}}]}}]}
                """, Encoding.UTF8, "application/json") };
        }
    }
}
