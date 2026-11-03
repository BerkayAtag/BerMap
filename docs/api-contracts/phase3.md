# Phase 3 API Contract

All endpoints are under `/api`. Errors use `application/problem+json` with `title`, `code`, and `traceId`.
Secrets are never returned by the API; configuration JSON is sanitized on output.

## Integrations

| Method | Path | Description |
|---|---|---|
| `GET` | `/integrations` | List non-deleted integrations |
| `GET` | `/integrations/adapters` | List registered adapter descriptors |
| `GET` | `/integrations/{id}` | Get one integration |
| `POST` | `/integrations` | Create integration |
| `PUT` | `/integrations/{id}` | Update integration |
| `DELETE` | `/integrations/{id}` | Soft-delete integration |
| `POST` | `/integrations/test-connection` | Test a draft configuration |
| `POST` | `/integrations/{id}/test` | Test a saved integration |
| `POST` | `/integrations/{id}/discover` | Queue a discovery job |
| `GET` | `/integrations/jobs` | List discovery jobs |
| `GET` | `/integrations/jobs/{jobId}` | Get one discovery job |
| `POST` | `/integrations/jobs/{jobId}/cancel` | Cancel a queued/running job |

## Inventory & Reconciliation

| Method | Path | Description |
|---|---|---|
| `GET` | `/inventory/nodes` | Paginated inventory with query/nodeType/status/isStale/needsReview filters |
| `GET` | `/inventory/nodes/{id}` | Node detail with source links, relationships, reviews |
| `GET` | `/inventory/reviews` | List reconciliation reviews |
| `POST` | `/inventory/reviews/{id}/resolve` | Resolve a review (`Merged` or `Dismissed`) |

## Snapshots

| Method | Path | Description |
|---|---|---|
| `GET` | `/snapshots` | List snapshots |
| `POST` | `/snapshots` | Create a snapshot |
| `GET` | `/snapshots/{id}` | Get snapshot summary |
| `GET` | `/snapshots/{id}/graph` | Get snapshot graph |
| `GET` | `/snapshots/compare?a={a}&b={b}` | Diff two snapshots |
| `GET` | `/snapshots/{id}/verify` | Verify snapshot integrity |

## Collectors

| Method | Path | Description |
|---|---|---|
| `GET` | `/collectors` | List registered collectors with live metrics |
| `POST` | `/collectors/heartbeat` | Collector heartbeat |
| `POST` | `/collectors/submit-batch` | Submit discovery batch |
| `POST` | `/collectors/register` | Register a collector (requires `X-BerMap-Enrollment-Token`) |

## Other

| Method | Path | Description |
|---|---|---|
| `GET` | `/dashboard/summary` | Dashboard aggregation |
| `GET` | `/healthz` | Health checks |
| `POST` | `/simulations` | Run impact analysis |

## Notes

- Collector batches require `DiscoveryResult.integrationId`. An ad-hoc completed `DiscoveryJob` is
  created when no job id is supplied.
- Collector enrollment is disabled unless `BerMap:Collectors:EnrollmentToken` is configured.
- Snapshot rows are append-only; update/delete is rejected by the DbContext.
- Reconciliation reviews are never auto-resolved; `Merged` requires an explicit target node id.