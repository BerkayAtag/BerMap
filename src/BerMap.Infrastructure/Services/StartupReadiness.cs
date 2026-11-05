using System;
using System.Threading;
using System.Threading.Tasks;

namespace BerMap.Infrastructure.Services;

/// <summary>
/// Signals that the database has been migrated and bootstrapped. The host
/// starts (and reports started to the Windows SCM or systemd) before that, so
/// the API and the background workers wait on this signal instead.
/// </summary>
public sealed class StartupReadiness
{
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool IsReady => _ready.Task.IsCompleted;

    public void MarkReady() => _ready.TrySetResult();

    /// <summary>Completes once the database is ready; throws when <paramref name="cancellationToken"/> fires first.</summary>
    public Task WaitAsync(CancellationToken cancellationToken) => _ready.Task.WaitAsync(cancellationToken);
}
