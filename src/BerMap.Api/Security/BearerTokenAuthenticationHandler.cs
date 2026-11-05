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

    // How long a user's active flag, role and security stamp are trusted
    // before the database is consulted again. This instance drops its cache
    // entry as soon as it changes them; other API instances notice within
    // this window instead of when the token expires.
    private static readonly TimeSpan UserStateCacheDuration = TimeSpan.FromSeconds(30);

    // Cached for missing or disabled accounts, so they are not re-read on every request.
    private static readonly UserState Rejected = new(null, string.Empty);

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

        var state = await GetUserStateAsync(userId);
        if (state.Role == null)
            return AuthenticateResult.Fail("The user account no longer exists or is disabled.");

        // A password reset or change, deactivation or role change replaces the
        // stamp, which revokes every token issued before it.
        if (!string.Equals(principal.FindFirstValue(AccessTokenService.SecurityStampClaimType), state.SecurityStamp,
                StringComparison.Ordinal))
            return AuthenticateResult.Fail("The access token has been revoked.");

        // Authorization uses the role the user has now, not the one in the token.
        var identity = (ClaimsIdentity)principal.Identity!;
        foreach (var claim in identity.FindAll(ClaimTypes.Role).ToList())
            identity.RemoveClaim(claim);
        identity.AddClaim(new Claim(ClaimTypes.Role, state.Role));

        return AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName));
    }

    private static string UserStateCacheKey(Guid userId) => "auth-user-state:" + userId;

    /// <summary>
    /// Drops the cached active flag, role and security stamp so the next
    /// request reads the database. Call it after changing any of them.
    /// </summary>
    public static void InvalidateUserState(IMemoryCache cache, Guid userId) => cache.Remove(UserStateCacheKey(userId));

    /// <summary>Returns the user's primary role (null when the account is missing or disabled) and security stamp.</summary>
    private async Task<UserState> GetUserStateAsync(Guid userId)
    {
        var cacheKey = UserStateCacheKey(userId);
        if (_cache.TryGetValue(cacheKey, out UserState? cached) && cached != null)
            return cached;

        var user = await _context.Users
            .AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => new { u.IsActive, u.SecurityStamp, Roles = u.Roles.Select(r => r.Role.RoleType).ToList() })
            .FirstOrDefaultAsync(Context.RequestAborted);

        var state = user is { IsActive: true }
            ? new UserState(user.Roles.DefaultIfEmpty(UserRole.ReadOnlyViewer).Min().ToString(), user.SecurityStamp)
            : Rejected;

        _cache.Set(cacheKey, state, UserStateCacheDuration);
        return state;
    }

    private sealed record UserState(string? Role, string SecurityStamp);

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
