import { apiRequest, type IntegrationDto, type SnapshotDto } from './api';

/*
 * Deterministic synthetic demo data, sent through the real collector protocol
 * (POST /api/collectors/submit-batch?final=true) into two collector-managed
 * integrations. All names are fictitious (corp.bermap.demo). Rerunning against
 * the same database updates the same nodes instead of adding new ones.
 */

export const DOMAIN = 'corp.bermap.demo';
export const AD_INTEGRATION = 'AD · corp.bermap.demo';
export const VCENTER_INTEGRATION = 'vCenter · IST-DC1';
export const COLLECTOR_NAME = 'collector-ist-01';
export const BASELINE_SNAPSHOT = 'Baseline · Kasım 2026';

type Props = Record<string, string | number | boolean>;

interface SeedNode {
  externalId: string;
  nodeType: string;
  name: string;
  fqdn: string;
  ipAddress: string | null;
  status: string;
  sourceSystem: string;
  properties: Record<string, string>;
  [extra: string]: unknown;
}

interface SeedRelationship {
  sourceExternalId: string;
  targetExternalId: string;
  relationshipType: string;
  confidenceLevel: string;
  evidence: string;
  sourceSystem: string;
}

interface Batch {
  nodes: SeedNode[];
  relationships: SeedRelationship[];
}

function batch(): Batch & {
  node: (ext: string, type: string, name: string, opts?: NodeOptions) => string;
  rel: (src: string, dst: string, type: string, evidence?: string, conf?: string, system?: string) => void;
} {
  const nodes: SeedNode[] = [];
  const relationships: SeedRelationship[] = [];
  return {
    nodes,
    relationships,
    node(ext, type, name, opts = {}) {
      const { fqdn = '', ip = null, status = 'healthy', props = {}, sourceSystem = 'VMware', ...extra } = opts;
      nodes.push({
        externalId: ext,
        nodeType: type,
        name,
        fqdn,
        ipAddress: ip,
        status,
        sourceSystem,
        properties: Object.fromEntries(Object.entries(props).map(([k, v]) => [k, pyString(v)])),
        ...extra,
      });
      return ext;
    },
    rel(src, dst, type, evidence = '', conf = 'Verified', system = 'VMware') {
      relationships.push({
        sourceExternalId: src,
        targetExternalId: dst,
        relationshipType: type,
        confidenceLevel: conf,
        evidence,
        sourceSystem: system,
      });
    },
  };
}

interface NodeOptions {
  fqdn?: string;
  ip?: string | null;
  status?: string;
  props?: Props;
  sourceSystem?: string;
  [extra: string]: unknown;
}

/** Same text as Python's str() for the values used here (True/False, 1.0). */
function pyString(value: string | number | boolean): string {
  if (typeof value === 'boolean') return value ? 'True' : 'False';
  if (typeof value === 'number' && !Number.isInteger(value)) return String(value);
  return String(value);
}

type AppRole = 'Web' | 'App' | 'Db' | 'Dc';

/** name, cluster, network, datastore, depends-on, role */
export const APPS: Array<[string, string, string, string, string[], AppRole]> = [
  ['web-portal-01', 'CL-DMZ-01', 'VLAN-110-Web', 'DS-NFS-DMZ-01', ['app-core-01', 'app-core-02'], 'Web'],
  ['web-portal-02', 'CL-DMZ-01', 'VLAN-110-Web', 'DS-NFS-DMZ-01', ['app-core-01', 'app-core-02'], 'Web'],
  ['api-gateway-01', 'CL-DMZ-01', 'VLAN-110-Web', 'DS-NFS-DMZ-01', ['app-payments-01', 'app-core-01'], 'Web'],
  ['app-core-01', 'CL-PROD-01', 'VLAN-204-App', 'DS-PURE-PROD-01', ['sql-core-01', 'mq-broker-01'], 'App'],
  ['app-core-02', 'CL-PROD-01', 'VLAN-204-App', 'DS-PURE-PROD-02', ['sql-core-01', 'mq-broker-01'], 'App'],
  ['app-payments-01', 'CL-PROD-01', 'VLAN-204-App', 'DS-PURE-PROD-01', ['sql-payments-01', 'mq-broker-01'], 'App'],
  ['app-reporting-01', 'CL-PROD-01', 'VLAN-204-App', 'DS-PURE-PROD-02', ['sql-dwh-01'], 'App'],
  ['mq-broker-01', 'CL-PROD-01', 'VLAN-204-App', 'DS-PURE-PROD-01', [], 'App'],
  ['sql-core-01', 'CL-PROD-01', 'VLAN-310-Data', 'DS-PURE-PROD-01', [], 'Db'],
  ['sql-payments-01', 'CL-PROD-01', 'VLAN-310-Data', 'DS-PURE-PROD-02', [], 'Db'],
  ['sql-dwh-01', 'CL-PROD-01', 'VLAN-310-Data', 'DS-PURE-PROD-02', [], 'Db'],
  ['dc01', 'CL-PROD-01', 'VLAN-204-App', 'DS-PURE-PROD-01', [], 'Dc'],
  ['dc02', 'CL-DMZ-01', 'VLAN-204-App', 'DS-NFS-DMZ-01', [], 'Dc'],
  ['backup-proxy-01', 'CL-PROD-01', 'VLAN-310-Data', 'DS-PURE-PROD-02', [], 'App'],
  ['monitor-01', 'CL-PROD-01', 'VLAN-204-App', 'DS-PURE-PROD-01', ['sql-core-01'], 'App'],
  ['jump-01', 'CL-DMZ-01', 'VLAN-110-Web', 'DS-NFS-DMZ-01', [], 'App'],
];

