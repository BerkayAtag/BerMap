using System;
using System.Threading;
using System.Threading.Tasks;
using BerMap.Api.Security;
using BerMap.Infrastructure.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace BerMap.Tests.Unit;

/// <summary>
/// The host starts before the database is initialised: initialisation retries
/// in the background and /api answers 503 "starting" until it succeeds.
/// </summary>
public class StartupReadinessTests
{
    [Fact]
    public async Task Initialization_RetriesUntilItSucceeds_ThenSignalsReady()
    {
        var readiness = new StartupReadiness();
        var attempts = 0;
        var service = new DatabaseInitializationService(
            _ =>
            {
                if (++attempts < 3)
                    throw new TimeoutException("PostgreSQL is still starting.");
                return Task.CompletedTask;
            },
            readiness,
            NullLogger<DatabaseInitializationService>.Instance,
            initialDelay: TimeSpan.FromMilliseconds(1),
            maxDelay: TimeSpan.FromMilliseconds(5));

        await service.StartAsync(CancellationToken.None);
        await readiness.WaitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token);
        await service.StopAsync(CancellationToken.None);

        Assert.True(readiness.IsReady);
        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task Initialization_DoesNotBlockHostStart_AndStopsWhenTheHostStops()
    {
        var readiness = new StartupReadiness();
        var service = new DatabaseInitializationService(
            _ => throw new InvalidOperationException("Migration failed."),
            readiness,
            NullLogger<DatabaseInitializationService>.Instance,
            initialDelay: TimeSpan.FromMilliseconds(1),
            maxDelay: TimeSpan.FromMilliseconds(5));

        var start = service.StartAsync(CancellationToken.None);
        Assert.True(start.IsCompleted);
        await start;
        await Task.Delay(50);
        await service.StopAsync(CancellationToken.None);

        Assert.False(readiness.IsReady);
    }

    [Theory]
    [InlineData("/api/dashboard/summary", false, StatusCodes.Status503ServiceUnavailable)]
    [InlineData("/api/dashboard/summary", true, StatusCodes.Status200OK)]
    [InlineData("/healthz/ready", false, StatusCodes.Status200OK)]
    [InlineData("/index.html", false, StatusCodes.Status200OK)]
    [InlineData("/apidocs", false, StatusCodes.Status200OK)]
    public async Task Gate_BlocksOnlyApiRequests_UntilReady(string path, bool ready, int expectedStatus)
    {
        var readiness = new StartupReadiness();
        if (ready)
            readiness.MarkReady();

        var nextCalled = false;
        var gate = new StartupGateMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        }, readiness);
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Response.Body = new System.IO.MemoryStream();

        await gate.InvokeAsync(context);

        Assert.Equal(expectedStatus, context.Response.StatusCode);
        Assert.Equal(expectedStatus == StatusCodes.Status200OK, nextCalled);
        if (!nextCalled)
        {
            Assert.Equal("application/problem+json", context.Response.ContentType);
            context.Response.Body.Position = 0;
            var body = await new System.IO.StreamReader(context.Response.Body).ReadToEndAsync();
            Assert.Contains("\"code\":\"starting\"", body);
        }
    }
}
