using System.Text.Json;
using System.Text.Json.Serialization;
using BerMap.Api.Health;
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
using Microsoft.AspNetCore.Diagnostics;
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

builder.Services.AddSingleton<ICredentialStore>(_ =>
{
    var vaultDirectory = builder.Configuration["BerMap:CredentialVault:Directory"];
    return new CredentialStore(string.IsNullOrWhiteSpace(vaultDirectory) ? null : vaultDirectory);
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
    app.MapOpenApi();
}

app.UseCors("OnPremises");
app.UseRouting();
app.MapControllers();
app.MapHealthChecks("/healthz");
app.MapHealthChecks("/healthz/ready", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready")
});
app.MapHealthChecks("/healthz/live", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    Predicate = _ => false
});

app.UseDefaultFiles();
app.UseStaticFiles();
app.MapFallbackToFile("index.html");

app.Run();
