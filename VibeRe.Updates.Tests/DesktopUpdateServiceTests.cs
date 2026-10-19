using Velopack;
using VibeRe.Lifecycle;

namespace VibeRe.Updates.Tests;

public sealed class DesktopUpdateServiceTests
{
    [Fact]
    public async Task Check_download_and_apply_are_separate_explicit_actions()
    {
        var backend = new FakeBackend();
        var guard = new FakeGuard();
        var service = new DesktopUpdateService(backend, guard);
        Assert.Equal(UpdateStage.Available, (await service.CheckAsync()).Stage);
        Assert.Equal(0, backend.DownloadCount);
        Assert.Equal(0, backend.ApplyCount);
        Assert.Equal(UpdateStage.ReadyToRestart, (await service.DownloadAsync()).Stage);
        Assert.Equal(0, backend.ApplyCount);
        Assert.Equal(0, guard.AcquireCount);
        await service.ApplyAndRestartAsync(false);
        Assert.Equal(UpdateStage.ReadyToRestart, service.State.Stage);
        Assert.Equal(0, guard.AcquireCount);
        Assert.Equal(UpdateStage.Applying, (await service.ApplyAndRestartAsync(true)).Stage);
        Assert.Equal(1, backend.ApplyCount);
        Assert.True(backend.LeaseWasHeldAtApply);
        Assert.Equal(1, guard.AcquireCount);
        Assert.True(guard.Disposed);
        Assert.Equal(3, backend.VerifyCount); // After download, before shutdown, and immediately before apply.
    }

    [Fact]
    public async Task Apply_before_download_and_download_before_check_do_nothing()
    {
        var backend = new FakeBackend();
        var guard = new FakeGuard();
        var service = new DesktopUpdateService(backend, guard);
        await service.DownloadAsync();
        await service.ApplyAndRestartAsync(true);
        await service.CheckAsync();
        await service.ApplyAndRestartAsync(true);
        Assert.Equal(0, backend.DownloadCount);
        Assert.Equal(0, backend.ApplyCount);
        Assert.Equal(0, guard.AcquireCount);
    }

    [Theory]
    [InlineData("0.9.0", "linux-x64-stable", "linux-x64")]
    [InlineData("1.0.0", "linux-x64-stable", "linux-x64")]
    [InlineData("2.0.0-beta", "linux-x64-stable", "linux-x64")]
    [InlineData("1.1.0", "win-x64-stable", "linux-x64")]
    [InlineData("1.1.0", "linux-x64-stable", "osx-arm64")]
    public async Task Rejects_downgrades_prereleases_and_wrong_platform(string version, string channel, string runtime)
    {
        var backend = new FakeBackend { Candidate = new(SemanticVersion.Parse(version), channel, runtime) };
        var service = new DesktopUpdateService(backend, new FakeGuard());
        Assert.Equal(UpdateStage.Error, (await service.CheckAsync()).Stage);
        Assert.Equal(0, backend.DownloadCount);
    }

    [Fact]
    public async Task No_update_and_unavailable_install_are_distinct()
    {
        var backend = new FakeBackend { Candidate = null };
        var service = new DesktopUpdateService(backend, new FakeGuard());
        Assert.Equal(UpdateStage.UpToDate, (await service.CheckAsync()).Stage);
        backend.UnavailableReason = "Install the desktop package first.";
        Assert.Equal(UpdateStage.Unsupported, (await service.CheckAsync()).Stage);
        Assert.Equal(1, backend.CheckCount);
    }

    [Fact]
    public async Task Feed_errors_do_not_claim_up_to_date()
    {
        var backend = new FakeBackend { CheckError = new HttpRequestException("404") };
        var service = new DesktopUpdateService(backend, new FakeGuard());
        Assert.Equal(UpdateStage.Error, (await service.CheckAsync()).Stage);
        Assert.Contains("no compatible release", service.State.Message);
    }

    [Fact]
    public async Task Download_failure_never_reaches_ready_or_stops_server()
    {
        var backend = new FakeBackend { DownloadError = new IOException("offline") };
        var guard = new FakeGuard();
        var service = new DesktopUpdateService(backend, guard);
        await service.CheckAsync();
        Assert.Equal(UpdateStage.Error, (await service.DownloadAsync()).Stage);
        await service.ApplyAndRestartAsync(true);
        Assert.Equal(0, guard.AcquireCount);
        Assert.Equal(0, backend.ApplyCount);
    }

    [Fact]
    public async Task Modified_download_is_blocked_before_server_shutdown()
    {
        var backend = new FakeBackend();
        var guard = new FakeGuard();
        var service = new DesktopUpdateService(backend, guard);
        await service.CheckAsync();
        await service.DownloadAsync();
        backend.VerifyError = new UpdateSafetyException("The cached update failed its integrity check.");
        Assert.Equal(UpdateStage.Error, (await service.ApplyAndRestartAsync(true)).Stage);
        Assert.Equal(0, guard.AcquireCount);
        Assert.Equal(0, backend.ApplyCount);
    }

    [Fact]
    public async Task Uncertain_server_ownership_blocks_apply_with_actionable_message()
    {
        var backend = new FakeBackend();
        var guard = new FakeGuard { Error = new InvalidOperationException("Close the user-managed server, then try again.") };
        var service = new DesktopUpdateService(backend, guard);
        await service.CheckAsync();
        await service.DownloadAsync();
        Assert.Equal(UpdateStage.Error, (await service.ApplyAndRestartAsync(true)).Stage);
        Assert.Contains("user-managed server", service.State.Message);
        Assert.Equal(0, backend.ApplyCount);
    }

