using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using VibeRe.Contracts;
using VibeRe.Server.Application.Providers.Auth;

namespace VibeRe.Server.Tests;

public class ProviderAuthMappingTests
{
    [Theory]
    [InlineData(ProviderAuthState.SignedOut, AuthState.SignedOut)]
    [InlineData(ProviderAuthState.Authenticated, AuthState.Authenticated)]
    [InlineData(ProviderAuthState.InvalidCredential, AuthState.InvalidCredential)]
    [InlineData(ProviderAuthState.SubscriptionRequired, AuthState.SubscriptionRequired)]
    [InlineData(ProviderAuthState.Unavailable, AuthState.Unavailable)]
    public async Task ProviderStatesMapToExplicitWireValues(ProviderAuthState state, AuthState expected)
    {
        using var server = WithAuthStatus(new(state, "Provider status."));
        await using var hub = server.CreateHub();
        await hub.StartAsync();

        var result = await hub.InvokeAsync<AuthStatusReply>("GetStatus");

        Assert.Equal(expected, result.State);
        Assert.Equal("Provider status.", result.Message);
    }

    [Theory]
    [InlineData(BrowserAttemptState.Waiting, BrowserLoginState.Waiting)]
    [InlineData(BrowserAttemptState.Completed, BrowserLoginState.Completed)]
    [InlineData(BrowserAttemptState.Failed, BrowserLoginState.Failed)]
    [InlineData(BrowserAttemptState.Expired, BrowserLoginState.Expired)]
    [InlineData(BrowserAttemptState.Canceled, BrowserLoginState.Canceled)]
    public async Task BrowserStatesMapToExplicitWireValues(BrowserAttemptState state, BrowserLoginState expected)
    {
        using var server = WithBrowserStatus(Attempt(state));
        await using var hub = server.CreateHub();
        await hub.StartAsync();

        var result = await hub.InvokeAsync<BrowserLoginReply>("StartBrowserLogin");

        Assert.Equal(expected, result.State);
        Assert.Equal("attempt-id", result.AttemptId);
        Assert.Equal("Browser status.", result.Message);
    }

    [Fact]
    public async Task UnknownProviderStateIsRejectedWithoutLeakingServiceMessage()
    {
        using var server = WithAuthStatus(new((ProviderAuthState)999, "sensitive-service-message"));
        await using var hub = server.CreateHub();
        await hub.StartAsync();

        var exception = await Assert.ThrowsAsync<HubException>(() => hub.InvokeAsync<AuthStatusReply>("GetStatus"));

        Assert.Contains("Could not access server credential storage.", exception.Message);
        server.AssertResponsesExclude("sensitive-service-message", "Unknown provider authentication state.");
    }

    [Fact]
    public async Task UnknownBrowserStateIsRejectedWithoutLeakingServiceMessage()
    {
        var attempt = Attempt((BrowserAttemptState)999) with { Message = "sensitive-service-message" };
        using var server = WithBrowserStatus(attempt);
        await using var hub = server.CreateHub();
        await hub.StartAsync();

        var exception = await Assert.ThrowsAsync<HubException>(() => hub.InvokeAsync<BrowserLoginReply>("StartBrowserLogin"));

        Assert.Contains("Could not complete Mistral browser login. Try again.", exception.Message);
        server.AssertResponsesExclude("sensitive-service-message", "Unknown browser login state.");
    }

    [Fact]
    public async Task UnknownNestedProviderStateRejectsEntireBrowserReply()
    {
        var attempt = Attempt(BrowserAttemptState.Completed) with
        {
            AuthStatus = new((ProviderAuthState)999, "sensitive-service-message")
        };
        using var server = WithBrowserStatus(attempt);
        await using var hub = server.CreateHub();
        await hub.StartAsync();

        var exception = await Assert.ThrowsAsync<HubException>(() => hub.InvokeAsync<BrowserLoginReply>("StartBrowserLogin"));

        Assert.Contains("Could not complete Mistral browser login. Try again.", exception.Message);
        server.AssertResponsesExclude("sensitive-service-message", "Unknown provider authentication state.");
    }

    private static SignalRTestServer WithAuthStatus(ProviderAuthStatus status) => new()
    {
        ConfigureTestServices = services =>
        {
            services.RemoveAll<IAuthService>();
            services.AddSingleton<IAuthService>(new FixedAuthService(status));
        }
    };

    private static SignalRTestServer WithBrowserStatus(BrowserAttemptStatus status) => new()
    {
        ConfigureTestServices = services =>
        {
            services.RemoveAll<IBrowserAuthService>();
            services.AddSingleton<IBrowserAuthService>(new FixedBrowserAuthService(status));
        }
    };

    private static BrowserAttemptStatus Attempt(BrowserAttemptState state) => new(
        "attempt-id", "https://console.mistral.ai/login", DateTimeOffset.UtcNow.AddMinutes(5), state, "Browser status.");

    private sealed class FixedAuthService(ProviderAuthStatus status) : IAuthService
    {
        public Task<ProviderAuthStatus> LoginAsync(string provider, string apiKey, CancellationToken cancellationToken = default) =>
            Task.FromResult(status);

        public Task<ProviderAuthStatus> GetStatusAsync(string provider, CancellationToken cancellationToken = default) =>
            Task.FromResult(status);

        public Task LogoutAsync(string provider, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FixedBrowserAuthService(BrowserAttemptStatus status) : IBrowserAuthService
    {
        public Task<BrowserAttemptStatus> StartAsync(string provider, CancellationToken cancellationToken = default) =>
            Task.FromResult(status);

        public Task<BrowserAttemptStatus> PollAsync(string attemptId, CancellationToken cancellationToken = default) =>
            Task.FromResult(status);

        public Task<BrowserAttemptStatus> CancelAsync(string attemptId, CancellationToken cancellationToken = default) =>
            Task.FromResult(status);

        public Task CancelProviderAsync(string provider, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
