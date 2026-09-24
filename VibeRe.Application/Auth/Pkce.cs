using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace VibeRe.Core.Auth;

/// <summary>An RFC 7636 PKCE verifier and its S256 challenge.</summary>
public sealed class Pkce
{
    public const string ChallengeMethod = "S256";

    private Pkce(string verifier)
    {
        Verifier = verifier;
        Challenge = ComputeChallenge(verifier);
    }

    public string Verifier { get; }

    public string Challenge { get; }

    public static Pkce Create() => new(Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(64)));

    public static string ComputeChallenge(string verifier) =>
        Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
}
