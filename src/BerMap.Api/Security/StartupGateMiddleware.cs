using System;
using System.Threading.Tasks;
using BerMap.Infrastructure.Services;
using Microsoft.AspNetCore.Http;

namespace BerMap.Api.Security;

/// <summary>
/// Answers /api requests with 503 (code "starting") until the database is
/// migrated and bootstrapped. Health probes and the static console are not
/// gated, so the service manager and load balancers can see the state.
/// </summary>
public sealed class StartupGateMiddleware
{
    private readonly RequestDelegate _next;
    private readonly StartupReadiness _readiness;

    public StartupGateMiddleware(RequestDelegate next, StartupReadiness readiness)
    {
        _next = next;
        _readiness = readiness;
    }

    public Task InvokeAsync(HttpContext context)
    {
        if (_readiness.IsReady || !context.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase))
            return _next(context);

        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        context.Response.Headers.RetryAfter = "5";
        return context.Response.WriteAsJsonAsync(new
        {
            type = "https://bermap.local/errors/starting",
            title = "The server is starting; the database is not ready yet.",
            status = StatusCodes.Status503ServiceUnavailable,
            code = "starting",
            traceId = context.TraceIdentifier
        }, options: null, contentType: "application/problem+json");
    }
}
