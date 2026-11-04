# 11 — MVP Scope (Phase 1 & Phase 2 Foundation)

> **BerMap — Enterprise Infrastructure Intelligence & Simulation Platform**  
> *"Discover. Map. Analyze. Simulate. Predict."*

---

## 1. Executive Summary

The MVP (Minimum Viable Product) establishes the production foundation of BerMap. It proves the architectural soundness of the modular monolith, sets up the air-gapped on-premises operational model, delivers the primary Red & White visual design system with dark mode toggle, and provides initial end-to-end infrastructure discovery and interactive topology visualization.

---

## 2. In-Scope for MVP

### 2.1 Backend Platform Foundation (.NET 10 LTS)
- **Modular Monolith Core**: Clear boundaries for `Identity`, `Inventory`, `Integrations`, `Graph`, and `Audit`.
- **Clean Architecture Implementation**: Strict segregation of Domain, Application, Infrastructure, and API layers.
- **PostgreSQL 16+ Schema**: UUID primary keys, JSONB metadata, typed nodes & edges, recursive CTE graph queries, audit trail.
- **Authentication & RBAC**: Local identity authentication, JWT access tokens, role-based claims (5 standard roles: Platform Administrator, Integration Administrator, Infrastructure Operator, Security Auditor, Read-Only Viewer).
- **Health & Diagnostics**: `/health`, `/health/ready`, `/health/live` endpoints monitoring database connectivity, disk thresholds, and internal queues.
- **Structured Logging**: Serilog configured for rolling local JSON logs with zero external telemetry.

### 2.2 Frontend Web Console (React + TypeScript + Vite)
- **Design System Implementation**:
  - Full design tokens with Primary Red (`#E30613`), White (`#FFFFFF`), Neutral Dark (`#17191F`), Light Gray (`#F5F6F8`), and Dark Theme (`#111318`).
  - Seamless Theme Toggle (Light / Dark) persisting to localStorage and user profile.
  - WCAG 2.1 AA accessible contrast ratios and keyboard navigation.
- **Executive Dashboard**:
  - Live KPI metrics: Total Discovered Entities, System Health breakdown, Active Integrations, Recent Discoveries.
  - ECharts donut visualization for infrastructure health distribution.
  - Real-time recent events and infrastructure changes feed.
- **Interactive Infrastructure Map (React Flow / XYFlow)**:
  - Directed graph visualization of infrastructure nodes (Datacenters, Clusters, Hosts, VMs, Databases, Apps).
  - Zoom, Pan, Neighborhood selection, and node inspection side panel.
  - Custom themed nodes matching BerMap status color tokens.
- **Integrations & Collector Management UI**:
  - Wizard-style integration setup (Connection test, credential validation, discovery trigger).
  - Collector status monitoring (Online/Offline, last heartbeat, queue status).

### 2.3 Discovery & Integration Engine
- **Active Directory / LDAP Adapter**:
  - Read-only discovery of Forest, Domains, Domain Controllers, OUs, and Computer objects.
  - Paged search results, configurable timeouts, connection testing.
- **VMware vCenter Adapter**:
  - Read-only vSphere API discovery (Datacenter, Cluster, ESXi Host, VM, Datastore, Portgroups).
  - VM-to-Host and VM-to-Datastore relationship mapping.
- **Reconciliation Engine**:
  - Cross-source identity reconciliation using FQDN, BIOS UUID, and MAC address rules.
  - Ingestion into PostgreSQL typed node/edge graph model.

### 2.4 Simulation Studio Prototype (BerSim)
- **What-If Host Failure Simulation**:
  - Selecting an ESXi host to simulate failure.
  - Calculating direct blast radius: impacted VMs, databases, and hosted application services.
  - Cluster headroom calculation (can surviving hosts absorb the memory and CPU load?).
  - Simulation result report with Confirmed, Probable, and Possible impacts.

---

## 3. Out-of-Scope for MVP (Deferred to Phases 3-8)

| Capability | Deferred To | Rationale |
|---|---|---|
| Zabbix & SCCM Deep Sync | Phase 3 | Focus initial discovery on foundational AD & VMware hypervisor layers |
| Native Windows Service MSI Installer | Phase 8 | Development and internal testing runs directly as native process / offline Docker |
| Local AI / Ollama Query Assistant | Phase 7 | Core deterministic graph engine must be hardened before adding natural language interpretation |
| Dynamic Network Discovery (CDP/LLDP) | Phase 6 | Requires network device SNMP credentials and specialized port parsers |
| Multi-Forest Trust Auto-Traversal | Phase 4 | Security compliance requires explicit credentials per forest initially |
| Automated Disaster Recovery Orchestration | Phase 8 | BerMap is read-only simulation; write-back orchestration is strictly out of scope |

---

## 4. MVP Success Criteria & Acceptance Gates

1. **100% Air-Gapped Operation**: Platform compiles, starts, discovers, maps, and operates without internet connectivity or external CDN/font calls.
2. **Visual Consistency**: Both Light and Dark themes strictly adhere to the Red/White design token specification.
3. **Graph Traversal Performance**: Interactive map renders 1,000+ nodes smoothly and performs 5-hop impact traversal in under 200ms on PostgreSQL.
4. **Deterministic Simulation**: Running identical host failure scenarios produces identical blast radius and capacity headroom numbers.
5. **Zero Data Tampering**: All operations are read-only; absolutely zero configuration write-back to target AD or vCenter environments.
