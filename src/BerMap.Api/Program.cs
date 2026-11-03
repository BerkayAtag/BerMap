using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using BerMap.Api.Health;
using BerMap.Api.Security;
using BerMap.Application.Interfaces;
using BerMap.Application.Services;
using BerMap.Application.Services.Reconciliation;
using BerMap.Contracts;
using BerMap.Infrastructure.Data;
using BerMap.Infrastructure.Integrations;
using BerMap.Infrastructure.Repositories;
using BerMap.Infrastructure.Security;
using BerMap.Infrastructure.Services;
using BerMap.Integrations.ActiveDirectory;
using BerMap.Integrations.VMware;
using BerMap.Simulation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        options.JsonSerializerOptions.DictionaryKeyPolicy = JsonNamingPolicy.CamelCase;
        options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
    });

builder.Services.AddOpenApi();
builder.Services.AddHttpContextAccessor();
builder.Services.AddProblemDetails();

var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? "Host=localhost;Port=5432;Database=bermap_db;Username=postgres;Password=postgres";

builder.Services.AddDbContext<BerMapDbContext>(options =>
{
    options.UseNpgsql(connectionString, b => b.MigrationsAssembly("BerMap.Infrastructure"));
    if (builder.Environment.IsDevelopment())
        options.EnableDetailedErrors();
});

// Discovery adapters are stateless singletons. Only real, read-only adapters
// are registered; an unknown adapter type is rejected by the registry.
builder.Services.AddSingleton<IIntegrationAdapter, ActiveDirectoryAdapter>();
builder.Services.AddSingleton<IIntegrationAdapter, VMwareAdapter>();
builder.Services.AddSingleton<IIntegrationAdapterRegistry, IntegrationAdapterRegistry>();

var configuredVaultDirectory = builder.Configuration["BerMap:CredentialVault:Directory"];
// The default lives outside the application folder: a redeploy that replaces
// the binaries must not regenerate the master key and orphan stored secrets.
var defaultVaultDirectory = OperatingSystem.IsWindows()
    ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "BerMap", "vault")
    : "/var/lib/bermap/vault";
var vaultDirectory = Path.GetFullPath(string.IsNullOrWhiteSpace(configuredVaultDirectory)
    ? defaultVaultDirectory
    : configuredVaultDirectory, builder.Environment.ContentRootPath);

builder.Services.AddSingleton<ICredentialStore>(_ => new CredentialStore(vaultDirectory));

// Console sign-in: local accounts with PBKDF2 hashes and HMAC-signed bearer
// tokens. Every API endpoint requires an authenticated user unless it is
// explicitly marked [AllowAnonymous] (login, health probes, collector protocol).
var tokenLifetime = TimeSpan.FromMinutes(Math.Clamp(
    builder.Configuration.GetValue("BerMap:Auth:TokenLifetimeMinutes", 480), 5, 24 * 60));
builder.Services.AddSingleton(new AccessTokenService(vaultDirectory, tokenLifetime));
builder.Services
    .AddAuthentication(BearerTokenAuthenticationHandler.SchemeName)
    .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, BearerTokenAuthenticationHandler>(
        BearerTokenAuthenticationHandler.SchemeName, null);
builder.Services.AddAuthorization(options =>
{
    options.FallbackPolicy = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build();
    options.AddPolicy(AuthorizationPolicies.ManageIntegrations, policy => policy.RequireRole(
        nameof(BerMap.Domain.Enums.UserRole.PlatformAdministrator),
        nameof(BerMap.Domain.Enums.UserRole.IntegrationAdministrator)));
    options.AddPolicy(AuthorizationPolicies.Operate, policy => policy.RequireRole(
        nameof(BerMap.Domain.Enums.UserRole.PlatformAdministrator),
        nameof(BerMap.Domain.Enums.UserRole.IntegrationAdministrator),
        nameof(BerMap.Domain.Enums.UserRole.InfrastructureOperator)));
});
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("login", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});

