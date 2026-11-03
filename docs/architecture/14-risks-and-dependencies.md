# 14 — Critical Risks, Dependencies & Mitigations

> **BerMap — Enterprise Infrastructure Intelligence & Digital Twin Platform**  
> *"Discover. Map. Analyze. Simulate. Predict."*

---

## 1. Risk Matrix

| Risk ID | Risk Description | Severity | Probability | Mitigation Strategy |
|---|---|---|---|---|
| **RSK-01** | **PostgreSQL Graph Query Latency at Scale (30k+ Nodes, 100k+ Edges)** | High | Medium | Implement strict recursive CTE cycle detection (`CYCLE edge_path SET is_cycle USING target_node_id`), depth ceilings (max 5 hops by default), covering B-tree indexes on `(source_node_id, relationship_type)` and `(target_node_id, relationship_type)`, and materialized neighborhood cache tables. Provide graph persistence abstraction to allow pluggable graph backends if needed in future releases. |
| **RSK-02** | **Air-Gapped Offline Packaging & Asset Leakage** | Critical | High | Rigorous build-pipeline audit. Disallow external CDN links, Google Fonts, and runtime package pulls. Run offline verification suite inside fully disconnected air-gapped test VM. All frontend fonts (Inter/system fonts) and icons (Lucide SVG bundle) are strictly pre-compiled and served locally by ASP.NET Core static file middleware. |
| **RSK-03** | **Active Directory / vCenter Rate Limiting & Account Lockout** | High | Low | Adapters implement token-bucket rate limiting, paging (e.g. 1000 objects per LDAP page), and configurable backoff timers. All credentials run with minimum read-only permissions (`List Object`, `Read All Properties`, VMware `ReadOnly` role). Clear health reporting surfaces warnings before thresholds cause account lockouts. |
| **RSK-04** | **Entity Duplication Across Diverse Discovery Sources** | High | High | Deterministic reconciliation matrix with confidence rankings. Primary matching relies on immutable hardware UUIDs (BIOS UUID, System UUID), FQDN, and network MAC addresses. Never merge entities based solely on simple hostname collisions. Ambiguous matches are flagged in the UI for administrator review. |
| **RSK-05** | **Browser Memory Exhaustion During Large Graph Visualization** | Medium | Medium | React Flow / XYFlow canvas implements viewport-based virtualization, level-of-detail (LOD) node clustering, and progressive neighborhood expansion. Instead of rendering all 30,000 nodes simultaneously, the UI renders aggregated clusters/datacenters and dynamically expands 2-hop neighborhoods upon user drill-down. |
| **RSK-06** | **Credential Storage Vulnerability on Windows/Linux Hosts** | Critical | Low | Plaintext secrets are strictly prohibited. On Windows Server, credentials are encrypted using Windows Data Protection API (DPAPI) tied to the BerMap service account. On Linux, AES-256-GCM encryption with local master key injected via environment variable or file permissions (`chmod 400`). |
| **RSK-07** | **Simulation Engine Misinterpretation as Production Action** | High | Low | Architectural wall: The Simulation Studio operates exclusively on immutable point-in-time snapshots. The simulation domain model has zero write adapters or outbound execution clients to real vCenter, AD, or network devices. All UI banners clearly state: *"Simulation Studio — Read-Only What-If Modeling"*. |

---

## 2. Technical Dependencies & Versions

| Dependency | Required Version | Purpose | Offline Packaging Mode |
|---|---|---|---|
| **.NET SDK / Runtime** | 10.0 LTS (or 9.0 LTS) | Core API Server, Background Worker, Collector | Bundled in installer / Container |
| **PostgreSQL** | 16+ | Core Datastore, Relational & Graph Storage | Bundled offline binaries / Container |
| **Node.js (Build-time)**| 20+ LTS | Frontend asset compilation | Pre-compiled static assets embedded in API |
| **Tailwind CSS** | v4 | Design Token & UI Styling | Bundled at compile-time |
| **React Flow / XYFlow** | Latest stable | Topology Graph Interactive Canvas | Bundled at compile-time |
| **ECharts** | Latest stable | High-performance Canvas/SVG Charts | Bundled at compile-time |
| **Lucide Icons** | Latest stable | Vector SVG Icon system | Bundled at compile-time |
