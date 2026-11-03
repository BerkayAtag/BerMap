using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BerMap.Application.DTOs;

namespace BerMap.Application.Services;

public interface ISnapshotService
{
    Task<SnapshotSummaryDto> CreateSnapshotAsync(
        CreateSnapshotRequest request,
        string createdBy,
        CancellationToken cancellationToken = default);

    Task<List<SnapshotSummaryDto>> ListSnapshotsAsync(CancellationToken cancellationToken = default);

    Task<SnapshotSummaryDto?> GetSnapshotAsync(Guid id, CancellationToken cancellationToken = default);

    Task<SnapshotGraphDto?> GetSnapshotGraphAsync(Guid id, CancellationToken cancellationToken = default);

    Task<SnapshotDiffDto> CompareSnapshotsAsync(
        Guid snapshotAId,
        Guid snapshotBId,
        CancellationToken cancellationToken = default);

    Task<SnapshotVerificationDto?> VerifySnapshotAsync(Guid id, CancellationToken cancellationToken = default);
}
