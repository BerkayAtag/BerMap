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
}
