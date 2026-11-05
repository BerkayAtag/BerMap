using System;
using System.IO;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace BerMap.Api.Security;

/// <summary>
/// Issues and validates compact HMAC-SHA256 signed bearer tokens
/// (<c>base64url(payload).base64url(signature)</c>). The signing key lives next
/// to the credential vault master key and never leaves the server, so no
/// external identity provider or package is required in air-gapped installs.
/// </summary>
public sealed class AccessTokenService
{
    public const string SigningKeyFileName = ".token-signing.key";

    /// <summary>Claim carrying the user's security stamp at the time the token was issued.</summary>
    public const string SecurityStampClaimType = "security_stamp";

    private readonly byte[] _signingKey;
    private readonly TimeSpan _lifetime;

    public AccessTokenService(string vaultDirectory, TimeSpan lifetime)
    {
        SecretFile.EnsureDirectory(vaultDirectory);
        var keyPath = Path.Combine(vaultDirectory, SigningKeyFileName);

        if (File.Exists(keyPath) && new FileInfo(keyPath).Length >= 32)
        {
            _signingKey = File.ReadAllBytes(keyPath);
        }
        else
        {
            _signingKey = RandomNumberGenerator.GetBytes(64);
            SecretFile.WriteAllBytes(keyPath, _signingKey);
        }

        _lifetime = lifetime;
    }

    public TimeSpan Lifetime => _lifetime;

    public (string Token, DateTime ExpiresAt) Issue(Guid userId, string username, string displayName, string role,
        string securityStamp)
    {
        var expiresAt = DateTime.UtcNow.Add(_lifetime);
        var payload = new TokenPayload(
            userId,
            username,
            displayName,
            role,
            new DateTimeOffset(expiresAt).ToUnixTimeSeconds(),
            securityStamp);

        var payloadSegment = Base64Url(JsonSerializer.SerializeToUtf8Bytes(payload));
        var signatureSegment = Base64Url(Sign(payloadSegment));
        return ($"{payloadSegment}.{signatureSegment}", expiresAt);
    }

    public ClaimsPrincipal? Validate(string token, string authenticationScheme)
    {
        var separator = token.IndexOf('.');
        if (separator <= 0 || separator == token.Length - 1)
            return null;

        var payloadSegment = token[..separator];
        byte[] signature;
        try
        {
            signature = FromBase64Url(token[(separator + 1)..]);
        }
        catch (FormatException)
        {
            return null;
        }

        if (!CryptographicOperations.FixedTimeEquals(signature, Sign(payloadSegment)))
            return null;

        TokenPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<TokenPayload>(FromBase64Url(payloadSegment));
        }
        catch (Exception ex) when (ex is JsonException or FormatException)
        {
            return null;
        }

        // Tokens issued before security stamps existed carry none and are no
        // longer accepted: they could not be revoked.
        if (payload == null || payload.Exp < DateTimeOffset.UtcNow.ToUnixTimeSeconds() ||
            string.IsNullOrEmpty(payload.Stamp))
            return null;

        var identity = new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, payload.Sub.ToString()),
            new Claim(ClaimTypes.Name, payload.Name),
            new Claim("display_name", payload.DisplayName),
            new Claim(ClaimTypes.Role, payload.Role),
            new Claim(SecurityStampClaimType, payload.Stamp)
        }, authenticationScheme);

        return new ClaimsPrincipal(identity);
    }

    private byte[] Sign(string payloadSegment) =>
        HMACSHA256.HashData(_signingKey, Encoding.ASCII.GetBytes(payloadSegment));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string value)
    {
        var base64 = value.Replace('-', '+').Replace('_', '/');
        base64 = base64.PadRight(base64.Length + (4 - base64.Length % 4) % 4, '=');
        return Convert.FromBase64String(base64);
    }

    private sealed record TokenPayload(Guid Sub, string Name, string DisplayName, string Role, long Exp, string? Stamp);
}
