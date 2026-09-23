using VibeRe.Core.Auth;

namespace VibeRe.Core.Tests.Auth;

public class CredentialTests
{
    [Fact]
    public void ToString_does_not_leak_secret() =>
        Assert.DoesNotContain("sk-secret", new Credential("fake", "sk-secret").ToString());

    [Fact]
    public void Pkce_matches_rfc7636_test_vector() =>
        Assert.Equal(
            "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM",
            Pkce.ComputeChallenge("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk"));

    [Fact]
    public void Pkce_verifier_is_fresh_and_within_rfc7636_length_limits()
    {
        var first = Pkce.Create();
        var second = Pkce.Create();

        Assert.NotEqual(first.Verifier, second.Verifier);
        Assert.InRange(first.Verifier.Length, 43, 128);
        Assert.Equal(Pkce.ComputeChallenge(first.Verifier), first.Challenge);
    }
}