builder.Services.AddScoped<IGraphService, GraphRepository>();
builder.Services.AddScoped<ISimulationEngine, SimulationEngine>();
builder.Services.AddScoped<ISnapshotService, SnapshotService>();
builder.Services.AddScoped<IEntityReconciliationService, EntityReconciliationService>();
builder.Services.AddScoped<IAuditService, AuditService>();
builder.Services.AddScoped<IIntegrationService, IntegrationService>();
builder.Services.AddScoped<IDiscoveryIngestionService, DiscoveryIngestionService>();
builder.Services.AddScoped<IInventoryService, InventoryService>();
builder.Services.AddScoped<IDashboardService, DashboardService>();
builder.Services.AddSingleton<IDiscoveryJobRunner, DiscoveryJobRunner>();
builder.Services.AddHostedService<DiscoveryBackgroundService>();

var allowedOrigins = builder.Configuration
    .GetSection("BerMap:Cors:AllowedOrigins")
    .Get<string[]>()?
    .Where(o => !string.IsNullOrWhiteSpace(o))
    .Distinct(StringComparer.OrdinalIgnoreCase)
    .ToArray();

if (allowedOrigins == null || allowedOrigins.Length == 0)
{
    // Development fallback only. Production deployments must set explicit
    // on-premises origins in BerMap:Cors:AllowedOrigins.
    allowedOrigins = builder.Environment.IsDevelopment()
        ? new[] { "http://localhost:5173", "https://localhost:5173", "http://localhost:3000" }
        : Array.Empty<string>();
}

builder.Services.AddCors(options =>
{
    options.AddPolicy("OnPremises", policy =>
    {
        if (allowedOrigins.Length == 0)
        {
            policy.WithOrigins("https://localhost");
        }
        else
        {
            policy.WithOrigins(allowedOrigins);
        }

        policy.AllowAnyHeader().AllowAnyMethod();
    });
});

builder.Services
    .AddHealthChecks()
    .AddCheck<DatabaseHealthCheck>("database", HealthStatus.Unhealthy, new[] { "ready" });

var app = builder.Build();

app.UseExceptionHandler(errorApp =>
{
    errorApp.Run(async context =>
    {
        var feature = context.Features.Get<IExceptionHandlerFeature>();
        var exception = feature?.Error;

        var (status, title, code) = exception switch
        {
            IntegrationValidationException { Code: "DISCOVERY_ALREADY_ACTIVE" or "REVIEW_NOT_PENDING" } conflict => (
                StatusCodes.Status409Conflict, conflict.Message, conflict.Code),
            IntegrationValidationException validation => (
                StatusCodes.Status400BadRequest, validation.Message, validation.Code),
            ArgumentException argument => (
                StatusCodes.Status400BadRequest, argument.Message, "invalid_request"),
            KeyNotFoundException notFound => (
                StatusCodes.Status404NotFound, notFound.Message, "not_found"),
            OperationCanceledException => (
                499, "The request was cancelled.", "cancelled"),
            _ => (
                StatusCodes.Status500InternalServerError,
                "An unexpected server error occurred.",
                "internal_error")
        };

        if (status >= StatusCodes.Status500InternalServerError)
        {
            app.Logger.LogError(exception, "Unhandled API exception for {Method} {Path}.",
                context.Request.Method, context.Request.Path);
        }
        else
        {
            app.Logger.LogWarning(exception, "API request rejected for {Method} {Path}: {Message}",
                context.Request.Method, context.Request.Path, exception?.Message);
        }

        context.Response.StatusCode = status;
        context.Response.ContentType = "application/problem+json";
        await context.Response.WriteAsJsonAsync(new
        {
            type = $"https://bermap.local/errors/{code}",
            title,
            status,
            code,
            traceId = context.TraceIdentifier
        });
    });
});

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi().AllowAnonymous();
}

await DatabaseInitializer.InitializeAsync(app.Services, app.Configuration, vaultDirectory, app.Logger);

app.UseDefaultFiles();
app.UseStaticFiles();

app.UseRouting();
app.UseCors("OnPremises");
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.MapHealthChecks("/healthz").AllowAnonymous();
app.MapHealthChecks("/healthz/ready", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready")
}).AllowAnonymous();
app.MapHealthChecks("/healthz/live", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    Predicate = _ => false
}).AllowAnonymous();

// Unknown /api routes must return 404 instead of the SPA shell.
app.Map("/api/{**path}", () => Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "API endpoint not found."))
    .AllowAnonymous();
app.MapFallbackToFile("index.html").AllowAnonymous();

app.Run();

// Exposed for WebApplicationFactory-based integration tests.
public partial class Program;
