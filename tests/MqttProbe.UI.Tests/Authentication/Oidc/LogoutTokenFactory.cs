using System.Security.Cryptography;
using System.Text;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace MqttProbe.UI.Tests.Authentication;

// Signs logout tokens the way a provider does (RS256 over base64url) while
// keeping the raw payload as text, so tests can express duplicates and shapes
// no fluent token builder would emit.
internal sealed class LogoutTokenFactory : IDisposable
{
    internal const string Issuer = "https://idp.example.com";
    internal const string ClientId = "mqttprobe";
    internal const string KeyId = "logout-key-1";

    private readonly RSA _rsa = RSA.Create(2048);

    internal OpenIdConnectConfiguration CreateConfiguration(string? issuer = null, RSA? trustedRsa = null)
    {
        var configuration = new OpenIdConnectConfiguration { Issuer = issuer ?? Issuer };
        configuration.SigningKeys.Add(new RsaSecurityKey(trustedRsa ?? _rsa) { KeyId = KeyId });
        return configuration;
    }

    internal string CreateLogoutToken(string payloadJson, RSA? signingRsa = null, string? keyId = KeyId)
    {
        var rsa = signingRsa ?? _rsa;
        var headerJson = keyId is null
            ? "{\"alg\":\"RS256\",\"typ\":\"logout+jwt\"}"
            : $"{{\"alg\":\"RS256\",\"kid\":\"{keyId}\",\"typ\":\"logout+jwt\"}}";

        var signingInput = Base64UrlEncoder.Encode(headerJson) + "." + Base64UrlEncoder.Encode(payloadJson);
        var signature = Base64UrlEncoder.Encode(
            rsa.SignData(Encoding.ASCII.GetBytes(signingInput), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));

        return signingInput + "." + signature;
    }

    // Optional claims are omitted entirely when their value is null; iat and exp
    // default to now / now+120s and are dropped with their include flags.
    internal static string CreatePayload(
        string? issuer = Issuer,
        string? subject = "user-123",
        string? sid = null,
        string? tokenId = "jti-1",
        bool includeIat = true,
        bool includeExp = true,
        long? issuedAt = null,
        long? expiresAt = null,
        string? audience = ClientId,
        bool includeEvents = true,
        string? nonce = null,
        string? extraJson = null)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var parts = new List<string>();

        if (issuer is not null)
        {
            parts.Add($"\"iss\":\"{issuer}\"");
        }

        if (audience is not null)
        {
            parts.Add($"\"aud\":\"{audience}\"");
        }

        if (subject is not null)
        {
            parts.Add($"\"sub\":\"{subject}\"");
        }

        if (sid is not null)
        {
            parts.Add($"\"sid\":\"{sid}\"");
        }

        if (tokenId is not null)
        {
            parts.Add($"\"jti\":\"{tokenId}\"");
        }

        if (nonce is not null)
        {
            parts.Add($"\"nonce\":\"{nonce}\"");
        }

        if (includeIat)
        {
            parts.Add($"\"iat\":{issuedAt ?? now}");
        }

        if (includeExp)
        {
            parts.Add($"\"exp\":{expiresAt ?? now + 120}");
        }

        if (includeEvents)
        {
            parts.Add("\"events\":{\"http://schemas.openid.net/event/backchannel-logout\":{}}");
        }

        if (extraJson is not null)
        {
            parts.Add(extraJson);
        }

        return "{" + string.Join(",", parts) + "}";
    }

    public void Dispose()
    {
        _rsa.Dispose();
    }
}
