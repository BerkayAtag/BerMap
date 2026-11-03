# BerMap — Enterprise Infrastructure Intelligence & Digital Twin Platform

> **"Discover. Map. Analyze. Simulate. Predict."**  
> *Production-Grade | Fully On-Premises | Air-Gapped | Enterprise-Ready*

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
- **Security**: PBKDF2/Argon2 password hashing, JWT bearer tokens, Granular RBAC, Windows DPAPI / Linux Keyring credential encryption, mTLS for collectors

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
│   └── adr/                     # Architecture Decision Records (ADR-001 to ADR-004)
├── README.md
├── CONTRIBUTING.md
└── LICENSE
```

---

## 5. Quick Start (Development)

### Prerequisites
- .NET 10 (or .NET 9) SDK
- Node.js 20+ and npm 10+
- PostgreSQL 16+ instance running locally

### Running Backend API
```bash
cd src/BerMap.Api
dotnet run
```
API runs on `https://localhost:5001` with Swagger available at `/swagger`.

### Running Frontend Web Console
```bash
cd src/BerMap.Web
npm install
npm run dev
```
Console runs on `http://localhost:3000`.

---

## 6. License
Proprietary software. Copyright © 2026 Berkay Atağ. All rights reserved. The source is published for reference; no licence to use, copy or redistribute it is granted.
