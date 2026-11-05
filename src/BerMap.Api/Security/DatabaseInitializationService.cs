using System;
using System.Threading;
using System.Threading.Tasks;
using BerMap.Infrastructure.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BerMap.Api.Security;

/// <summary>
/// Runs <see cref="DatabaseInitializer"/> after the host has started, retrying
/// with backoff until it succeeds, then signals <see cref="StartupReadiness"/>.
/// Initialising before <c>app.Run()</c> kept the Windows service and the
/// systemd unit from reporting started while PostgreSQL was still unreachable
/// or a long migration ran, and the service manager gave up on the start.
/// </summary>
public sealed class DatabaseInitializationService : BackgroundService
{
    private readonly Func<CancellationToken, Task> _initialize;
    private readonly StartupReadiness _readiness;
    private readonly ILogger<DatabaseInitializationService> _logger;
    private readonly TimeSpan _initialDelay;
    private readonly TimeSpan _maxDelay;

    public DatabaseInitializationService(
        Func<CancellationToken, Task> initialize,
        StartupReadiness readiness,
        ILogger<DatabaseInitializationService> logger,
        TimeSpan? initialDelay = null,
        TimeSpan? maxDelay = null)
    {
        _initialize = initialize;
        _readiness = readiness;
        _logger = logger;
        _initialDelay = initialDelay ?? TimeSpan.FromSeconds(2);
        _maxDelay = maxDelay ?? TimeSpan.FromSeconds(30);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Let the host finish starting before the first, possibly slow, attempt.
        await Task.Yield();

        var delay = _initialDelay;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await _initialize(stoppingToken);
                _readiness.MarkReady();
                _logger.LogInformation("Database is ready (attempt {Attempt}).", attempt);
                return;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (IsTransient(ex))
            {
                // PostgreSQL may still be starting (service start order after
                // a reboot, containers starting together).
                _logger.LogWarning("Database is not reachable yet (attempt {Attempt}): {Message}. Retrying in {Delay}s.",
                    attempt, ex.Message, delay.TotalSeconds);
            }
            catch (Exception ex)
            {
                // A failed migration or bootstrap is retried as well: the API
                // keeps answering health probes, which report it as not ready.
                _logger.LogError(ex, "Database initialisation failed (attempt {Attempt}). Retrying in {Delay}s.",
                    attempt, delay.TotalSeconds);
            }

            try
            {
                await Task.Delay(delay, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, _maxDelay.Ticks));
        }
    }

    private static bool IsTransient(Exception ex) =>
        ex is Npgsql.NpgsqlException or System.Net.Sockets.SocketException or TimeoutException ||
        ex.InnerException is Npgsql.NpgsqlException or System.Net.Sockets.SocketException or TimeoutException;
}
