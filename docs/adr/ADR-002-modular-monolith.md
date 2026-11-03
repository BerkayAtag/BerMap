# ADR-002: Modular Monolith Architecture + Independent Collectors

## Status
Accepted

## Context
Enterprise IT teams managing critical infrastructure cannot tolerate the operational complexity, distributed transaction failure modes, network latency, and orchestration overhead of a premature microservices architecture. At the same time, tightly coupled spaghetti architectures prevent long-term maintainability, team velocity, and future scaling.

Furthermore, data collection across isolated network segments (DMZs, remote branches, isolated security enclaves) requires lightweight, remote data acquisition agents that can operate independently of the central server.

## Decision
1. **Modular Monolith for Central Server**: The central BerMap platform is architected as a single deployable process structured into strictly bounded domain modules (`Identity`, `Inventory`, `Integration`, `Discovery`, `Graph`, `Impact`, `Simulation`, `Capacity`, `Events`, `Reporting`, `Audit`).
2. **Explicit Contracts Between Modules**: Modules communicate through well-defined in-process application contracts, DTOs, and domain events, never reaching into another module's internal database tables or private implementation classes.
3. **Independent Collectors**: Verifiably decoupled `BerMap.Collector` processes operate outside the central server. Collectors can be deployed remotely (on remote Windows or Linux machines) and communicate back to the central server via an outbound-only, mTLS-secured HTTPS protocol.
4. **Crash Isolation**: A failure or crash in an individual collector or integration adapter must never destabilize or terminate the central BerMap Server.

## Consequences
### Positive
- Single binary deployment simplifies enterprise Windows Service installation, upgrades, and backups.
- High-performance in-memory method invocation eliminates inter-service network overhead.
- Clear module boundaries allow future extraction of specific modules into standalone microservices if specific horizontal scaling demands arise.
- Independent collectors easily penetrate network enclaves via outbound-only HTTPS.

### Negative / Tradeoffs
- Discipline is strictly required to prevent developers from bypassing module contracts. Architecture boundary unit tests must enforce dependency rules.