const CLUSTERS: Record<string, { hosts: number; cpu: number; mem: number; ha: boolean; drs: boolean }> = {
  'CL-PROD-01': { hosts: 4, cpu: 68, mem: 74, ha: true, drs: true },
  'CL-DMZ-01': { hosts: 2, cpu: 41, mem: 52, ha: true, drs: false },
};

/** First ESXi host of CL-PROD-01; the simulation tests fail it. */
export const PROD_HOST = `esx01.${DOMAIN}`;

function vcenterBatch(): Batch {
  const b = batch();
  const dc = b.node('datacenter-1', 'Datacenter', 'IST-DC1', { props: { Source: 'vCenter' } });

  const networks: Record<string, string> = {
    'VLAN-110-Web': b.node('network-110', 'Network', 'VLAN-110-Web', { props: { Type: 'DistributedPortgroup' } }),
    'VLAN-204-App': b.node('network-204', 'Network', 'VLAN-204-App', { props: { Type: 'DistributedPortgroup' } }),
    'VLAN-310-Data': b.node('network-310', 'Network', 'VLAN-310-Data', { props: { Type: 'DistributedPortgroup' } }),
  };
  for (const n of Object.values(networks)) b.rel(dc, n, 'Contains');

  const datastores: Record<string, string> = {};
  const dsSpecs: Array<[string, number, number]> = [
    ['DS-PURE-PROD-01', 20480, 14100],
    ['DS-PURE-PROD-02', 20480, 17900],
    ['DS-NFS-DMZ-01', 8192, 3900],
  ];
  dsSpecs.forEach(([name, cap, used], index) => {
    const pct = Math.round((used / cap) * 1000) / 10;
    datastores[name] = b.node(`datastore-${index + 1}`, 'Datastore', name, {
      status: pct > 85 ? 'warning' : 'healthy',
      props: {
        CapacityGb: cap,
        UsedGb: used,
        FreeGb: cap - used,
        UsagePercent: pct,
        Type: name.includes('PURE') ? 'VMFS' : 'NFS',
      },
    });
    b.rel(dc, datastores[name], 'Contains');
  });

  const hosts: Array<[string, string]> = [];
  let hid = 0;
  for (const [cname, c] of Object.entries(CLUSTERS)) {
    const capMhz = c.hosts * 64 * 2600;
    const capMb = c.hosts * 768 * 1024;
    const cl = b.node(`domain-c${hosts.length + 7}`, 'Cluster', cname, {
      props: {
        HostCount: c.hosts,
        CpuCapacityMhz: capMhz,
        CpuUsageMhz: Math.floor((capMhz * c.cpu) / 100),
        CpuUsagePercent: c.cpu,
        MemoryCapacityMb: capMb,
        MemoryUsageMb: Math.floor((capMb * c.mem) / 100),
        MemoryUsagePercent: c.mem,
        HaEnabled: c.ha,
        DrsEnabled: c.drs,
        VmCount: APPS.filter((a) => a[1] === cname).length,
      },
    });
    b.rel(dc, cl, 'Contains');
    for (let h = 0; h < c.hosts; h++) {
      hid++;
      const hname = `esx${String(hid).padStart(2, '0')}.${DOMAIN}`;
      const cpu = c.cpu + ((h * 7) % 15) - 6;
      const mem = c.mem + ((h * 5) % 12) - 5;
      const host = b.node(`host-${100 + hid}`, 'Hypervisor', hname, {
        fqdn: hname,
        ip: `10.20.0.${10 + hid}`,
        status: mem > 80 ? 'warning' : 'healthy',
        biosUuid: `4c4c4544-0042-4d10-8000-b${String(hid).padStart(11, '0')}`,
        props: {
          Vendor: 'Dell Inc.',
          Model: 'PowerEdge R750',
          CpuCores: 64,
          CpuMhz: 2600,
          MemoryMb: 786432,
          CpuUsagePercent: cpu,
          MemoryUsagePercent: mem,
          ConnectionState: 'CONNECTED',
          Cluster: cname,
        },
      });
      b.rel(host, cl, 'MemberOf', 'vCenter cluster membership');
      hosts.push([host, cname]);
    }
  }

  const clusterHosts: Record<string, string[]> = {};
  for (const [host, cname] of hosts) (clusterHosts[cname] ??= []).push(host);

  const vmExt: Record<string, string> = {};
  APPS.forEach(([name, cname, net, ds, , role], index) => {
    const i = index + 1;
    const members = clusterHosts[cname];
    const host = members[i % members.length];
    const ext = b.node(`vm-${1000 + i}`, 'VirtualMachine', name, {
      fqdn: `${name}.${DOMAIN}`,
      ip: `10.${30 + (i % 3)}.${i}.${20 + i}`,
      status: name === 'sql-dwh-01' || name === 'app-reporting-01' ? 'warning' : 'healthy',
      vmInstanceUuid: `5003a1b2-0000-4000-8000-${String(i).padStart(12, '0')}`,
      props: {
        PowerState: 'POWERED_ON',
        GuestOS: role === 'Db' || role === 'Dc' ? 'Windows Server 2022' : 'Ubuntu 22.04',
        CpuCount: role === 'Db' ? 8 : 4,
        MemoryMb: role === 'Db' ? 65536 : 16384,
        ToolsStatus: 'toolsOk',
        HostName: host,
        Cluster: cname,
      },
    });
    vmExt[name] = ext;
    b.rel(ext, host, 'HostedOn', 'vCenter runtime.host');
    b.rel(ext, datastores[ds], 'UsesStorage', 'vCenter datastore backing');
    b.rel(ext, networks[net], 'ConnectedTo', 'vNIC portgroup');
  });
  for (const [name, , , , deps] of APPS) {
    for (const d of deps) {
      b.rel(vmExt[name], vmExt[d], d.startsWith('sql') ? 'UsesDatabase' : 'DependsOn', 'Observed TCP flows', 'Observed');
    }
  }
  b.rel(vmExt['dc01'], vmExt['dc02'], 'ReplicatesTo', 'AD replication partner');
  return { nodes: b.nodes, relationships: b.relationships };
}

