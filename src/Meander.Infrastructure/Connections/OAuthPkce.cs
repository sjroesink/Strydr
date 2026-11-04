using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace Meander.Infrastructure;

public sealed record PkcePair(string State, string Verifier, string Challenge);

// RFC 7636 PKCE with the S256 method.
public static class OAuthPkce
{
    public static PkcePair Create()
    {
        var verifier = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        return new(Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(16)), verifier, ChallengeFor(verifier));
    }
    public static string ChallengeFor(string verifier) => Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
}
