using System.Net;
using System.Net.Http.Headers;
using VibeRe.Server.Application.Providers.Auth;
using VibeRe.Domain.Provider;

namespace VibeRe.Server.Infrastructure.Providers.Mistral.Auth;

public sealed class MistralAuthFlow(HttpClient client) : IAuthFlow
{
    public string ProviderName => ProviderIdentifiers.Mistral;
    public Uri BaseUri => new("https://api.mistral.ai/");

    public async Task<ProviderAuthStatus> ValidateAsync(string apiKey, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(BaseUri, "v1/models"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            // Browser-issued Vibe credentials can be valid for the account even when the main API denies them.
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                using var accountRequest = new HttpRequestMessage(HttpMethod.Get, "https://console.mistral.ai/api/vibe/whoami");
                accountRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                using var account = await client.SendAsync(accountRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                if (account.StatusCode == HttpStatusCode.OK)
                    return new(ProviderAuthState.Authenticated, "Mistral account credential accepted. API access depends on the account plan.");
                if (account.StatusCode is not (HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden))
                    return new(ProviderAuthState.Unavailable, "Mistral could not verify the account credential. Try again later.");
            }
            return response.StatusCode switch
            {
                HttpStatusCode.OK => new(ProviderAuthState.Authenticated, "Mistral API key accepted."),
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                    new(ProviderAuthState.InvalidCredential, "Mistral rejected the API key or denied access."),
                HttpStatusCode.PaymentRequired =>
                    new(ProviderAuthState.SubscriptionRequired, "Mistral requires an active API billing plan."),
                _ => new(ProviderAuthState.Unavailable, "Mistral could not verify the API key. Try again later.")
            };
        }
        catch (HttpRequestException)
        {
            return new(ProviderAuthState.Unavailable, "Could not reach Mistral. Try again later.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(ProviderAuthState.Unavailable, "Mistral verification timed out. Try again later.");
        }
    }
}
