using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BerMap.Application.DTOs;
using BerMap.Contracts;

namespace BerMap.Application.Interfaces;

public interface IIntegrationAdapterRegistry
{
    IReadOnlyList<AdapterDescriptorDto> GetDescriptors();
    IIntegrationAdapter? GetAdapter(string adapterType);
}

public interface IIntegrationService
{
    Task<List<IntegrationDto>> ListAsync(CancellationToken cancellationToken = default);
    Task<IntegrationDto?> GetAsync(Guid id, CancellationToken cancellationToken = default);
    Task<IntegrationDto> CreateAsync(CreateIntegrationRequest request, string actor, CancellationToken cancellationToken = default);
    Task<IntegrationDto?> UpdateAsync(Guid id, UpdateIntegrationRequest request, string actor, CancellationToken cancellationToken = default);
    Task<bool> DeleteAsync(Guid id, string actor, CancellationToken cancellationToken = default);
    Task<ConnectionTestResult> TestDraftAsync(TestConnectionRequest request, CancellationToken cancellationToken = default);
    Task<ConnectionTestResult?> TestAsync(Guid id, string? secretOverride, string actor, CancellationToken cancellationToken = default);
    Task<DiscoveryJobDto?> StartDiscoveryAsync(Guid id, StartDiscoveryRequest request, string actor, CancellationToken cancellationToken = default);
    Task<List<DiscoveryJobDto>> ListJobsAsync(Guid? integrationId, int limit, CancellationToken cancellationToken = default);
    Task<DiscoveryJobDto?> GetJobAsync(Guid id, CancellationToken cancellationToken = default);
    Task<CancelJobOutcome> CancelJobAsync(Guid jobId, string actor, CancellationToken cancellationToken = default);
}

public enum CancelJobOutcome
{
    NotFound,
    NotCancellable,
    Cancelled
}

public interface IDiscoveryJobRunner
{
    Task RunJobAsync(Guid jobId, CancellationToken cancellationToken = default);

    /// <summary>Requests cooperative cancellation of a running job. Returns false when the job is not active.</summary>
    bool RequestCancellation(Guid jobId);
}

public sealed class IntegrationValidationException : Exception
{
    public IntegrationValidationException(string code, string message) : base(message)
    {
        Code = code;
    }

    public string Code { get; }
}

public interface IDiscoveryIngestionService
{
    Task<IngestionResult> IngestAsync(
        Guid integrationId,
        Guid jobId,
        DiscoveryResult discovery,
        CancellationToken cancellationToken = default);

    /// <param name="isCompleteSnapshot">
    /// True only when <paramref name="discovery"/> is the complete, successful
    /// view of the source. Anything not seen in it is then marked stale. Failed
    /// or partial runs and intermediate collector batches must pass false, or a
    /// single outage would stale the whole inventory of that integration.
    /// </param>
    Task<IngestionResult> IngestAsync(
        Guid integrationId,
        Guid jobId,
        DiscoveryResult discovery,
        bool isCompleteSnapshot,
        CancellationToken cancellationToken = default);
}

public interface IInventoryService
{
    Task<InventoryPageDto> ListNodesAsync(InventoryQueryRequest request, CancellationToken cancellationToken = default);
    Task<InventoryNodeDetailDto?> GetNodeAsync(Guid id, CancellationToken cancellationToken = default);
    Task<List<ReconciliationReviewDto>> ListReviewsAsync(string? status, int limit, CancellationToken cancellationToken = default);
    Task<ReconciliationReviewDto?> ResolveReviewAsync(Guid id, ResolveReconciliationReviewRequest request, string actor, CancellationToken cancellationToken = default);
}

public interface IDashboardService
{
    Task<DashboardSummaryDto> GetSummaryAsync(CancellationToken cancellationToken = default);
}
