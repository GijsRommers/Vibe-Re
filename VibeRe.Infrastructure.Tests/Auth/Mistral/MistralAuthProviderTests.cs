using System.Net;
using VibeRe.Application.Auth;
using VibeRe.Infrastructure.Auth.Mistral;

namespace VibeRe.Infrastructure.Tests.Auth.Mistral;

public class MistralAuthProviderTests
{
    [Theory]
    [InlineData(HttpStatusCode.PaymentRequired, AuthErrorCode.SubscriptionRequired)]
    [InlineData(HttpStatusCode.Unauthorized, AuthErrorCode.Unauthorized)]
    public async Task Account_rejection_checks_whether_the_model_api_recognizes_the_key(
        HttpStatusCode modelApiStatus, AuthErrorCode expectedError)
    {
        var handler = new AccountThenModelsHandler(modelApiStatus);
        var provider = new MistralAuthProvider(new HttpClient(handler));

        var error = await Assert.ThrowsAsync<AuthException>(() =>
            provider.GetAccountAsync(new Credential("mistral", "test-key")));

        Assert.Equal(expectedError, error.Code);
        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task Recognized_key_with_unavailable_account_details_is_not_rejected()
    {
        var handler = new AccountThenModelsHandler(HttpStatusCode.OK);
        var provider = new MistralAuthProvider(new HttpClient(handler));

        Assert.Null(await provider.GetAccountAsync(new Credential("mistral", "test-key")));
        Assert.Equal(2, handler.RequestCount);
    }

    private sealed class AccountThenModelsHandler(HttpStatusCode modelApiStatus) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            Assert.Equal("Bearer test-key", request.Headers.Authorization?.ToString());
            var status = request.RequestUri!.AbsolutePath switch
            {
                "/api/vibe/whoami" => HttpStatusCode.Unauthorized,
                "/v1/models" => modelApiStatus,
                _ => throw new InvalidOperationException($"Unexpected URL: {request.RequestUri}"),
            };
            return Task.FromResult(new HttpResponseMessage(status));
        }
    }
}
