using System;
using System.Threading;
using System.Threading.Tasks;
using BerMap.Application.DTOs;

namespace BerMap.Application.Interfaces;

/// <summary>
/// Optional capability of an <see cref="IGraphService"/>: the neighbourhood an
/// impact simulation needs. Unlike the map's topology graph it follows only
/// live dependency and placement edges, and reads cluster membership only
/// around the target and the hosts it runs on, so Datacenter/Folder
/// containment never floods the node and edge caps. The simulation falls
/// back to the topology graph when the graph service does not offer it.
/// </summary>
public interface IImpactGraphSource
{
    Task<GraphQueryResultDto> GetImpactGraphAsync(Guid targetNodeId, int depth, CancellationToken cancellationToken = default);

    /// <summary>
    /// The same neighbourhood, built from a snapshot's frozen nodes and
    /// relationships instead of the live graph. Node and relationship ids are
    /// the original graph ids captured in the snapshot, so a live target id
    /// finds its frozen copy. Returns null when the snapshot does not exist;
    /// a target that is not in the snapshot yields an empty graph.
    /// </summary>
    Task<SnapshotImpactGraph?> GetSnapshotImpactGraphAsync(
        Guid snapshotId,
        Guid targetNodeId,
        int depth,
        CancellationToken cancellationToken = default);
}

/// <summary>An impact graph read from a snapshot, together with the snapshot it came from.</summary>
public sealed class SnapshotImpactGraph
{
    public Guid SnapshotId { get; set; }
    public string SnapshotName { get; set; } = string.Empty;
    public DateTime SnapshotCreatedAt { get; set; }
    public GraphQueryResultDto Graph { get; set; } = new();
}