function activeDirectoryBatch(): Batch {
  const b = batch();
  const ad = (ext: string, type: string, name: string, opts: NodeOptions = {}) =>
    b.node(ext, type, name, { ...opts, sourceSystem: 'ActiveDirectory' });
  const rel = (src: string, dst: string, type: string, evidence = '') =>
    b.rel(src, dst, type, evidence, 'Verified', 'ActiveDirectory');

  const SID = 'S-1-5-21-3623811015-3361044348-30300820';
  const forest = ad('forest-corp', 'Forest', DOMAIN, { fqdn: DOMAIN });
  const dom = ad('domain-corp', 'Domain', DOMAIN, { fqdn: DOMAIN, domainSid: SID });
  rel(forest, dom, 'Contains');
  const site = ad('site-ist', 'Site', 'IST-HQ');
  rel(dom, site, 'Contains');
  const ous: Record<string, string> = {};
  for (const o of ['Servers', 'Web', 'Databases', 'Domain Controllers']) {
    ous[o] = ad(`ou-${o.toLowerCase()}`, 'OrganizationalUnit', o, { parentName: DOMAIN });
    rel(dom, ous[o], 'Contains');
  }
  APPS.forEach(([name, , , , , role], index) => {
    const i = index + 1;
    const ou = role === 'Web' ? 'Web' : role === 'Db' ? 'Databases' : role === 'Dc' ? 'Domain Controllers' : 'Servers';
    const ext = ad(`ad-${name}`, role === 'Dc' ? 'DomainController' : 'Computer', name, {
      fqdn: `${name}.${DOMAIN}`,
      domainSid: `${SID}-${1100 + i}`,
      props: {
        OperatingSystem: role === 'Db' || role === 'Dc' ? 'Windows Server 2022' : 'Ubuntu 22.04',
        Enabled: true,
      },
    });
    rel(ext, ous[ou], 'MemberOf', 'distinguishedName');
    if (role === 'Dc') rel(ext, site, 'MemberOf', 'serverReference');
  });
  return { nodes: b.nodes, relationships: b.relationships };
}

