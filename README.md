# BerMap — Enterprise Infrastructure Intelligence & Digital Twin Platform

> **"Discover. Map. Analyze. Simulate. Predict."**  
> *Fully On-Premises | Air-Gapped | Preview (0.1)*

**Product page:** https://berkayatag.github.io/BerMap/

---

## 1. What is BerMap?

**BerMap** is an enterprise-grade infrastructure discovery, dependency mapping, blast-radius impact analysis, and Digital Twin simulation platform designed from the ground up for strictly on-premises, air-gapped corporate environments (banking, telecommunications, healthcare, defense, and public utilities).

BerMap automatically connects to enterprise infrastructure systems (Active Directory, VMware vCenter, Zabbix, Microsoft SCCM/MECM, SQL Server, and Network devices), reconciles physical and virtual assets into a unified graph data model, visualizes complex dependencies in real time, and allows systems engineers to **simulate changes and failures before touching production** via the integrated **Simulation Studio (BerTwin)**.

---

## 2. Architectural Pillars

- **Windows Server Native (ADR-001)**: Primary enterprise host OS (Windows Server 2022/2025) running as native Windows Services without requiring Docker, WSL, or Kubernetes. Linux is supported via an offline Docker Compose bundle.
- **Modular Monolith (ADR-002)**: Single cohesive central server with strictly bounded domain modules (`Identity`, `Inventory`, `Integration`, `Discovery`, `Graph`, `Impact`, `Simulation`, `Capacity`, `Events`, `Reporting`, `Audit`).
- **PostgreSQL 16+ Primary Datastore (ADR-003)**: Relational inventory, JSONB metadata, audit logging, and graph model with recursive CTEs in a single ACID datastore. Zero mandatory Redis, Neo4j, or Elasticsearch dependencies.
- **Red & White Design Identity (ADR-004)**: High-contrast, executive-level enterprise web console with instant Light / Dark mode toggle and WCAG 2.1 AA accessibility.
- **100% Air-Gapped & Offline**: Zero telemetry, zero external CDN / Google Fonts, zero SaaS dependencies, completely offline installation and upgrade.

---

## 3. Technology Stack

- **Backend**: .NET 10 LTS, C# 13, ASP.NET Core Web API, Entity Framework Core 10, Npgsql
- **Frontend**: React 19, TypeScript, Vite, Tailwind CSS v4, React Flow / XYFlow, TanStack Query, Zustand, ECharts, Lucide Icons
- **Database**: PostgreSQL 16+ with UUIDs, JSONB, and Recursive CTEs
- **Security**: PBKDF2-SHA256 password hashing, HMAC-signed bearer tokens, role-based authorization, AES-256-GCM credential vault, enrollment-token authenticated collectors (see [SECURITY.md](SECURITY.md))

---

## 4. Repository Structure

```text
BerMap/
├── src/
│   ├── BerMap.Api/              # ASP.NET Core Web API host & controllers
│   ├── BerMap.Application/      # CQRS handlers, DTOs, domain service interfaces
│   ├── BerMap.Domain/           # Pure domain entities, value objects, enums
│   ├── BerMap.Infrastructure/   # EF Core DbContext, PostgreSQL repos, audit, security
│   ├── BerMap.Contracts/        # Shared DTOs, integration interfaces, collector protocol
│   ├── BerMap.Worker/           # Background discovery & synchronization worker
│   ├── BerMap.Collector/        # Standalone, outbound-only collection agent
│   ├── BerMap.Simulation/       # Deterministic Digital Twin simulation engine (BerTwin)
│   ├── BerMap.Integrations/     # Integration adapter implementations
│   │   ├── ActiveDirectory/     # LDAP/LDAPS directory discovery adapter
│   │   └── VMware/              # vSphere REST/SOAP hypervisor adapter
│   └── BerMap.Web/              # React + TypeScript + Vite management console
├── tests/
│   ├── BerMap.Tests.Unit/       # Unit tests for domain, graph, and simulation
│   └── BerMap.Tests.Integration/# Integration tests with PostgreSQL
├── docs/
│   ├── architecture/            # 14 technical architecture specifications
│   └── adr/                     # Architecture Decision Records (ADR-001 to ADR-005)
├── site/                        # Product page published to GitHub Pages
├── README.md
├── CONTRIBUTING.md
└── SECURITY.md
```

---

## 5. Quick Start (Development)

### Prerequisites
- .NET 10 SDK
- Node.js 20+ and npm 10+
- PostgreSQL 16+ (a local instance or `docker run -d -p 5432:5432 -e POSTGRES_USER=bermap_dev -e POSTGRES_PASSWORD=bermap_dev -e POSTGRES_DB=bermap_dev postgres:17-alpine`)

### Running the backend API
```bash
cd src/BerMap.Api
dotnet run --launch-profile http
```
- The API listens on `http://localhost:5294` and uses the `bermap_dev` connection string from `appsettings.Development.json`.
- On first start it applies the EF Core migrations (`BerMap:Database:ApplyMigrationsOnStartup`), creates the built-in roles and a local administrator `admin`.
- Set `BerMap:Bootstrap:AdminPassword` to choose the password; otherwise a random one is written to `initial-admin-password.txt` in the credential vault directory (`./.bermap-vault` in development). Delete that file after the first sign-in.
- Health probes: `/healthz/live`, `/healthz/ready` (fails while migrations are pending).

### Running the web console
```bash
cd src/BerMap.Web
npm install
npm run dev
```
The console runs on `http://localhost:3000` and proxies `/api` to `BERMAP_API_URL` (default `http://localhost:5294`).

### Tests
```bash
dotnet test BerMap.slnx
cd src/BerMap.Web && npm run lint && npm run build
```
The PostgreSQL integration tests run when `BERMAP_TEST_CONNECTION_STRING` points at a server where the user may create databases; otherwise they are skipped.

### Scheduled discovery
Each integration has a five-field cron schedule (`minute hour day-of-month month day-of-week`, e.g. `0 2 * * *` for 02:00 daily; `*/15`, ranges, lists, `MON-FRI`, `@daily` are supported). The API checks schedules every 30 seconds and queues a full discovery when the first occurrence after the integration's last discovery has passed:
- Schedules are evaluated in the server's local time zone; set `BerMap:DiscoveryScheduler:TimeZone` (e.g. `Europe/Istanbul`) to override.
- After downtime only one missed run is caught up, and a manual discovery counts as the run for its slot.
- Several API instances can share a database; a PostgreSQL advisory lock lets one of them schedule at a time.
- `BerMap:DiscoveryScheduler:Enabled` (default `true`) and `PollSeconds` (default `30`) control the scheduler.

### Known limitations of this preview
- Simulations always run against the live graph, not a snapshot.
- The collector enrolls, heartbeats and forwards queued batches, but does not run adapters itself yet.

---

## 6. License
Proprietary software. Copyright © 2026 Berkay Atağ. All rights reserved. The source is published for reference; no licence to use, copy or redistribute it is granted.
