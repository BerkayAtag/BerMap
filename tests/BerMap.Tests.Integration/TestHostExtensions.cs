using BerMap.Infrastructure.Services;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace BerMap.Tests.Integration;

internal static class TestHostExtensions
{
    /// <summary>
    /// Starts the host and waits until the background initialisation has
    /// migrated and bootstrapped the database. Until then /api answers 503.
    /// </summary>
    public static async Task WaitUntilReadyAsync(this WebApplicationFactory<Program> factory)
    {
        factory.CreateClient().Dispose();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        await factory.Services.GetRequiredService<StartupReadiness>().WaitAsync(timeout.Token);
    }
}
