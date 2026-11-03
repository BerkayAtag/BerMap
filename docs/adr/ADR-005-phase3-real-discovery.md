# ADR-005: Real Discovery Adapters, Reconciliation, Snapshots and Collector Protocol (Phase 3)

## Status
Accepted (Phase 3 implementation complete; live AD/vCenter validation pending air-gapped target access).

## Context

BerMap Phase 1 and 2 established the modular monolith, PostgreSQL schema, red/white design, and a
simulation engine. Discovery, however, was simulated: the adapters returned hardcoded topology and the
ingestion path merged nodes on hostname alone.

Phase 3 must replace simulation with real, read-only discovery and reconciliation, and must make the
platform's operational contract explicit:

- Real Active Directory adapter: LDAP/LDAPS, multi-forest, DC/site/subnet/OU/computer/group/trusts, paging,
  read-only, strict TLS by default.
- Real VMware adapter: datacenters, clusters, ESXi hosts, VMs, datastores, networks/DVS, resource pools,
  HA/DRS, CPU/RAM/storage metrics, read-only.
- Multi-key reconciliation: FQDN, BIOS UUID, VM instance UUID, domain SID, IP+MAC, name+type+parent.
  Never merge on hostname alone; ambiguous matches create a `ReconciliationReview`.
- Immutable snapshots: point-in-time graph capture with SHA-256 hash, versioning, diff, integrity verify.
- Durable discovery queue: PostgreSQL-backed jobs claimed with `FOR UPDATE SKIP LOCKED`.
- Collector protocol: outbound-only, enrollment token, heartbeat, batch submission, offline buffer.

## Decision

1. **Real adapters replace simulated ones.** `ActiveDirectoryAdapter` and `VMwareAdapter` are the only
   registered adapters. Unknown adapter types are rejected by `IntegrationAdapterRegistry`. Adapters
   return explicit failures/warnings; they never fabricate success.
2. **Strict TLS by default.** Plaintext LDAP and untrusted certificates require explicit opt-in and emit
   warnings through `ConnectionTestResult.Warnings`.
3. **Reconciliation is multi-key and conservative.** `EntityReconciliationService` evaluates strong keys
   (FQDN, BIOS UUID, VM instance UUID, domain SID, IP+MAC) before name+type+parent. Name+type+parent never
   auto-merges; it creates a separate node and a pending review. Bookkeeping keys (`_lastReconciliation*`)
   are excluded from change detection so unchanged re-discovery reports `NodesUnchanged`.
4. **Snapshots are append-only.** `SnapshotService` captures `SnapshotNode`/`SnapshotRelationship` rows in a
   transaction, computes a canonical SHA-256 graph hash, and the `BerMapDbContext` rejects updates/deletes of
   snapshot rows.
5. **Discovery queue is durable.** Jobs are PostgreSQL queue entries claimed with `FOR UPDATE SKIP LOCKED` by
   `DiscoveryBackgroundService`. One job runs at a time per process.
6. **Collector registration is opt-in.** Registration requires `BerMap:Collectors:EnrollmentToken` and the
   `X-BerMap-Enrollment-Token` header. Batches require `DiscoveryResult.integrationId`; an ad-hoc completed
   `DiscoveryJob` is created when no job id is supplied.
7. **API contracts are documented.** Controllers expose real endpoints only; error responses use
   `application/problem+json` with `title`, `code`, and `traceId`.

## Consequences

- Backend builds with zero warnings; 34 unit tests pass; TypeScript build passes.
- Frontend pages (`IntegrationsPage`, `InventoryPage`, `SnapshotsPage`, `CollectorsPage`, `DashboardPage`)
  now call real endpoints and render loading/error/empty states instead of mock data.
- Light and dark themes verified in the browser.
- Live AD/vCenter discovery, migration application, and queue execution cannot be verified without a
  PostgreSQL instance and live target systems; this is documented as a known limitation.