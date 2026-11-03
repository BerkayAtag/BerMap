# ADR-003: PostgreSQL as Primary Relational & Graph Datastore

## Status
Accepted

## Context
BerMap requires persistent storage for enterprise inventory, users, roles, audit trails, and configuration, while also needing graph traversal capabilities for dependency mapping, blast-radius impact analysis, and Digital Twin simulations.

Introducing a specialized graph database (e.g., Neo4j) or dedicated cache (e.g., Redis) or search engine (e.g., Elasticsearch) in the first release would drastically increase deployment friction, memory footprint, licensing costs, and operational failure surface in air-gapped on-premises datacenters.

## Decision
1. **PostgreSQL 16+ as the Single Primary Datastore**: All persistent data—including entity tables, node/edge topology graphs, immutable snapshots, JSONB dynamic metadata, and audit logs—will reside in PostgreSQL.
2. **Graph Model in PostgreSQL**: The dependency graph is modeled using typed `entities` (nodes) and `relationships` (edges) with foreign keys, composite indexes, and JSONB properties.
3. **Graph Traversal via Recursive CTEs**: Dependency path traversal, blast-radius queries, and neighborhood lookups will be executed using optimized PostgreSQL Recursive Common Table Expressions (CTEs) with cycle detection and depth boundaries.
4. **Graph Persistence Abstraction**: An interface (`IGraphRepository` / `IGraphService`) will isolate all graph operations, ensuring that if enterprise scale in a future release requires a dedicated graph engine, the application layer remains unchanged.

## Consequences
### Positive
- Minimizes enterprise installation footprint (single database service to manage, monitor, and back up).
- Native ACID transactions guarantee atomic snapshot creation and relational integrity across inventory and graph edges.
- Powerful JSONB indexing (GIN indexes) allows schemaless adapter metadata without sacrificing relational rigor.
- Proven enterprise reliability and zero commercial licensing fees.

### Negative / Tradeoffs
- Extremely deep graph traversals (e.g., >10 hops across 100k+ edges) require careful query tuning and depth bounding to maintain sub-second response times.
- Graph persistence abstraction must be maintained diligently.
