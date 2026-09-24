using Microsoft.Extensions.DependencyInjection;
using VibeRe.Application.Tools;
using VibeRe.Domain.Tools;

namespace VibeRe.Tools.Tests;

public class RegistrationTests
{
    [Fact]
    public async Task Registry_resolves_with_built_in_tools()
    {
        await using var provider = new ServiceCollection()
            .AddSingleton<IToolPolicyStore, NoPolicies>()
            .AddVibeReTools()
            .BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();

        var tools = await scope.ServiceProvider.GetRequiredService<ToolRegistry>()
            .ListAsync(new ToolContext(Guid.NewGuid(), Path.GetTempPath()));

        Assert.Contains(tools, entry => entry.Tool.Name == "grep" && entry.Approval == ToolApproval.Ask);
    }

    private sealed class NoPolicies : IToolPolicyStore
    {
        public Task<IReadOnlyDictionary<string, ToolApproval>> GetAllAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<string, ToolApproval>>(new Dictionary<string, ToolApproval>());

        public Task SetAsync(string toolName, ToolApproval approval, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<bool> DeleteAsync(string toolName, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);
    }
}
