using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using VibeRe.Server.Application.Providers.Auth;
using VibeRe.Server.Application.Workspace;

namespace VibeRe.Server.Infrastructure.Workspace;

public sealed class MistralAgentModel(HttpClient http, ICredentialResolver credentials, string model) : IAgentModel
{
    public async Task<ModelReply> CompleteAsync(IReadOnlyList<ModelMessage> messages, IReadOnlyList<AgentTool> toolDefinitions, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(90));
        try { return await CompleteCoreAsync(messages, toolDefinitions, timeout.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new WorkspaceException("Mistral took too long to respond. Try again."); }
        catch (HttpRequestException)
        { throw new WorkspaceException("Could not reach Mistral. Check the connection and try again."); }
    }

    private async Task<ModelReply> CompleteCoreAsync(IReadOnlyList<ModelMessage> messages, IReadOnlyList<AgentTool> toolDefinitions, CancellationToken ct)
    {
        var key = await credentials.ResolveAsync("mistral", ct)
            ?? throw new WorkspaceException("Sign in to Mistral in Settings to use The Cockpit.");
        var wire = messages.Select(message =>
        {
            var value = new Dictionary<string, object?> { ["role"] = message.Role, ["content"] = message.Text };
            if (message.Tools is { Count: > 0 }) value["tool_calls"] = message.Tools.Select(tool => new
            { id = tool.Id, type = "function", function = new { name = tool.Name, arguments = tool.Arguments } });
            if (message.ToolCallId is not null) value["tool_call_id"] = message.ToolCallId;
            return value;
        });
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.mistral.ai/v1/chat/completions");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        request.Content = JsonContent.Create(new { model, messages = wire, tools = toolDefinitions.Select(tool => new
        {
            type = "function", function = new { name = tool.Name, description = tool.Description, parameters = new
            {
                type = "object", properties = tool.Properties.ToDictionary(x => x.Key, x => new { type = x.Value.Type, description = x.Value.Description }),
                required = tool.Required, additionalProperties = false
            } }
        }), max_tokens = 2048 });
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode) throw new WorkspaceException(response.StatusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "Mistral denied model access. Check your account in Settings.",
            HttpStatusCode.PaymentRequired => "Mistral requires an active API billing plan for this model.",
            HttpStatusCode.TooManyRequests => "Mistral is rate limiting requests. Try again shortly.",
            _ => "Mistral could not complete this request. Try again."
        });
        // Provider output is bounded before parsing; it is never logged with credentials.
        await response.Content.LoadIntoBufferAsync(1024 * 1024, ct);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var message = json.RootElement.GetProperty("choices")[0].GetProperty("message");
        var text = "";
        if (message.TryGetProperty("content", out var content))
            text = content.ValueKind == JsonValueKind.String ? content.GetString() ?? "" :
                content.ValueKind == JsonValueKind.Array ? string.Concat(content.EnumerateArray()
                    .Where(x => x.TryGetProperty("text", out var chunk) && chunk.ValueKind == JsonValueKind.String)
                    .Select(x => x.GetProperty("text").GetString())) : "";
        var calls = new List<ModelToolCall>();
        if (message.TryGetProperty("tool_calls", out var tools) && tools.ValueKind == JsonValueKind.Array)
            foreach (var tool in tools.EnumerateArray().Take(8))
            {
                var function = tool.GetProperty("function");
                calls.Add(new(tool.GetProperty("id").GetString()!, function.GetProperty("name").GetString()!,
                    function.GetProperty("arguments").ValueKind == JsonValueKind.String
                        ? function.GetProperty("arguments").GetString()! : function.GetProperty("arguments").GetRawText()));
            }
        return new(text, calls);
    }

}
