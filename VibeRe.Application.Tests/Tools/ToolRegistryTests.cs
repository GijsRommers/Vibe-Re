using System.Text.Json;
using VibeRe.Application.Tools;
using VibeRe.Domain.Tools;

namespace VibeRe.Application.Tests.Tools;

public class ToolRegistryTests
{
    private static readonly JsonElement NoArguments = JsonDocument.Parse("{}").RootElement;
    private static readonly ToolContext Context = new(Guid.NewGuid(), "/tmp");

    [Fact]
    public async Task Policies_decide_whether_a_call_runs_and_default_to_asking()
    {
        var policies = new InMemoryPolicies { ["allowed"] = ToolApproval.Allow, ["denied"] = ToolApproval.Deny };
        var registry = new ToolRegistry([new Source(new EchoTool("allowed"), new EchoTool("denied"), new EchoTool("asked"))], policies);
        var approver = new Approver(answer: false);

        Assert.Equal(["allowed", "asked"], (await registry.GetAvailableAsync(Context)).Select(tool => tool.Name));
        Assert.Equal(new ToolResult("allowed ran"), await registry.InvokeAsync("allowed", NoArguments, Context, approver));
        Assert.Empty(approver.Asked);

        Assert.True((await registry.InvokeAsync("denied", NoArguments, Context, approver)).IsError);
        Assert.True((await registry.InvokeAsync("asked", NoArguments, Context, approver)).IsError);
        Assert.Equal(["asked"], approver.Asked);

        approver.Answer = true;
        Assert.Equal(new ToolResult("asked ran"), await registry.InvokeAsync("asked", NoArguments, Context, approver));
    }

    [Fact]
    public async Task Unknown_and_failing_tools_become_error_results_for_the_model()
    {
        var registry = new ToolRegistry(
            [new Source(new EchoTool("broken", fail: true))],
            new InMemoryPolicies { ["broken"] = ToolApproval.Allow });

        var unknown = await registry.InvokeAsync("missing", NoArguments, Context, new Approver(true));
        var broken = await registry.InvokeAsync("broken", NoArguments, Context, new Approver(true));

        Assert.Equal(ToolResult.Error("Unknown tool 'missing'."), unknown);
        Assert.Equal(ToolResult.Error("'broken' failed: boom"), broken);
    }

    [Fact]
    public async Task Duplicate_or_invalid_names_are_rejected()
    {
        var duplicate = new ToolRegistry([new Source(new EchoTool("read")), new Source(new EchoTool("read"))], new InMemoryPolicies());
        var invalid = new ToolRegistry([new Source(new EchoTool("read file"))], new InMemoryPolicies());

        await Assert.ThrowsAsync<InvalidOperationException>(() => duplicate.ListAsync(Context));
        await Assert.ThrowsAsync<InvalidOperationException>(() => invalid.ListAsync(Context));
    }

    private sealed class EchoTool(string name, bool fail = false) : ITool
    {
        public string Name => name;

        public string? Description => null;

        public JsonElement InputSchema { get; } = JsonDocument.Parse("""{"type":"object"}""").RootElement;

        public Task<ToolResult> InvokeAsync(JsonElement arguments, ToolContext context, CancellationToken cancellationToken = default) =>
            fail ? throw new InvalidOperationException("boom") : Task.FromResult(new ToolResult($"{name} ran"));
    }

    private sealed class Source(params ITool[] tools) : IToolSource
    {
        public Task<IReadOnlyList<ITool>> GetToolsAsync(ToolContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ITool>>(tools);
    }

    private sealed class Approver(bool answer) : IToolApprover
    {
        public bool Answer { get; set; } = answer;

        public List<string> Asked { get; } = [];

        public Task<bool> ApproveAsync(ITool tool, JsonElement arguments, CancellationToken cancellationToken)
        {
            Asked.Add(tool.Name);
            return Task.FromResult(Answer);
        }
    }

    private sealed class InMemoryPolicies : Dictionary<string, ToolApproval>, IToolPolicyStore
    {
        public Task<IReadOnlyDictionary<string, ToolApproval>> GetAllAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<string, ToolApproval>>(this);

        public Task SetAsync(string toolName, ToolApproval approval, CancellationToken cancellationToken = default)
        {
            this[toolName] = approval;
            return Task.CompletedTask;
        }

        public Task<bool> DeleteAsync(string toolName, CancellationToken cancellationToken = default) =>
            Task.FromResult(Remove(toolName));
    }
}
