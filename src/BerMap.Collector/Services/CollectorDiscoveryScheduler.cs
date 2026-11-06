using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BerMap.Collector.Services;

/// <summary>
/// Starts the configured collector integrations on their cron schedules
/// (collector local time). An integration never runs twice at once; at most
/// <see cref="MaxParallelRuns"/> different integrations run in parallel.
/// Discovery does not wait for the server: batches of a run made while it is
/// unreachable go to the offline queue. Invalid entries are reported to the
/// server once, as a failed run, when their IntegrationId is usable.
/// </summary>
public sealed class CollectorDiscoveryScheduler : BackgroundService
{
    public const int MaxParallelRuns = 2;
    private static readonly TimeSpan MaxTickInterval = TimeSpan.FromSeconds(30);

    private readonly CollectorIntegrationSet _integrations;
    private readonly ICollectorDiscoveryRunner _runner;
    private readonly ICollectorClient _client;
    private readonly TimeProvider _timeProvider;
    private readonly TimeZoneInfo _timeZone;
    private readonly ILogger<CollectorDiscoveryScheduler> _logger;
    private readonly SemaphoreSlim _parallelRuns = new(MaxParallelRuns, MaxParallelRuns);
    private readonly ConcurrentDictionary<Guid, Task> _running = new();
    private readonly Dictionary<Guid, DateTime?> _nextRunUtc = new();
    private readonly object _scheduleLock = new();
    private bool _initialized;

    public CollectorDiscoveryScheduler(
        CollectorIntegrationSet integrations,
        ICollectorDiscoveryRunner runner,
        ICollectorClient client,
        ILogger<CollectorDiscoveryScheduler> logger,
        TimeProvider? timeProvider = null,
        TimeZoneInfo? timeZone = null)
    {
        _integrations = integrations;
        _runner = runner;
        _client = client;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _timeZone = timeZone ?? TimeZoneInfo.Local;
    }

    /// <summary>Runs in progress (or waiting for a parallel slot), by integration id.</summary>
    public IReadOnlyCollection<Guid> RunningIntegrations => _running.Keys.ToList();

    /// <summary>Next planned start of an integration (UTC), or null when it never runs again.</summary>
    public DateTime? GetNextRunUtc(Guid integrationId)
    {
        lock (_scheduleLock)
            return _nextRunUtc.GetValueOrDefault(integrationId);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_integrations.Integrations.Count == 0 && _integrations.Errors.Count == 0)
        {
            _logger.LogInformation("No collector integrations are configured (BerMap:Collector:Integrations); discovery is idle.");
            return;
        }

        await ReportConfigurationErrorsAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                Tick(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error in the collector discovery scheduler");
            }

            try
            {
                await Task.Delay(DelayUntilNextTick(), _timeProvider, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        // Graceful shutdown: the runs observe the cancelled token; wait for them to unwind.
        await WhenIdleAsync();
        _logger.LogInformation("Collector discovery scheduler stopped.");
    }

    /// <summary>
    /// Starts every integration that is due and not already running. The first
    /// tick plans each integration: due now with RunOnStart, otherwise at its
    /// next cron occurrence. Returns the integrations started by this tick.
    /// </summary>
    public IReadOnlyList<Guid> Tick(CancellationToken stoppingToken)
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var started = new List<Guid>();

        lock (_scheduleLock)
        {
            if (!_initialized)
            {
                foreach (var integration in _integrations.Integrations)
                {
                    _nextRunUtc[integration.IntegrationId] = integration.RunOnStart
                        ? now
                        : integration.Schedule.GetNextOccurrence(now, _timeZone);
                }
                _initialized = true;
            }

            foreach (var integration in _integrations.Integrations)
            {
                if (_nextRunUtc.GetValueOrDefault(integration.IntegrationId) is not { } due || due > now)
                    continue;

                // Plan the next occurrence now, so an occurrence that falls while
                // this run is still busy is skipped instead of queued up.
                _nextRunUtc[integration.IntegrationId] = integration.Schedule.GetNextOccurrence(now, _timeZone);

                if (_running.ContainsKey(integration.IntegrationId))
                {
                    _logger.LogWarning(
                        "Integration {IntegrationId} is still running; the run due at {Due:u} is skipped.",
                        integration.IntegrationId, due);
                    continue;
                }

                // Registered before it starts, so the run's own clean-up can never
                // happen before the registration (which would leave a stale entry).
                var start = new Task<Task>(() => RunGuardedAsync(integration, stoppingToken));
                if (!_running.TryAdd(integration.IntegrationId, start.Unwrap()))
                    continue;
                start.Start(TaskScheduler.Default);
                started.Add(integration.IntegrationId);
            }
        }

        return started;
    }

    /// <summary>Completes when no run is in progress.</summary>
    public async Task WhenIdleAsync()
    {
        while (!_running.IsEmpty)
        {
            try
            {
                await Task.WhenAll(_running.Values.ToList());
            }
            catch
            {
                // RunGuardedAsync never throws; nothing to observe.
            }
        }
    }

    private async Task RunGuardedAsync(CollectorIntegration integration, CancellationToken stoppingToken)
    {
        var acquired = false;
        try
        {
            await _parallelRuns.WaitAsync(stoppingToken);
            acquired = true;
            await _runner.RunAsync(integration, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Stopping while waiting for a slot.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Discovery run for integration {IntegrationId} crashed", integration.IntegrationId);
        }
        finally
        {
            if (acquired)
                _parallelRuns.Release();
            _running.TryRemove(integration.IntegrationId, out _);
        }
    }

    private TimeSpan DelayUntilNextTick()
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        DateTime? next;
        lock (_scheduleLock)
            next = _nextRunUtc.Values.Where(v => v.HasValue).Select(v => v!.Value).DefaultIfEmpty(DateTime.MaxValue).Min();

        var delay = next is { } value && value != DateTime.MaxValue ? value - now : MaxTickInterval;
        if (delay < TimeSpan.FromSeconds(1))
            delay = TimeSpan.FromSeconds(1);
        // Re-check at least every 30 seconds: wall-clock jumps and DST shifts.
        return delay > MaxTickInterval ? MaxTickInterval : delay;
    }

    private async Task ReportConfigurationErrorsAsync(CancellationToken stoppingToken)
    {
        foreach (var error in _integrations.Errors)
        {
            _logger.LogError("BerMap:Collector:Integrations:{Index} is ignored: {Message}", error.Index, error.Message);
            if (error.IntegrationId is not { } integrationId || stoppingToken.IsCancellationRequested)
                continue;

            try
            {
                var batch = CollectorDiscoveryRunner.BuildFailureBatch(
                    integrationId, new[] { $"COLLECTOR_CONFIG_INVALID: {error.Message}" });
                await _client.SubmitSnapshotBatchAsync(batch, Guid.NewGuid(), 0, final: true, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Could not report the configuration error of integration {IntegrationId}: {Message}",
                    integrationId, ex.Message);
            }
        }
    }
}
