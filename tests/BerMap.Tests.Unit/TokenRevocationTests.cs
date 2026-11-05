using System;
using System.IO;
using System.Text.Encodings.Web;
using System.Threading.Tasks;
using BerMap.Api.Security;
using BerMap.Domain.Entities;
using BerMap.Domain.Enums;
using BerMap.Infrastructure.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BerMap.Tests.Unit;

/// <summary>
/// Bearer tokens carry the user's security stamp; rotating the stamp (password
/// reset or change, deactivation, role change) must revoke tokens issued before.
/// </summary>
public sealed class TokenRevocationTests : IDisposable
{
    private readonly string _vault = Path.Combine(Path.GetTempPath(), "bermap-revocation-" + Guid.NewGuid().ToString("N"));
    private readonly AccessTokenService _tokens;
    private readonly MemoryCache _cache = new(new MemoryCacheOptions());
    private readonly BerMapDbContext _context;
    private readonly User _user;

    public TokenRevocationTests()
    {
        _tokens = new AccessTokenService(_vault, TimeSpan.FromMinutes(30));
        _context = new BerMapDbContext(new DbContextOptionsBuilder<BerMapDbContext>()
            .UseInMemoryDatabase($"revocation-{Guid.NewGuid():N}")
            .Options);

        var role = new Role { Name = "InfrastructureOperator", RoleType = UserRole.InfrastructureOperator };
        _user = new User { Username = "operator", DisplayName = "Operator", PasswordHash = "x", IsActive = true };
        _user.Roles.Add(new UserRoleAssignment { User = _user, Role = role });
        _context.Users.Add(_user);
        _context.SaveChanges();
    }

    public void Dispose()
    {
        _context.Dispose();
        _cache.Dispose();
        if (Directory.Exists(_vault))
            Directory.Delete(_vault, true);
    }

    [Fact]
    public async Task Token_WithCurrentStamp_IsAccepted_WithTheCurrentRole()
    {
        var (token, _) = _tokens.Issue(_user.Id, _user.Username, _user.DisplayName, "ReadOnlyViewer", _user.SecurityStamp);

        var result = await AuthenticateAsync(token);

        Assert.True(result.Succeeded);
        Assert.True(result.Principal!.IsInRole(nameof(UserRole.InfrastructureOperator)));
    }

    [Fact]
    public async Task Token_IsRejected_OnceTheStampIsRotated()
    {
        var (token, _) = _tokens.Issue(_user.Id, _user.Username, _user.DisplayName, "InfrastructureOperator", _user.SecurityStamp);
        Assert.True((await AuthenticateAsync(token)).Succeeded);

        var oldStamp = _user.SecurityStamp;
        _user.RotateSecurityStamp();
        Assert.NotEqual(oldStamp, _user.SecurityStamp);
        await _context.SaveChangesAsync();
        BearerTokenAuthenticationHandler.InvalidateUserState(_cache, _user.Id);

        var revoked = await AuthenticateAsync(token);
        Assert.False(revoked.Succeeded);
        Assert.Contains("revoked", revoked.Failure!.Message);

        var (fresh, _) = _tokens.Issue(_user.Id, _user.Username, _user.DisplayName, "InfrastructureOperator", _user.SecurityStamp);
        Assert.True((await AuthenticateAsync(fresh)).Succeeded);
    }

    [Fact]
    public async Task Token_IsRejected_WhenItCarriesAnotherStamp()
    {
        var (token, _) = _tokens.Issue(_user.Id, _user.Username, _user.DisplayName, "InfrastructureOperator", "not-the-stamp");

        Assert.False((await AuthenticateAsync(token)).Succeeded);
    }

    [Fact]
    public void Token_WithoutStamp_IsRejected()
    {
        // Tokens issued before stamps existed have none and cannot be revoked.
        var (token, _) = _tokens.Issue(_user.Id, _user.Username, _user.DisplayName, "InfrastructureOperator", string.Empty);

        Assert.Null(_tokens.Validate(token, "test"));
    }

    [Fact]
    public async Task DisabledAccount_IsRejected()
    {
        var (token, _) = _tokens.Issue(_user.Id, _user.Username, _user.DisplayName, "InfrastructureOperator", _user.SecurityStamp);
        _user.IsActive = false;
        await _context.SaveChangesAsync();
        BearerTokenAuthenticationHandler.InvalidateUserState(_cache, _user.Id);

        Assert.False((await AuthenticateAsync(token)).Succeeded);
    }

    private async Task<AuthenticateResult> AuthenticateAsync(string token)
    {
        var handler = new BearerTokenAuthenticationHandler(
            new StaticOptionsMonitor(), NullLoggerFactory.Instance, UrlEncoder.Default, _tokens, _cache, _context);
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers.Authorization = "Bearer " + token;
        await handler.InitializeAsync(
            new AuthenticationScheme(BearerTokenAuthenticationHandler.SchemeName, null, typeof(BearerTokenAuthenticationHandler)),
            httpContext);
        return await handler.AuthenticateAsync();
    }

    private sealed class StaticOptionsMonitor : IOptionsMonitor<AuthenticationSchemeOptions>
    {
        public AuthenticationSchemeOptions CurrentValue { get; } = new();
        public AuthenticationSchemeOptions Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<AuthenticationSchemeOptions, string?> listener) => null;
    }
}
