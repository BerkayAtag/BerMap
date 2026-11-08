# Phase 3 API Contract

All endpoints are under `/api` unless noted. Errors use `application/problem+json` with `type`, `title`,
`status`, `code` and `traceId`. Secrets are never returned by the API; configuration JSON is sanitized on
output.

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
| `DELETE` | `/collectors/{id}` | Remove a collector (its discovered data stays) |
| `POST` | `/collectors/register` | Register a collector (requires `X-BerMap-Enrollment-Token`) |
| `POST` | `/collectors/heartbeat` | Collector heartbeat (requires `X-BerMap-Enrollment-Token`) |
| `POST` | `/collectors/submit-batch?final={bool}&jobId={jobId}` | Submit a discovery batch (requires `X-BerMap-Enrollment-Token`) |

## Other

| Method | Path | Description |
|---|---|---|
| `GET` | `/dashboard/summary` | Dashboard aggregation |
| `POST` | `/simulations/run` | Run impact analysis on the live graph, or on a snapshot with `snapshotId` |
| `GET` | `/healthz`, `/healthz/ready`, `/healthz/live` | Health checks (outside `/api`) |

## Notes

- Integration updates: changing an endpoint-identifying setting (`server`, `host`, `port`, `url`,
  `username`, `domain`, `additionalDomains`, `useSsl`, `useStartTls`, `allowPlaintext`,
  `allowUntrustedCertificate`, `ignoreSslErrors`) of an integration that has a stored credential requires
  `secret` (or `clearSecret: true`) in the same request; otherwise `400 SECRET_REQUIRED_FOR_ENDPOINT_CHANGE`.
  A test or discovery with an explicit secret in its request body is unaffected.
- Collector batches require `DiscoveryResult.integrationId`; the integration must be enabled and
  collector-managed (otherwise `409 INTEGRATION_DISABLED` / `NOT_COLLECTOR_MANAGED`).
- Collector job protocol: a batch without `jobId` opens a new `Running` collector job (and fails any
  collector job of that integration that received no batch for 30 minutes). Further batches of the same snapshot
  send `?jobId=` from the first response. The job stays `Running` until a batch with `final=true`
  completes it: `Completed` when no batch reported errors, `PartialSuccess` with errors and data,
  `Failed` with errors and nothing collected. A single-batch snapshot sends `final=true` without `jobId`.
  Only a complete, error-free final batch marks objects missing from the whole job as stale.
- A batch needs nodes or relationships: relationships may be sent in their own chunks after the node
  batches (endpoints are resolved through the integration's source links). An empty batch is `400`
  unless it is a final batch with `success=false`, which reports a failed run.
- A batch that cannot be ingested answers `500`, fails the job and sets the integration to `Error`
  with `INGESTION_FAILED: ...` as its last error. A request body over the server limit answers
  `413 request_body_too_large`.
- A collector is `Offline` after `BerMap:Collectors:HeartbeatTimeoutSeconds` (default 90) without a
  heartbeat.
- Collector enrollment is disabled unless `BerMap:Collectors:EnrollmentToken` is configured.
- Snapshot rows are append-only; update/delete is rejected by the DbContext. Snapshot compare ignores
  node property keys starting with `_` (reconciliation bookkeeping); the stored hash covers them.
- Reconciliation reviews are never auto-resolved. `Merged` merges the discovered node into
  `targetNodeId`, or into the review's first candidate when `targetNodeId` is omitted.