    [Fact]
    public async Task Package_tampered_during_shutdown_is_rejected_and_lease_released()
    {
        var backend = new FakeBackend();
        var guard = new FakeGuard { OnAcquire = () => backend.VerifyError = new UpdateSafetyException("Package changed.") };
        var service = new DesktopUpdateService(backend, guard);
        await service.CheckAsync();
        await service.DownloadAsync();
        Assert.Equal(UpdateStage.Error, (await service.ApplyAndRestartAsync(true)).Stage);
        Assert.Equal(0, backend.ApplyCount);
        Assert.True(guard.Disposed);
    }

    [Fact]
    public async Task Apply_failure_releases_the_server_launch_lease()
    {
        var backend = new FakeBackend { ApplyError = new IOException("updater unavailable") };
        var guard = new FakeGuard();
        var service = new DesktopUpdateService(backend, guard);
        await service.CheckAsync();
        await service.DownloadAsync();
        Assert.Equal(UpdateStage.Error, (await service.ApplyAndRestartAsync(true)).Stage);
        Assert.True(guard.Disposed);
    }

    [Fact]
    public async Task Concurrent_check_download_and_apply_do_not_queue_surprise_work()
    {
        var backend = new FakeBackend { CheckBlock = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var guard = new FakeGuard();
        var service = new DesktopUpdateService(backend, guard);
        var check = service.CheckAsync();
        Assert.Equal(UpdateStage.Checking, service.State.Stage);
        await service.CheckAsync();
        await service.DownloadAsync();
        await service.ApplyAndRestartAsync(true);
        Assert.Equal(1, backend.CheckCount);
        Assert.Equal(0, backend.DownloadCount);
        backend.CheckBlock.SetResult();
        await check;
        Assert.Equal(UpdateStage.Available, service.State.Stage);
    }

    [Fact]
    public async Task Cancellation_clears_busy_state_and_allows_retry()
    {
        var backend = new FakeBackend { CheckBlock = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var service = new DesktopUpdateService(backend, new FakeGuard());
        using var cancellation = new CancellationTokenSource();
        var pending = service.CheckAsync(cancellation.Token);
        cancellation.Cancel();
        backend.CheckBlock.SetResult();
        Assert.Equal(UpdateStage.Error, (await pending).Stage);
        backend.CheckBlock = null;
        Assert.Equal(UpdateStage.Available, (await service.CheckAsync()).Stage);
    }

    [Fact]
    public async Task Observer_errors_do_not_break_update_state()
    {
        var service = new DesktopUpdateService(new FakeBackend(), new FakeGuard());
        var seen = new List<UpdateStage>();
        service.StateChanged += _ => throw new InvalidOperationException("UI error");
        service.StateChanged += update => seen.Add(update.Stage);
        await service.CheckAsync();
        await service.DownloadAsync();
        Assert.Contains(UpdateStage.ReadyToRestart, seen);
    }

    private sealed class FakeBackend : IUpdateBackend
    {
        public SemanticVersion? CurrentVersion => SemanticVersion.Parse("1.0.0");
        public string RuntimeIdentifier => "linux-x64";
        public string Channel => "linux-x64-stable";
        public string? UnavailableReason { get; set; }
        public UpdateCandidate? Candidate { get; set; } = new(SemanticVersion.Parse("1.1.0"), "linux-x64-stable", "linux-x64");
        public TaskCompletionSource? CheckBlock { get; set; }
        public Exception? CheckError { get; set; }
        public Exception? DownloadError { get; set; }
        public Exception? VerifyError { get; set; }
        public Exception? ApplyError { get; set; }
        public int CheckCount { get; private set; }
        public int DownloadCount { get; private set; }
        public int VerifyCount { get; private set; }
        public int ApplyCount { get; private set; }
        public bool LeaseWasHeldAtApply { get; private set; }
        public async Task<UpdateCandidate?> CheckAsync(CancellationToken cancellationToken)
        {
            CheckCount++;
            if (CheckBlock is not null) await CheckBlock.Task;
            if (CheckError is not null) throw CheckError;
            return Candidate;
        }
        public Task DownloadAsync(UpdateCandidate candidate, Action<int> progress, CancellationToken cancellationToken)
        {
            DownloadCount++;
            if (DownloadError is not null) throw DownloadError;
            progress(50);
            progress(100);
            return Task.CompletedTask;
        }
        public Task VerifyAsync(UpdateCandidate candidate, CancellationToken cancellationToken)
        {
            VerifyCount++;
            if (VerifyError is not null) throw VerifyError;
            return Task.CompletedTask;
        }
        public void ApplyAndRestart(UpdateCandidate candidate)
        {
            ApplyCount++;
            LeaseWasHeldAtApply = FakeGuard.IsHeld.Value;
            if (ApplyError is not null) throw ApplyError;
        }
    }

    private sealed class FakeGuard : IServerShutdownGuard, IAsyncDisposable
    {
        public static readonly AsyncLocal<bool> IsHeld = new();
        public int AcquireCount { get; private set; }
        public bool Disposed { get; private set; }
        public Exception? Error { get; init; }
        public Action? OnAcquire { get; init; }
        public Task<IAsyncDisposable> AcquireUpdateLeaseAsync(CancellationToken cancellationToken)
        {
            AcquireCount++;
            if (Error is not null) throw Error;
            IsHeld.Value = true;
            OnAcquire?.Invoke();
            return Task.FromResult<IAsyncDisposable>(this);
        }
        public ValueTask DisposeAsync()
        {
            Disposed = true;
            IsHeld.Value = false;
            return ValueTask.CompletedTask;
        }
    }
}
