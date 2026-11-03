using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BerMap.Application.DTOs;
using BerMap.Domain.Entities;

namespace BerMap.Application.Interfaces;

public interface IGraphService
{
    Task<GraphQueryResultDto> GetTopologyGraphAsync(Guid? centerNodeId = null, int depth = 2, CancellationToken cancellationToken = default);
    Task<List<InfrastructureNodeDto>> SearchNodesAsync(string query, CancellationToken cancellationToken = default);
    Task<InfrastructureNodeDto?> GetNodeDetailsAsync(Guid id, CancellationToken cancellationToken = default);
}

public interface ISimulationEngine
{
    Task<ImpactAnalysisResultDto> RunSimulationAsync(SimulationRunRequest request, CancellationToken cancellationToken = default);
}

public interface IAuditService
{
    Task LogAsync(string userId, string username, string action, string entityType, string entityId, string detailsJson, string ipAddress, bool success = true, CancellationToken cancellationToken = default);
}

public interface IRepository<T> where T : Entity
{
    Task<T?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<T>> ListAllAsync(CancellationToken cancellationToken = default);
    Task<T> AddAsync(T entity, CancellationToken cancellationToken = default);
    Task UpdateAsync(T entity, CancellationToken cancellationToken = default);
    Task DeleteAsync(T entity, CancellationToken cancellationToken = default);
}
