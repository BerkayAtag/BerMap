using System.Text.Encodings.Web;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BerMap.Api.Security;

public sealed class BearerTokenAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "BerMapBearer";

    private readonly AccessTokenService _tokens;

    public BearerTokenAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        AccessTokenService tokens)
        : base(options, logger, encoder)
    {
        _tokens = tokens;
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (string.IsNullOrEmpty(header) || !header.StartsWith("Bearer ", System.StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(AuthenticateResult.NoResult());

        var principal = _tokens.Validate(header["Bearer ".Length..].Trim(), SchemeName);
        return Task.FromResult(principal == null
            ? AuthenticateResult.Fail("Invalid or expired access token.")
            : AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName)));
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
