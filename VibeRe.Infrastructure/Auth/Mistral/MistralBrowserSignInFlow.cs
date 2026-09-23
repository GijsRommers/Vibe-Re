using VibeRe.Core.Auth;

namespace VibeRe.Infrastructure.Auth.Mistral;

/// <summary>
/// Mistral Vibe's browser sign-in: start a PKCE-protected sign-in process on the console, send the user to
/// its sign-in page, poll until they approve, then trade the exchange token + PKCE verifier for an API key.
/// </summary>
public sealed class MistralBrowserSignInFlow : IAuthFlow
{
    public const string FlowId = "browser";

    private readonly MistralSignInClient _client;
    private readonly MistralAuthOptions _options;
    private readonly TimeProvider _time;

    public MistralBrowserSignInFlow(HttpClient http, MistralAuthOptions options, TimeProvider? timeProvider = null)
    {
        _client = new MistralSignInClient(http, options);
        _options = options;
        _time = timeProvider ?? TimeProvider.System;
    }

    public string Id => FlowId;

    public string DisplayName => "Sign in with your browser";

    public async Task<string> AcquireSecretAsync(IAuthInteraction interaction, CancellationToken cancellationToken = default)
    {
        var pkce = Pkce.Create();
        var process = await _client.CreateProcessAsync(pkce.Challenge, cancellationToken);

        interaction.ReportStatus(AuthStatus.OpeningBrowser);
        interaction.OpenSignInUrl(process.SignInUrl, process.ExpiresAt);

        interaction.ReportStatus(AuthStatus.WaitingForUser);
        var exchangeToken = await WaitForExchangeTokenAsync(process, cancellationToken);

        interaction.ReportStatus(AuthStatus.Exchanging);
        var apiKey = await _client.ExchangeAsync(process.ProcessId, exchangeToken, pkce.Verifier, cancellationToken);

        interaction.ReportStatus(AuthStatus.Completed);
        return apiKey;
    }

    private async Task<string> WaitForExchangeTokenAsync(SignInProcess process, CancellationToken cancellationToken)
    {
        var consecutivePollFailures = 0;
        while (_time.GetUtcNow() < process.ExpiresAt)
        {
            PollResult poll;
            try
            {
                poll = await _client.PollAsync(process.PollUrl, cancellationToken);
            }
            catch (AuthException ex) when (ex.Code == AuthErrorCode.PollFailed)
            {
                // Ride out brief network blips; a persistent failure is surfaced to the user.
                consecutivePollFailures++;
                if (consecutivePollFailures >= _options.MaxConsecutivePollFailures)
                {
                    throw;
                }

                await DelayUntilNextPollAsync(process.ExpiresAt, cancellationToken);
                continue;
            }

            consecutivePollFailures = 0;
            switch (poll.Status)
            {
                case "pending":
                    await DelayUntilNextPollAsync(process.ExpiresAt, cancellationToken);
                    break;
                case "completed":
                    return poll.ExchangeToken is { Length: > 0 } exchangeToken
                        ? exchangeToken
                        : throw new AuthException("Sign-in worked, but setup couldn't finish.", AuthErrorCode.MissingSecret);
                case "expired":
                    throw new AuthException("Browser sign-in expired.", AuthErrorCode.Expired);
                case "denied":
                    throw new AuthException("Browser sign-in was denied.", AuthErrorCode.Denied);
                case "error":
                    throw new AuthException(poll.Message ?? "Browser sign-in failed.", AuthErrorCode.ProviderError);
                default:
                    throw new AuthException("Browser sign-in returned an unknown state.", AuthErrorCode.UnknownState);
            }
        }

        throw TimedOut();
    }

    private Task DelayUntilNextPollAsync(DateTimeOffset expiresAt, CancellationToken cancellationToken)
    {
        var remaining = expiresAt - _time.GetUtcNow();
        if (remaining <= TimeSpan.Zero)
        {
            throw TimedOut();
        }

        return Task.Delay(remaining < _options.PollInterval ? remaining : _options.PollInterval, _time, cancellationToken);
    }

    private static AuthException TimedOut() => new("Browser sign-in timed out.", AuthErrorCode.TimedOut);
}
