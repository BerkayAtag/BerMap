using System;
using System.Linq;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Threading.Tasks;
using BerMap.Domain.Enums;
using BerMap.Infrastructure.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BerMap.Api.Security;

public sealed class BearerTokenAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "BerMapBearer";

    // How long a user's active flag and role are trusted before the database
    // is consulted again. Disabling an account or changing a role takes
    // effect within this window instead of when the token expires.
    private static readonly TimeSpan UserStateCacheDuration = TimeSpan.FromSeconds(30);

    private readonly AccessTokenService _tokens;
    private readonly IMemoryCache _cache;
    private readonly BerMapDbContext _context;

    public BearerTokenAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        AccessTokenService tokens,
        IMemoryCache cache,
        BerMapDbContext context)
        : base(options, logger, encoder)
    {
        _tokens = tokens;
        _cache = cache;
        _context = context;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (string.IsNullOrEmpty(header) || !header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return AuthenticateResult.NoResult();

        var principal = _tokens.Validate(header["Bearer ".Length..].Trim(), SchemeName);
        if (principal == null || !Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
            return AuthenticateResult.Fail("Invalid or expired access token.");

        var currentRole = await GetCurrentRoleAsync(userId);
        if (currentRole == null)
            return AuthenticateResult.Fail("The user account no longer exists or is disabled.");

        // Authorization uses the role the user has now, not the one in the token.
        var identity = (ClaimsIdentity)principal.Identity!;
        foreach (var claim in identity.FindAll(ClaimTypes.Role).ToList())
            identity.RemoveClaim(claim);
        identity.AddClaim(new Claim(ClaimTypes.Role, currentRole));

        return AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName));
    }

    private static string UserStateCacheKey(Guid userId) => "auth-user-state:" + userId;

    /// <summary>Drops the cached active flag/role so the next request reads the database.</summary>
    public static void InvalidateUserState(IMemoryCache cache, Guid userId) => cache.Remove(UserStateCacheKey(userId));

    /// <summary>Returns the user's primary role, or null when the account is missing or disabled.</summary>
    private async Task<string?> GetCurrentRoleAsync(Guid userId)
    {
        var cacheKey = UserStateCacheKey(userId);
        if (_cache.TryGetValue(cacheKey, out string? cached))
            return cached == string.Empty ? null : cached;

        var user = await _context.Users
            .AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => new { u.IsActive, Roles = u.Roles.Select(r => r.Role.RoleType).ToList() })
            .FirstOrDefaultAsync(Context.RequestAborted);

        string? role = user is { IsActive: true }
            ? user.Roles.DefaultIfEmpty(UserRole.ReadOnlyViewer).Min().ToString()
            : null;

        _cache.Set(cacheKey, role ?? string.Empty, UserStateCacheDuration);
        return role;
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.ContentType = "application/problem+json";
        return Response.WriteAsJsonAsync(new
        {
            type = "https://bermap.local/errors/unauthorized",
            title = "Authentication is required.",
            status = StatusCodes.Status401Unauthorized,
            code = "unauthorized",
            traceId = Context.TraceIdentifier
        });
    }

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status403Forbidden;
        Response.ContentType = "application/problem+json";
        return Response.WriteAsJsonAsync(new
        {
            type = "https://bermap.local/errors/forbidden",
            title = "You do not have permission to perform this action.",
            status = StatusCodes.Status403Forbidden,
            code = "forbidden",
            traceId = Context.TraceIdentifier
        });
    }
}
