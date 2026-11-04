# 13 — Installation, Upgrade & Rollback Architecture

> **BerMap — Enterprise Infrastructure Intelligence & Simulation Platform**  
> *"Discover. Map. Analyze. Simulate. Predict."*

---

## 1. Overview & Constraints

BerMap is engineered for strictly air-gapped, on-premises corporate datacenters. All installation, upgrade, maintenance, and disaster recovery workflows must function completely offline without access to public package repositories, container registries, or remote licensing validation servers.

---

## 2. Windows Server Deployment (Primary Target)

### 2.1 Single Offline Installer (`BerMap-Server-Setup-x64.exe`)
The installer is a self-contained executable packaging:
- .NET 10 Runtime (Windows Server Hosting Bundle)
- BerMap API Server & Static Web Console Assets
- BerMap Background Worker Service
- Bundled PostgreSQL 16 Binaries (Offline distribution)
- EF Core Database Migration Bundle (`bundle.exe`)
- SSL/TLS Self-Signed Certificate Provisioner (with enterprise PKI import option)

### 2.2 15-Step Installer Workflow
```mermaid
flowchart TD
    S1[1. Verify Administrator Privileges] --> S2[2. Check OS: Windows Server 2022/2025]
    S2 --> S3[3. Hardware Audit: CPU, RAM, Disk]
    S3 --> S4[4. Verify / Install .NET 10 Hosting Bundle]
    S4 --> S5[5. Choose Installation Path: C:\\Program Files\\BerMap]
    S5 --> S6{6. Database Mode Selection}
    S6 -- Mode A: Bundled --> S7A[Install & Configure Bundled PostgreSQL Service]
    S6 -- Mode B: Existing --> S7B[Test Connection & Permissions to Remote PostgreSQL]
    S7A --> S8[7. Deploy BerMap Core Files & Binaries]
    S7B --> S8
    S8 --> S9[8. Register Windows Service: BerMap Server]
    S9 --> S10[9. Configure Service Recovery: Auto-restart on failure]
    S10 --> S11[10. Configure HTTPS Port 443 & TLS Certificate]
    S11 --> S12[11. Bootstrap First Admin Account: Generate secure credential]
    S12 --> S13[12. Configure Windows Defender Firewall Rules]
    S13 --> S14[13. Execute EF Core Schema Migrations]
    S14 --> S15[14. Perform Automated Local Health Check]
    S15 --> S16[15. Complete: Output Web Console URL and Admin Credentials]
```

### 2.3 Silent & Unattended Installation
For automated enterprise rollouts via Microsoft SCCM / MECM or HCL BigFix:

```powershell
# Mode A: Bundled PostgreSQL Silent Install
BerMap-Server-Setup-x64.exe /quiet /norestart `
  INSTALLDIR="D:\BerMap" `
  DB_MODE="Bundled" `
  DB_PORT=5432 `
  HTTP_PORT=80 `
  HTTPS_PORT=443 `
  ADMIN_EMAIL="secadmin@enterprise.local" `
  ADMIN_PASSWORD="SecureGeneratedPassword123!" `
  LOGFILE="C:\Windows\Temp\bermap_install.log"

# Mode B: Existing PostgreSQL Silent Install
BerMap-Server-Setup-x64.exe /quiet /norestart `
  INSTALLDIR="D:\BerMap" `
  DB_MODE="Existing" `
  DB_HOST="db-cluster.enterprise.local" `
  DB_PORT=5432 `
  DB_NAME="bermap_prod" `
  DB_USER="bermap_app" `
  DB_PASSWORD="DbSecretPassword123!" `
  HTTPS_PORT=443 `
  LOGFILE="C:\Windows\Temp\bermap_install.log"
```

---

## 3. Linux Deployment (Offline Docker Bundle)

For Linux environments, BerMap provides `bermap-linux-offline-v{version}.tar.gz`:
1. **Archive Contents**:
   - `bermap-api:latest.tar` (Pre-saved container image)
   - `postgres:16-alpine.tar` (Pre-saved database container image)
   - `docker-compose.yml` (Configured with local bridge network and persistent volumes)
   - `.env.template`
   - `install.sh`, `upgrade.sh`, `backup.sh`, `rollback.sh`
2. **Installation Execution**:
   ```bash
   tar -xzf bermap-linux-offline-v1.0.0.tar.gz
   cd bermap-linux-offline
   chmod +x *.sh
   ./install.sh --db-password "SecureDbPassword123!" --admin-password "SecureAdminPassword123!"
   ```
3. Containers run as non-root users (`uid 10001:10001`) with explicit capability drops (`cap_drop: - ALL`).

---

## 4. Upgrade & Safe Rollback Procedure

```mermaid
sequenceDiagram
    participant Admin as System Administrator
    participant Installer as Upgrade Executable
    participant Svc as BerMap Server Service
    participant DB as PostgreSQL Database
    participant Backup as Local Backup Storage

    Admin->>Installer: Execute Upgrade (v1.0 -> v1.1)
    Installer->>Svc: Pre-upgrade check (Health, Disk, DB connectivity)
    Installer->>Svc: Stop Windows Service gracefully
    Installer->>DB: Trigger pg_dump (Pre-upgrade full DB snapshot)
    DB-->>Backup: Save bermap_pre_upgrade_v1.0.sql.enc
    Installer->>Installer: Replace Application Binaries & Web Assets
    Installer->>DB: Apply EF Core Database Migrations
    alt Migration Succeeds
        Installer->>Svc: Start BerMap Service
        Installer->>Svc: Run Post-Upgrade Health Check (/health/ready)
        Svc-->>Installer: Status: 200 OK
        Installer-->>Admin: Upgrade Complete Successfully
    else Migration Fails
        Installer->>Installer: Log Critical Error & Trigger Automated Rollback
        Installer->>DB: Restore Database from pre-upgrade snapshot
        Installer->>Installer: Restore Previous Application Binaries
        Installer->>Svc: Restart Previous Version Service
        Installer-->>Admin: Rollback Completed (System restored to v1.0)
    end
```
