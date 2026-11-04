using BerMap.Domain.Enums;

namespace BerMap.Application.Services.Integrations;

/// <summary>
/// The integration health shown when nothing is running. An enabled
/// integration is Active unless its last test or discovery failed; a disabled
/// one is always Inactive. Test and discovery results set Error/Degraded
/// themselves; this rule decides what a toggle or an idle state shows.
/// </summary>
public static class IntegrationStatusRules
{
    public static IntegrationStatus Resting(bool isEnabled, string? lastError)
    {
        if (!isEnabled)
            return IntegrationStatus.Inactive;

        return string.IsNullOrWhiteSpace(lastError) ? IntegrationStatus.Active : IntegrationStatus.Error;
    }

    /// <summary>
    /// The status a finished test or discovery leaves behind: its outcome
    /// (Active, Degraded or Error) while the integration is enabled, Inactive
    /// once it was disabled in the meantime. The error itself stays in
    /// LastError, so re-enabling shows it again.
    /// </summary>
    public static IntegrationStatus AfterRun(bool isEnabled, IntegrationStatus outcome) =>
        isEnabled ? outcome : IntegrationStatus.Inactive;
}
