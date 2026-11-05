using System;
using BerMap.Domain.Enums;

namespace BerMap.Application.Services.Integrations;

/// <summary>
/// The integration health shown when nothing is running. An enabled
/// integration shows the outcome of its last test or discovery (Active,
/// Degraded or Error); a disabled one is always Inactive. Test and discovery
/// results set Error/Degraded themselves; this rule decides what a toggle, a
/// cancelled run or an idle state shows.
/// </summary>
public static class IntegrationStatusRules
{
    /// <param name="lastOutcome">
    /// What the last finished test or discovery left behind (see
    /// <see cref="LastOutcome"/>); null when nothing ran or the history was reset.
    /// </param>
    public static IntegrationStatus Resting(bool isEnabled, IntegrationStatus? lastOutcome)
    {
        if (!isEnabled)
            return IntegrationStatus.Inactive;

        return lastOutcome is IntegrationStatus.Error or IntegrationStatus.Degraded
            ? lastOutcome.Value
            : IntegrationStatus.Active;
    }

    /// <summary>
    /// The outcome of the most recent finished test or discovery job: a
    /// partial job is Degraded, a failed test or job is Error. LastError is
    /// cleared by every success and by a mode switch, so without it nothing has
    /// failed since. An error with no recorded run behind it (an interrupted
    /// server, for example) counts as Error. Cancelled, queued and running jobs
    /// prove nothing and are passed as null by the caller.
    /// </summary>
    public static IntegrationStatus? LastOutcome(
        string? lastError,
        DateTime? lastTestAt,
        bool? lastTestSuccess,
        DiscoveryJobStatus? lastJobStatus,
        DateTime? lastJobCompletedAt)
    {
        if (string.IsNullOrWhiteSpace(lastError))
            return IntegrationStatus.Active;

        IntegrationStatus? testOutcome = lastTestAt.HasValue && lastTestSuccess.HasValue
            ? lastTestSuccess.Value ? IntegrationStatus.Active : IntegrationStatus.Error
            : null;
        var jobOutcome = lastJobCompletedAt.HasValue ? OutcomeOf(lastJobStatus) : null;

        if (testOutcome.HasValue && jobOutcome.HasValue)
            return lastTestAt!.Value >= lastJobCompletedAt!.Value ? testOutcome : jobOutcome;

        return testOutcome ?? jobOutcome ?? IntegrationStatus.Error;
    }

    /// <summary>The integration status a finished job stands for; null for jobs that prove nothing.</summary>
    public static IntegrationStatus? OutcomeOf(DiscoveryJobStatus? jobStatus) => jobStatus switch
    {
        DiscoveryJobStatus.Completed => IntegrationStatus.Active,
        DiscoveryJobStatus.PartialSuccess => IntegrationStatus.Degraded,
        DiscoveryJobStatus.Failed => IntegrationStatus.Error,
        _ => null,
    };

    /// <summary>
    /// The status a finished test or discovery leaves behind: its outcome
    /// (Active, Degraded or Error) while the integration is enabled, Inactive
    /// once it was disabled in the meantime. The error itself stays in
    /// LastError, so re-enabling shows it again.
    /// </summary>
    public static IntegrationStatus AfterRun(bool isEnabled, IntegrationStatus outcome) =>
        isEnabled ? outcome : IntegrationStatus.Inactive;
}
