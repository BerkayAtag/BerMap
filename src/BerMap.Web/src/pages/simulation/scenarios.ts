import type { ImpactAnalysisResult, RecommendationItem } from '@/types';
import { recommendationTemplates } from '@/lib/dictionaries';
import { interpolate } from '@/lib/i18n-runtime';

/** Scenario types accepted by POST /api/simulations/run (contract item 1). */
export const SCENARIOS = ['host_failure', 'datastore_failure', 'network_partition', 'workload_outage'] as const;
export type ScenarioType = (typeof SCENARIOS)[number];

/** Older names still accepted by the API and found in existing links. */
const SCENARIO_ALIASES: Record<string, ScenarioType> = {
  esxi_failure: 'host_failure',
  datastore_outage: 'datastore_failure',
  storage_failure: 'datastore_failure',
  network_cut: 'network_partition',
};

/** Node types each scenario can fail; the engine rejects any other pairing with 400. */
export const SCENARIO_TARGET_TYPES: Record<ScenarioType, readonly string[]> = {
  host_failure: ['Hypervisor', 'PhysicalServer'],
  datastore_failure: ['Datastore', 'Storage'],
  network_partition: ['Network', 'NetworkDevice'],
  workload_outage: ['VirtualMachine', 'Computer', 'DomainController', 'Application', 'Service', 'Database', 'Endpoint'],
};

export const SCENARIO_LABELS: Record<ScenarioType, { tr: string; en: string; descTr: string; descEn: string }> = {
  host_failure: {
    tr: 'Host Kesintisi',
    en: 'Host Outage',
    descTr: 'ESXi / fiziksel sunucu kapanması ve VM failover etkisi',
    descEn: 'ESXi / physical server shutdown and VM failover blast radius',
  },
  datastore_failure: {
    tr: 'Datastore / Storage Arızası',
    en: 'Datastore / Storage Failure',
    descTr: 'Depolama erişim kaybı ve bağlı iş yükleri',
    descEn: 'Storage access loss and the workloads that use it',
  },
  network_partition: {
    tr: 'Ağ Bölünmesi',
    en: 'Network Partition',
    descTr: 'VLAN, switch veya uplink kesintisi',
    descEn: 'VLAN, switch or uplink loss',
  },
  workload_outage: {
    tr: 'İş Yükü Kesintisi',
    en: 'Workload Outage',
    descTr: 'VM, uygulama, servis veya veritabanının durması',
    descEn: 'A VM, application, service or database going down',
  },
};

export function normalizeScenario(value: string | null | undefined): ScenarioType | null {
  const key = value?.trim().toLowerCase();
  if (!key) return null;
  if ((SCENARIOS as readonly string[]).includes(key)) return key as ScenarioType;
  return SCENARIO_ALIASES[key] ?? null;
}

export function isValidTarget(scenario: ScenarioType, nodeType: string | null | undefined): boolean {
  return !!nodeType && SCENARIO_TARGET_TYPES[scenario].includes(nodeType);
}

/** The scenario that fits a node type, used when a link carries only ?target=. */
export function scenarioForNodeType(nodeType: string | null | undefined): ScenarioType | null {
  return SCENARIOS.find((scenario) => isValidTarget(scenario, nodeType)) ?? null;
}

export function scenarioLabel(scenario: string, isTr: boolean): string {
  const normalized = normalizeScenario(scenario);
  if (!normalized) return scenario;
  return isTr ? SCENARIO_LABELS[normalized].tr : SCENARIO_LABELS[normalized].en;
}

export type RiskLevel = 'high' | 'medium' | 'low';

export function riskLevel(score: number): RiskLevel {
  if (score >= 60) return 'high';
  if (score >= 30) return 'medium';
  return 'low';
}

export function riskLevelLabel(level: RiskLevel, isTr: boolean): string {
  if (level === 'high') return isTr ? 'Yüksek' : 'High';
  if (level === 'medium') return isTr ? 'Orta' : 'Medium';
  return isTr ? 'Düşük' : 'Low';
}

/**
 * Localized recommendation lines. Coded items use the tr/en templates; unknown
 * codes, and servers without recommendationItems, fall back to the engine text.
 */
export function localizeRecommendations(result: ImpactAnalysisResult, isTr: boolean): string[] {
  const items: RecommendationItem[] | undefined = result.recommendationItems;
  if (!items || items.length === 0) return result.recommendations ?? [];
  const templates = recommendationTemplates[isTr ? 'tr' : 'en'];
  return items.map((item) => {
    const key =
      item.code === 'headroom_sufficient' && result.clusterHeadroom.isPostFailure
        ? 'headroom_sufficient_post_failure'
        : item.code;
    const template = templates[key] ?? templates[item.code];
    if (!template) return item.text;
    // The engine sends invariant numbers ("23.4"); show them in the UI locale.
    const values: Record<string, string> = { ...(item.values ?? {}) };
    for (const name of ['cpu', 'ram']) {
      const number = Number.parseFloat(values[name] ?? '');
      if (Number.isFinite(number)) {
        values[name] = number.toLocaleString(isTr ? 'tr-TR' : 'en-US', { maximumFractionDigits: 1 });
      }
    }
    return interpolate(template, values);
  });
}

/** One rendered path segment: a node name or the relationship between two nodes. */
export interface PathSegment {
  kind: 'node' | 'edge';
  text: string;
}

/**
 * Splits the engine's "a -> [DependsOn] -> b" path into segments. The path
 * already starts where the impact starts, so the target is never prefixed again.
 */
export function parseDependencyPath(path: string | null | undefined, fallbackName: string): PathSegment[] {
  const parts = (path ?? '')
    .split('->')
    .map((part) => part.trim())
    .filter(Boolean);
  if (parts.length === 0) return [{ kind: 'node', text: fallbackName }];
  return parts.map((part) =>
    part.startsWith('[') && part.endsWith(']')
      ? { kind: 'edge', text: part.slice(1, -1) }
      : { kind: 'node', text: part },
  );
}
