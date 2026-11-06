using System.Collections.Generic;

namespace BerMap.Simulation;

/// <summary>
/// A simulation input that does not exist: an unknown snapshot, or a target
/// that is not part of the chosen snapshot. It is a <see cref="KeyNotFoundException"/>
/// (404) that also carries a machine-readable code, so the console can tell
/// "snapshot gone" from "target not captured in that snapshot".
/// </summary>
public sealed class SimulationNotFoundException : KeyNotFoundException
{
    public const string SnapshotNotFoundCode = "snapshot_not_found";
    public const string NotInSnapshotCode = "not_in_snapshot";

    public SimulationNotFoundException(string code, string message) : base(message)
    {
        Code = code;
    }

    public string Code { get; }
}
