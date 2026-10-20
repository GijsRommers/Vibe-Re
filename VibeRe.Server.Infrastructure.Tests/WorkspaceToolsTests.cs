using System.Text.Json;
using VibeRe.Server.Application.Workspace;
using VibeRe.Server.Infrastructure.Workspace;

namespace VibeRe.Server.Infrastructure.Tests;

public sealed class WorkspaceToolsTests
{
    [Fact]
    public async Task FileToolsWriteReadAndRejectTraversalAndSymlinks()
    {
        var directory = Path.Combine(Path.GetTempPath(), "vibere-tools-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            var tools = new WorkspaceTools();
            await tools.ExecuteAsync(directory, "write_file", JsonSerializer.Serialize(new { path = "src/hello.txt", content = "hello" }), default);
            Assert.Equal("hello", await tools.ExecuteAsync(directory, "read_file", "{\"path\":\"src/hello.txt\"}", default));
            await Assert.ThrowsAsync<WorkspaceException>(() => tools.ExecuteAsync(directory, "write_file", "{\"path\":\"../escape.txt\",\"content\":\"x\"}", default));
            if (!OperatingSystem.IsWindows())
            {
                File.CreateSymbolicLink(Path.Combine(directory, "broken-file"), Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".txt"));
                await Assert.ThrowsAsync<WorkspaceException>(() => tools.ExecuteAsync(directory, "write_file", "{\"path\":\"broken-file\",\"content\":\"x\"}", default));
                Directory.CreateSymbolicLink(Path.Combine(directory, "outside"), Path.GetTempPath());
                await Assert.ThrowsAsync<WorkspaceException>(() => tools.ExecuteAsync(directory, "write_file", "{\"path\":\"outside/escape.txt\",\"content\":\"x\"}", default));
            }
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task CommandReturnsRealExitCodeAndCancellationStopsWork()
    {
        if (OperatingSystem.IsWindows()) return;
        var tools = new WorkspaceTools();
        var result = await tools.ExecuteAsync(Path.GetTempPath(), "run_command", "{\"command\":\"printf checked; exit 7\"}", default);
        Assert.Contains("Exit code: 7", result); Assert.Contains("checked", result);
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        var watch = System.Diagnostics.Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => tools.ExecuteAsync(Path.GetTempPath(), "run_command", "{\"command\":\"sleep 30\"}", cancel.Token));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5));
    }
}
