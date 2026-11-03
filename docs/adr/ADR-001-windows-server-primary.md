# ADR-001: Windows Server Primary, Linux Supported

## Status
Accepted

## Context
BerMap targets enterprise on-premises datacenter environments, particularly financial, banking, and critical infrastructure sectors. In these environments, Windows Server (2022 and 2025) represents the predominant host OS for enterprise core management services, Active Directory management, and infrastructure operations. Requiring Kubernetes, Docker, or Linux on day one would impose significant procurement, approval, and administrative hurdles for enterprise systems teams.

## Decision
1. **Windows Server is the primary deployment target**: BerMap Server and BerMap Collector will install and execute natively on Windows Server 2022 and 2025 as native Windows Services.
2. **No mandatory container virtualization on Windows**: Deployment must not mandate Docker Desktop, WSL2, Hyper-V, or Kubernetes on the target Windows host.
3. **Linux Secondary Support**: Linux is fully supported via an offline Docker Compose bundle (with Podman and native systemd planned for future releases).
4. **Offline Installer**: Windows deployment is packaged into a single offline installer (`BerMap-Server-Setup-x64.exe`) supporting both bundled and external PostgreSQL configurations.

## Consequences
### Positive
- Zero friction adoption in enterprise Windows Server datacenters.
- Straightforward integration with Windows authentication, Windows Defender Firewall, and Windows Service Recovery.
- Meets strict enterprise procurement policies where container runtimes may not be permitted on production management hosts.

### Negative / Tradeoffs
- Packaging a native Windows Service installer requires dedicated build steps compared to purely Docker-based distributions.
- Multi-platform verification (Windows Server native + Linux containers) must be automated in CI pipelines.
