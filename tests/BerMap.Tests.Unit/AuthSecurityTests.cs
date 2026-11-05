using System;
using System.IO;
using System.Security.Claims;
using BerMap.Api.Security;
using Xunit;

namespace BerMap.Tests.Unit;

public class AuthSecurityTests
{
    [Fact]
    public void PasswordHasher_VerifiesOnlyTheOriginalPassword()
    {
        var hash = PasswordHasher.Hash("Correct-Horse-1");

        Assert.StartsWith("pbkdf2-sha256$", hash);
        Assert.True(PasswordHasher.Verify("Correct-Horse-1", hash));
        Assert.False(PasswordHasher.Verify("correct-horse-1", hash));
        Assert.False(PasswordHasher.Verify("Correct-Horse-1", "not-a-hash"));
        Assert.NotEqual(hash, PasswordHasher.Hash("Correct-Horse-1"));
    }

    [Fact]
    public void AccessToken_RoundTripsClaims_AndRejectsTampering()
    {
        var vault = Path.Combine(Path.GetTempPath(), "bermap-token-" + Guid.NewGuid().ToString("N"));
        try
        {
            var service = new AccessTokenService(vault, TimeSpan.FromMinutes(30));
            var userId = Guid.NewGuid();
            var (token, _) = service.Issue(userId, "admin", "Platform Administrator", "PlatformAdministrator", "stamp-1");

            var principal = service.Validate(token, "test");
            Assert.NotNull(principal);
            Assert.Equal(userId.ToString(), principal!.FindFirstValue(ClaimTypes.NameIdentifier));
            Assert.True(principal.IsInRole("PlatformAdministrator"));
            Assert.Equal("stamp-1", principal.FindFirstValue(AccessTokenService.SecurityStampClaimType));

            var tampered = token[..^2] + (token[^2] == 'A' ? "BB" : "AA");
            Assert.Null(service.Validate(tampered, "test"));
            Assert.Null(service.Validate("garbage", "test"));

            // A token signed with a different key (another installation) is rejected.
            var other = new AccessTokenService(vault + "-other", TimeSpan.FromMinutes(30));
            Assert.Null(other.Validate(token, "test"));
            Directory.Delete(vault + "-other", true);
        }
        finally
        {
            Directory.Delete(vault, true);
        }
    }

    [Fact]
    public void AccessToken_Expired_IsRejected()
    {
        var vault = Path.Combine(Path.GetTempPath(), "bermap-token-" + Guid.NewGuid().ToString("N"));
        try
        {
            var service = new AccessTokenService(vault, TimeSpan.FromSeconds(-5));
            var (token, _) = service.Issue(Guid.NewGuid(), "admin", "Admin", "ReadOnlyViewer", "stamp");
            Assert.Null(service.Validate(token, "test"));
        }
        finally
        {
            Directory.Delete(vault, true);
        }
    }
}