async function ensureCollectorIntegration(
  token: string,
  name: string,
  adapterType: string,
  configuration: Record<string, unknown>,
  description: string,
): Promise<string> {
  const existing = await apiRequest<IntegrationDto[]>('GET', '/api/integrations', { token });
  const found = existing.find((i) => i.name === name);
  if (found) return found.id;
  const created = await apiRequest<IntegrationDto>('POST', '/api/integrations', {
    token,
    body: {
      name,
      description,
      adapterType,
      configuration,
      secret: 'demo-not-a-real-secret',
      isEnabled: true,
      collectorManaged: true,
    },
  });
  return created.id;
}

export interface SeedResult {
  adIntegrationId: string;
  vcenterIntegrationId: string;
  nodeCount: number;
}

export async function seedDemoData(token: string, enrollment: string): Promise<SeedResult> {
  const adId = await ensureCollectorIntegration(
    token,
    AD_INTEGRATION,
    'ActiveDirectory',
    {
      server: `dc01.${DOMAIN}`,
      port: 636,
      username: `svc-bermap@${DOMAIN}`,
      baseDn: 'DC=corp,DC=bermap,DC=demo',
      useLdaps: true,
    },
    'Sentetik demo etki alanı',
  );
  const vcId = await ensureCollectorIntegration(
    token,
    VCENTER_INTEGRATION,
    'VMware',
    { server: `vcenter01.${DOMAIN}`, port: 443, username: 'svc-bermap@vsphere.local' },
    'Sentetik demo vCenter',
  );

  const collectorHeaders = { 'X-BerMap-Enrollment-Token': enrollment };
  const vc = vcenterBatch();
  await apiRequest('POST', '/api/collectors/submit-batch?final=true', {
    headers: collectorHeaders,
    body: { success: true, status: 'Completed', integrationId: vcId, sourceVersion: 'vCenter 8.0.3', ...vc },
  });
  const ad = activeDirectoryBatch();
  await apiRequest('POST', '/api/collectors/submit-batch?final=true', {
    headers: collectorHeaders,
    body: { success: true, status: 'Completed', integrationId: adId, sourceVersion: 'Windows Server 2022 DFL', ...ad },
  });

  await ensureCollector(enrollment);

  const snapshots = await apiRequest<SnapshotDto[]>('GET', '/api/snapshots', { token });
  if (!snapshots.some((s) => s.name === BASELINE_SNAPSHOT)) {
    await apiRequest('POST', '/api/snapshots', {
      token,
      body: { name: BASELINE_SNAPSHOT, description: 'Demo başlangıç anı' },
    });
  }

  return { adIntegrationId: adId, vcenterIntegrationId: vcId, nodeCount: vc.nodes.length };
}

/** Registers a collector and sends one heartbeat; returns its id. */
export async function ensureCollector(
  enrollment: string,
  name = COLLECTOR_NAME,
  hostname = `collector01.${DOMAIN}`,
): Promise<string> {
  const headers = { 'X-BerMap-Enrollment-Token': enrollment };
  const reg = await apiRequest<{ collectorId: string }>('POST', '/api/collectors/register', {
    headers,
    body: {
      name,
      hostname,
      ipAddress: '10.20.1.15',
      version: '1.0.0',
      supportedAdapters: ['ActiveDirectory', 'VMware'],
    },
  });
  await apiRequest('POST', '/api/collectors/heartbeat', {
    headers,
    body: {
      collectorId: reg.collectorId,
      version: '1.0.0',
      cpuUsagePercent: 7.5,
      memoryUsagePercent: 23.0,
      queuedMessagesCount: 0,
    },
  });
  return reg.collectorId;
}
