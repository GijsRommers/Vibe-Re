using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using VibeRe.Client.Application;
using VibeRe.Client.Application.Configuration;
using VibeRe.Contracts;
using VibeRe.Web.Pages;
using VibeRe.Web.Services;
using AuthState = VibeRe.Client.Application.AuthState;
using AuthStatus = VibeRe.Client.Application.AuthStatus;
using BrowserLoginResult = VibeRe.Client.Application.BrowserLoginResult;

namespace VibeRe.Web.Tests;

public class HomeRenderingTests
{
    [Fact]
    public async Task TrustedLocalPageShowsMistralLoginAndDeviceApproval()
    {
        var html = await RenderAsync(new(true, true, true), AuthState.SignedOut);
        Assert.Contains("class=\"login-button\"", html);
        Assert.Contains("mistral.svg", html);
        Assert.Contains("https://vibe.example.com", html);
        Assert.Contains("Approve a device", html);
        Assert.Contains("Connected devices", html);
        Assert.Contains("<details class=\"device-management\">", html);
        Assert.Contains("<summary>Manage devices</summary>", html);
        Assert.DoesNotContain("<details open", html);
        Assert.DoesNotContain("device-management\" open", html);
        Assert.DoesNotContain("Get pairing code", html);
        Assert.DoesNotContain("Disconnect this browser", html);
    }

    [Theory]
    [InlineData(AuthState.Authenticated)]
    [InlineData(AuthState.SubscriptionRequired)]
    public async Task PairedSignedInPageSeparatesProviderLogoutFromDeviceDisconnect(AuthState state)
    {
        var html = await RenderAsync(new(true, false, false, "My tablet", DateTimeOffset.UtcNow.AddDays(30)), state);
        Assert.Contains("Signed in", html);
        Assert.Contains("Log out server from Mistral", html);
        Assert.Contains("Disconnect this browser", html);
        Assert.Contains("My tablet", html);
        Assert.DoesNotContain("Approve a device", html);
        Assert.DoesNotContain("class=\"login-button\"", html);
        if (state == AuthState.SubscriptionRequired) Assert.Contains("Mistral subscription required.", html);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnpairedPageOffersPairingAndLocalBootstrapOnlyWhenLocal(bool local)
    {
        var html = await RenderAsync(new(false, false, local), AuthState.SignedOut);
        Assert.Contains("Get pairing code", html);
        Assert.Contains("Reset this browser connection", html);
        Assert.DoesNotContain("<details", html);
        Assert.Equal(local, html.Contains("Configured server access token"));
        Assert.DoesNotContain("Approve a device", html);
        Assert.DoesNotContain("Continue in Mistral", html);
    }

    private static async Task<string> RenderAsync(AccessStatusReply access, AuthState authState)
    {
        var server = new BrowserServer("https://vibe.example.com/");
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<NavigationManager>(new BrowserNavigation());
        services.AddSingleton(server);
        services.AddSingleton(new BrowserAccessClient(new HttpClient(new PageHandler(access)) { BaseAddress = server.Origin }));
        services.AddSingleton(new ClientStartup(new CurrentOriginConfiguration(server), new Connector(server, authState)));
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var component = await renderer.RenderComponentAsync<Home>();
            return component.ToHtmlString();
        });
    }

    private sealed class PageHandler(AccessStatusReply access) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = request.RequestUri!.AbsolutePath == "/api/access/status" ? JsonContent.Create(access) : JsonContent.Create(Array.Empty<DeviceReply>())
            });
    }

    private sealed class BrowserNavigation : NavigationManager
    {
        public BrowserNavigation() => Initialize("https://vibe.example.com/", "https://vibe.example.com/");
        protected override void NavigateToCore(string uri, bool forceLoad) => throw new NotSupportedException();
    }

    private sealed class Connector(BrowserServer server, AuthState state) : IServerConnector
    {
        public Task<(ServerConnection Server, IVibeReClient Client)> ConnectAsync(string? name = null, string? tokenOverride = null, CancellationToken cancellationToken = default) =>
            Task.FromResult<(ServerConnection, IVibeReClient)>((server.Connection, new AuthClient(state)));
    }

    private sealed class AuthClient(AuthState state) : IVibeReClient
    {
        public Task<AuthStatus> GetStatusAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new AuthStatus { State = state, Message = state switch
            {
                AuthState.Authenticated => "Signed in.",
                AuthState.SubscriptionRequired => "Mistral subscription required.",
                _ => "Sign in to Mistral."
            } });
        public Task<AuthStatus> LoginAsync(string apiKey, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AuthStatus> LogoutAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<BrowserLoginResult> LoginWithBrowserAsync(Func<BrowserLoginResult, Task> showBrowser, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
