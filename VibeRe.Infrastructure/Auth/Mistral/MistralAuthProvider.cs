using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using VibeRe.Application.Auth;

namespace VibeRe.Infrastructure.Auth.Mistral;

public sealed class MistralAuthProvider : IAuthProvider
{
    public const string ProviderId = "mistral";

    private readonly HttpClient _http;
    private readonly MistralAuthOptions _options;

    public MistralAuthProvider(HttpClient http, MistralAuthOptions? options = null, TimeProvider? timeProvider = null)
    {
        _http = http;
        _options = options ?? new MistralAuthOptions();
        Flows =
        [
            new MistralBrowserSignInFlow(http, _options, timeProvider),
            new ApiKeyAuthFlow(DisplayName, new Uri(_options.CreateApiKeyUrl)),
        ];
    }

    public string Id => ProviderId;

    public string DisplayName => "Mistral";

    public string ApiKeyEnvironmentVariable => "MISTRAL_API_KEY";

    public IReadOnlyList<IAuthFlow> Flows { get; }

    public async Task<AccountInfo?> GetAccountAsync(Credential credential, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{_options.ConsoleApiBaseUrl.TrimEnd('/')}/vibe/whoami");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential.Secret);

        WhoAmIResponse? whoami;
        try
        {
            using var response = await _http.SendAsync(request, cancellationToken);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return await CheckKeyAfterAccountRejectionAsync(credential, cancellationToken);
            }

            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            whoami = await response.Content.ReadFromJsonAsync<WhoAmIResponse>(cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException
                                   || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            return null;
        }

        return whoami is { PlanType: { } planType }
            ? new AccountInfo(DescribePlan(planType, whoami.PlanName))
            : null;
    }

    private async Task<AccountInfo?> CheckKeyAfterAccountRejectionAsync(
        Credential credential, CancellationToken cancellationToken)
    {
        // The console's account endpoint can reject a key that the model API recognizes.
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.mistral.ai/v1/models");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential.Secret);

        try
        {
            using var response = await _http.SendAsync(request, cancellationToken);
            if (response.StatusCode == HttpStatusCode.PaymentRequired)
            {
                throw new AuthException("API access requires a subscription or billing setup.", AuthErrorCode.SubscriptionRequired);
            }

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                throw new AuthException("Mistral rejected the API key.", AuthErrorCode.Unauthorized);
            }

            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException
                                   || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            return null;
        }
    }

    /// <summary>Maps whoami's plan type/name to the labels Mistral Vibe shows.</summary>
    private static string DescribePlan(string planType, string? planName)
    {
        var name = planName?.Trim().ToUpperInvariant() ?? "";
        var label = planType.Trim().ToLowerInvariant() switch
        {
            "chat" => name switch
            {
                "FREE" => "Free",
                "INDIVIDUAL" => "Pro",
                "EDU" => "Student",
                "TEAM" => "Team",
                _ => null,
            },
            "mistral_code" => name switch
            {
                "F" => "Free Codestral",
                "E" => "Code Enterprise",
                _ => null,
            },
            "api" when name.Length > 0 => name.Contains("FREE") ? "Free API" : "PAYG API",
            _ => null,
        };
        return label ?? $"{planType} {planName}".Trim();
    }

    private sealed record WhoAmIResponse(
        [property: JsonPropertyName("plan_type")] string? PlanType,
        [property: JsonPropertyName("plan_name")] string? PlanName);
}
