import React, { useState } from 'react';
import { Card } from '@/components/ui/Card';
import { Button } from '@/components/ui/Button';
import { ButtonLink } from '@/components/ui/ButtonLink';
import {
  Play,
  RotateCcw,
  AlertTriangle,
  ArrowRight,
  Loader2,
  Radio,
  Info,
  PlugZap,
} from 'lucide-react';
import { api } from '@/lib/api';
import { formatNumber, getErrorMessage } from '@/lib/format';
import { useLocale } from '@/lib/i18n';
import { useT } from '@/lib/i18n-runtime';
import { NodePicker } from '@/components/inventory/NodePicker';
import { useInventoryNodes } from '@/hooks/useInventoryNodes';
import type { ImpactAnalysisResult, ImpactedEntity, InventoryNode } from '@/types';

const SCENARIOS = ['host_failure', 'datastore_outage', 'network_cut'] as const;
type ScenarioType = (typeof SCENARIOS)[number];

/** A result together with the parameters that produced it. */
interface SimulationRun {
  scenario: ScenarioType;
  target: Pick<InventoryNode, 'id' | 'name' | 'nodeType' | 'fqdn'>;
  result: ImpactAnalysisResult;
}

const SCENARIO_LABELS: Record<ScenarioType, { tr: string; en: string; descTr: string; descEn: string }> = {
  host_failure: {
    tr: 'ESXi Host Kesintisi',
    en: 'ESXi Host Outage',
    descTr: 'Host kapanması ve VM failover etkisi',
    descEn: 'Host shutdown and VM failover blast radius',
  },
  datastore_outage: {
    tr: 'Datastore / Storage Arızası',
    en: 'Datastore / Storage Outage',
    descTr: 'Depolama erişim kaybı ve volume kilidi',
    descEn: 'Storage path disconnect and volume lock',
  },
  network_cut: {
    tr: 'Ağ Bağlantı Kopması',
    en: 'Network Split / Cut',
    descTr: 'Core switch veya uplink kesintisi',
    descEn: 'Core switch or uplink loss partition',
  },
};

export const SimulationStudioPage: React.FC = () => {
  const locale = useLocale();
  const isTr = locale === 'tr';
  const t = useT();

  const [selectedScenario, setSelectedScenario] = useState<ScenarioType>('host_failure');
  const [selectedTarget, setSelectedTarget] = useState<InventoryNode | null>(null);
  const [isRunning, setIsRunning] = useState(false);
  const [runError, setRunError] = useState<string | null>(null);
  const [run, setRun] = useState<SimulationRun | null>(null);

  // Lightweight existence check: is there any discovered inventory at all?
  const inventoryProbe = useInventoryNodes({ page: 1, pageSize: 1 }, { refetchInterval: 60_000 });
  const inventoryTotal = inventoryProbe.data?.totalCount ?? 0;
  const inventoryError = inventoryProbe.isError ? getErrorMessage(inventoryProbe.error) : null;

  const result = run?.result ?? null;
  const runScenarioMeta = run ? SCENARIO_LABELS[run.scenario] : null;

  const reset = () => {
    setRun(null);
    setRunError(null);
  };

  const handleRunSimulation = async () => {
    if (!selectedTarget) return;
    const scenario = selectedScenario;
    const target = selectedTarget;
    setIsRunning(true);
    setRunError(null);
    setRun(null);
    try {
      // The engine always runs on the live graph (no snapshot parameter is honoured server-side).
      const response = await api.post<ImpactAnalysisResult>('/simulations/run', {
        targetNodeId: target.id,
        scenarioType: scenario,
      });
      setRun({
        scenario,
        target: { id: target.id, name: target.name, nodeType: target.nodeType, fqdn: target.fqdn },
        result: response,
      });
    } catch (error) {
      setRunError(getErrorMessage(error));
    } finally {
      setIsRunning(false);
    }
  };

  const riskTone =
    result == null
      ? ''
      : result.riskScore >= 60
        ? 'bg-rose-50 dark:bg-rose-950/40 text-rose-700 dark:text-rose-400 border-rose-200 dark:border-rose-900'
        : result.riskScore >= 30
          ? 'bg-amber-50 dark:bg-amber-950/40 text-amber-700 dark:text-amber-400 border-amber-200 dark:border-amber-900'
          : 'bg-emerald-50 dark:bg-emerald-950/40 text-emerald-700 dark:text-emerald-400 border-emerald-200 dark:border-emerald-900';

  const riskLabel =
    result == null
      ? ''
      : result.riskScore >= 60
        ? isTr ? 'Risk Seviyesi: Yüksek' : 'Risk Level: High'
        : result.riskScore >= 30
          ? isTr ? 'Risk Seviyesi: Orta' : 'Risk Level: Medium'
          : isTr ? 'Risk Seviyesi: Düşük' : 'Risk Level: Low';

  return (
    <div className="space-y-6 max-w-7xl mx-auto">
      {/* Page Title */}
      <div className="flex flex-col sm:flex-row sm:items-center sm:justify-between gap-4">
        <div>
          <div className="flex items-center gap-2">
            <h1 className="text-2xl font-bold tracking-tight text-neutral-900 dark:text-white">
              {isTr ? 'BerSim — Simülasyon Stüdyosu' : 'BerSim — Simulation Studio'}
            </h1>
            <span className="text-xs px-2.5 py-0.5 rounded-full bg-brand-primary text-white font-bold tracking-wide">
              {t('berSimEngine')}
            </span>
          </div>
          <p className="text-sm text-neutral-500 dark:text-neutral-400 mt-1">
            {isTr
              ? '"Simulate Before You Change" — gerçek envanter grafiği üzerinde tek senaryo seçerek deterministik etki hesabı çalıştırın'
              : '"Simulate Before You Change" — pick a single scenario and run a deterministic impact computation over the real inventory graph'}
          </p>
        </div>

        <div className="flex items-center gap-3">
          <Button variant="outline" size="sm" onClick={reset} className="gap-2" disabled={isRunning}>
            <RotateCcw className="w-4 h-4" />
            {isTr ? 'Sıfırla' : 'Reset'}
          </Button>
          <Button
            variant="primary"
            size="sm"
            onClick={() => void handleRunSimulation()}
            disabled={isRunning || !selectedTarget}
            className="gap-2 bg-brand-primary"
          >
            {isRunning ? <Loader2 className="w-4 h-4 animate-spin" /> : <Play className="w-4 h-4 fill-white" />}
            {isRunning
              ? (isTr ? 'Hesaplanıyor...' : 'Computing...')
              : (isTr ? 'Simülasyonu Çalıştır' : 'Run Simulation')}
          </Button>
        </div>
      </div>

      {/* Inventory error banner */}
      {inventoryError && (
        <div
          role="alert"
          className="p-3 rounded-lg border border-rose-200 dark:border-rose-900 bg-rose-50 dark:bg-rose-950/30 text-sm text-rose-700 dark:text-rose-300 flex items-center gap-2"
        >
          <AlertTriangle className="w-4 h-4" />
          <span className="flex-1">{inventoryError}</span>
          <Button variant="outline" size="sm" onClick={() => void inventoryProbe.refetch()}>
            {isTr ? 'Tekrar Dene' : 'Retry'}
          </Button>
        </div>
      )}

      {/* Empty state: no graph yet */}
      {!inventoryProbe.isLoading && !inventoryError && inventoryTotal === 0 ? (
        <Card className="p-10 text-center">
          <PlugZap className="w-10 h-10 mx-auto text-neutral-300 dark:text-neutral-600" />
          <h2 className="mt-3 font-bold text-neutral-900 dark:text-white">
            {isTr ? 'Stüdyo için simüle edilecek bileşen yok' : 'No components to simulate yet'}
          </h2>
          <p className="mt-1 text-sm text-neutral-500 dark:text-neutral-400 max-w-xl mx-auto">
            {isTr
              ? 'BerSim yalnızca gerçek keşif verisi üzerinde çalışır. Integrations sayfasından Active Directory veya vCenter keşfi başlatın; keşif ve uzlaştırma tamamlandığında bileşenler buraya otomatik gelir.'
              : 'BerSim only runs on real discovery data. Start an Active Directory or vCenter discovery from the Integrations page; components appear here automatically once discovery and reconciliation complete.'}
          </p>
          <ButtonLink to="/integrations" variant="primary" size="sm" className="mt-4 gap-2 bg-brand-primary">
            <PlugZap className="w-4 h-4" />
            {isTr ? 'Keşif başlat' : 'Start discovery'}
          </ButtonLink>
        </Card>
      ) : (
        <>
          {/* Scenario Configuration Grid */}
          <div className="grid grid-cols-1 lg:grid-cols-3 gap-6">
            {/* Step 1: Scenario Type */}
            <Card className="p-5 flex flex-col justify-between">
              <div>
                <div className="flex items-center gap-2 text-xs font-bold uppercase tracking-wider text-neutral-500 mb-3">
                  <span className="w-5 h-5 rounded-full bg-brand-primary text-white flex items-center justify-center text-xs">
                    1
                  </span>
                  {isTr ? 'Senaryo Türü' : 'Scenario Type'}
                </div>
                <div className="space-y-2">
                  {SCENARIOS.map((scenario) => (
                    <button
                      key={scenario}
                      type="button"
                      onClick={() => setSelectedScenario(scenario)}
                      aria-pressed={selectedScenario === scenario}
                      className={`w-full text-left p-3 rounded-lg border text-sm transition-all ${
                        selectedScenario === scenario
                          ? 'border-brand-primary bg-brand-primary/5 dark:bg-brand-primary-dark/10 text-neutral-900 dark:text-white font-medium'
                          : 'border-neutral-200 dark:border-dark-border text-neutral-600 dark:text-neutral-400 hover:border-neutral-300'
                      }`}
                    >
                      <div className="font-semibold">{isTr ? SCENARIO_LABELS[scenario].tr : SCENARIO_LABELS[scenario].en}</div>
                      <div className="text-xs text-neutral-500 mt-0.5">
                        {isTr ? SCENARIO_LABELS[scenario].descTr : SCENARIO_LABELS[scenario].descEn}
                      </div>
                    </button>
                  ))}
                </div>
              </div>
            </Card>

            {/* Step 2: Target Entity (real inventory) */}
            <Card className="p-5 flex flex-col justify-between">
              <div className="min-w-0">
                <div className="flex items-center gap-2 text-xs font-bold uppercase tracking-wider text-neutral-500 mb-3">
                  <span className="w-5 h-5 rounded-full bg-brand-primary text-white flex items-center justify-center text-xs">
                    2
                  </span>
                  {isTr ? 'Gerçek Envanterden Hedef' : 'Target from Real Inventory'}
                </div>
                <NodePicker
                  selectedId={selectedTarget?.id ?? null}
                  onSelect={(node) => {
                    setSelectedTarget(node);
                    setRunError(null);
                  }}
                  label={isTr ? 'Simülasyon hedefi' : 'Simulation target'}
                />
              </div>
            </Card>

            {/* Step 3: Snapshot (real list) & Parameters */}
            <Card className="p-5 flex flex-col justify-between">
              <div>
                <div className="flex items-center gap-2 text-xs font-bold uppercase tracking-wider text-neutral-500 mb-3">
                  <span className="w-5 h-5 rounded-full bg-brand-primary text-white flex items-center justify-center text-xs">
                    3
                  </span>
                  {isTr ? 'Veri Kaynağı & Parametreler' : 'Data Source & Parameters'}
                </div>
                <div className="space-y-3 text-sm">
                  <div>
                    <span className="text-xs font-medium text-neutral-500">
                      {isTr ? 'Simülasyon Tabanı:' : 'Simulation Base:'}
                    </span>
                    <div className="mt-1 p-2 rounded-lg bg-neutral-100 dark:bg-dark-border text-xs text-neutral-800 dark:text-neutral-200 flex items-center gap-2">
                      <Radio className="w-3.5 h-3.5 text-status-healthy shrink-0" aria-hidden="true" />
                      {isTr ? 'Canlı graf (güncel envanter)' : 'Live graph (current inventory)'}
                    </div>
                    <p className="mt-1.5 text-[11px] text-neutral-500">
                      {isTr
                        ? 'Simülasyon her zaman güncel canlı graf üzerinde çalışır; snapshot tabanlı simülasyon henüz desteklenmiyor.'
                        : 'Simulations always run on the current live graph; snapshot-based simulation is not supported yet.'}
                    </p>
                  </div>
                  <div>
                    <label className="text-xs font-medium text-neutral-500">
                      {isTr ? 'Motor Modu:' : 'Engine Mode:'}
                    </label>
                    <div className="mt-1 p-2 rounded-lg bg-neutral-100 dark:bg-dark-border text-xs text-neutral-800 dark:text-neutral-200">
                      {isTr
                        ? 'Deterministik BerSim motoru (salt-okunur)'
                        : 'Deterministic BerSim engine (read-only)'}
                    </div>
                  </div>
                  <div className="p-3 rounded-lg bg-amber-50 dark:bg-amber-950/30 border border-amber-200 dark:border-amber-900/50 text-xs text-amber-800 dark:text-amber-300">
                    <AlertTriangle className="w-4 h-4 inline mr-1 text-amber-600" />
                    {isTr
                      ? 'Simülasyon motoru deterministiktir. Üretim sistemlerine hiçbir çağrı yapılmaz.'
                      : 'Simulation engine is deterministic. Zero calls are made to live production targets.'}
                  </div>
                </div>
              </div>
            </Card>
          </div>

          {/* Run error */}
          {runError && (
            <div
              role="alert"
              className="p-3 rounded-lg border border-rose-200 dark:border-rose-900 bg-rose-50 dark:bg-rose-950/30 text-sm text-rose-700 dark:text-rose-300 flex items-center gap-2"
            >
              <AlertTriangle className="w-4 h-4" />
              {runError}
            </div>
          )}

          {/* Simulation Results Section (real engine output) */}
          {result && (
            <div className="space-y-6 bm-fade-in">
              <div className="p-5 rounded-xl border border-neutral-200 dark:border-dark-border bg-white dark:bg-dark-card shadow-sm">
                <div className="flex flex-col md:flex-row md:items-center justify-between border-b border-neutral-200 dark:border-dark-border pb-4 gap-4">
                  <div>
                    <span className="text-xs uppercase font-bold tracking-wider text-brand-primary dark:text-brand-primary-dark">
                      {isTr ? 'Simülasyon Çıktısı (Etki Yarıçapı Raporu)' : 'Simulation Output (Blast Radius Report)'}
                    </span>
                    <h3 className="text-lg font-bold text-neutral-900 dark:text-white mt-1">
                      {isTr
                        ? `Hedef: ${result.targetNodeName || run?.target.name} — ${runScenarioMeta?.tr} Analizi`
                        : `Target: ${result.targetNodeName || run?.target.name} — ${runScenarioMeta?.en} Analysis`}
                    </h3>
                    <p className="text-[11px] text-neutral-500 font-mono mt-0.5">
                      scenario: {result.scenarioType || run?.scenario} · {run?.target.nodeType}
                      {run?.target.fqdn ? ` · ${run.target.fqdn}` : ''} · {isTr ? 'taban: canlı graf' : 'base: live graph'}
                    </p>
                  </div>

                  <div className="flex items-center gap-3">
                    <div className={`px-3 py-1 rounded-full border text-xs font-semibold ${riskTone}`}>
                      {riskLabel} ({isTr ? 'Skor' : 'Score'}: {result.riskScore}/100)
                    </div>
                  </div>
                </div>

                {/* Impact Metric Cards (real counts) */}
                <div className="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-5 gap-4 my-5">
                  <div className="p-4 rounded-lg bg-neutral-50 dark:bg-dark-bg border border-neutral-200 dark:border-dark-border">
                    <span className="text-xs text-neutral-500 font-medium">
                      {isTr ? 'Doğrulanmış Etkiler:' : 'Confirmed Impacts:'}
                    </span>
                    <div className="text-2xl font-bold text-neutral-900 dark:text-white mt-1">
                      {formatNumber(result.confirmedImpacts.length)}
                    </div>
                    <span className="text-[11px] text-rose-600 font-semibold">
                      {isTr ? 'Kesin Etki (Confirmed)' : 'Confirmed Impact'}
                    </span>
                  </div>
                  <div className="p-4 rounded-lg bg-neutral-50 dark:bg-dark-bg border border-neutral-200 dark:border-dark-border">
                    <span className="text-xs text-neutral-500 font-medium">
                      {isTr ? 'Olası Etkiler:' : 'Probable Impacts:'}
                    </span>
                    <div className="text-2xl font-bold text-neutral-900 dark:text-white mt-1">
                      {formatNumber(result.probableImpacts.length)}
                    </div>
                    <span className="text-[11px] text-amber-600 font-semibold">
                      {isTr ? 'Muhtemel Etki (Probable)' : 'Probable Impact'}
                    </span>
                  </div>
                  <div className="p-4 rounded-lg bg-neutral-50 dark:bg-dark-bg border border-neutral-200 dark:border-dark-border">
                    <span className="text-xs text-neutral-500 font-medium">
                      {isTr ? 'Mümkün Etkiler:' : 'Possible Impacts:'}
                    </span>
                    <div className="text-2xl font-bold text-neutral-900 dark:text-white mt-1">
                      {formatNumber(result.possibleImpacts.length)}
                    </div>
                    <span className="text-[11px] text-neutral-500 font-semibold">
                      {isTr ? 'Düşük Olasılık (Possible)' : 'Possible Impact'}
                    </span>
                  </div>
                  <div className="p-4 rounded-lg bg-neutral-50 dark:bg-dark-bg border border-neutral-200 dark:border-dark-border">
                    <span className="text-xs text-neutral-500 font-medium">
                      {isTr ? 'Kalan Cluster Belleği:' : 'Remaining Cluster RAM:'}
                    </span>
                    <div className={`text-2xl font-bold mt-1 ${result.clusterHeadroom.hasCapacityForFailover ? 'text-emerald-600' : 'text-rose-600'}`}>
                      {formatNumber(result.clusterHeadroom.remainingRamPercent)}%
                    </div>
                    <span className="text-[11px] text-neutral-500">
                      {result.clusterHeadroom.hasCapacityForFailover
                        ? isTr ? 'HA failover mümkün' : 'HA failover viable'
                        : isTr ? 'HA failover için yetersiz' : 'Insufficient for HA failover'}
                    </span>
                  </div>
                  <div className="p-4 rounded-lg bg-neutral-50 dark:bg-dark-bg border border-neutral-200 dark:border-dark-border">
                    <span className="text-xs text-neutral-500 font-medium">
                      {isTr ? 'Kalan Cluster CPU:' : 'Remaining Cluster CPU:'}
                    </span>
                    <div className={`text-2xl font-bold mt-1 ${result.clusterHeadroom.bottleneckResource.toLowerCase() === 'cpu' ? 'text-amber-600' : 'text-emerald-600'}`}>
                      {formatNumber(result.clusterHeadroom.remainingCpuPercent)}%
                    </div>
                    <span className="text-[11px] text-neutral-500">
                      {isTr ? 'Darboğaz:' : 'Bottleneck:'}{' '}
                      <span className="font-mono">{result.clusterHeadroom.bottleneckResource || '—'}</span>
                    </span>
                  </div>
                </div>

                {/* Cascading dependency paths from real engine data */}
                <div className="mt-4">
                  <h4 className="text-sm font-bold text-neutral-800 dark:text-neutral-200 mb-2">
                    {isTr ? 'Zincirleme Bağımlılık Etki Yolu:' : 'Cascading Dependency Path:'}
                  </h4>
                  {buildPaths(result.confirmedImpacts).length === 0 &&
                  buildPaths(result.probableImpacts).length === 0 &&
                  buildPaths(result.possibleImpacts).length === 0 ? (
                    <p className="p-3 rounded-lg border border-neutral-200 dark:border-dark-border bg-neutral-50/50 dark:bg-dark-bg text-xs text-neutral-500">
                      {isTr
                        ? 'Motor bu senaryo için zincirleme bağımlılık yolu raporlamadı.'
                        : 'The engine reported no cascading dependency paths for this scenario.'}
                    </p>
                  ) : (
                    <div className="p-3 rounded-lg border border-neutral-200 dark:border-dark-border bg-neutral-50/50 dark:bg-dark-bg text-xs space-y-2 font-mono">
                      {buildPaths(result.confirmedImpacts).map((entity) => (
                        <div key={`confirmed-${entity.id}`} className="flex flex-wrap items-center gap-2 text-rose-600 font-semibold">
                          <span>[{isTr ? 'Doğrulanmış' : 'Confirmed'}] {result.targetNodeName}</span>
                          <ArrowRight className="w-3.5 h-3.5 text-neutral-400" />
                          <span>{entity.dependencyPath || entity.name}</span>
                        </div>
                      ))}
                      {buildPaths(result.probableImpacts).map((entity) => (
                        <div key={`probable-${entity.id}`} className="flex flex-wrap items-center gap-2 text-amber-600">
                          <span>[{isTr ? 'Olası' : 'Probable'}] {result.targetNodeName}</span>
                          <ArrowRight className="w-3.5 h-3.5 text-neutral-400" />
                          <span>{entity.dependencyPath || entity.name}</span>
                        </div>
                      ))}
                      {buildPaths(result.possibleImpacts).map((entity) => (
                        <div key={`possible-${entity.id}`} className="flex flex-wrap items-center gap-2 text-neutral-500 dark:text-neutral-400">
                          <span>[{isTr ? 'Mümkün' : 'Possible'}] {result.targetNodeName}</span>
                          <ArrowRight className="w-3.5 h-3.5 text-neutral-400" />
                          <span>{entity.dependencyPath || entity.name}</span>
                        </div>
                      ))}
                    </div>
                  )}
                </div>

                {/* Engine recommendations */}
                {result.recommendations.length > 0 && (
                  <div className="mt-4 p-3 rounded-lg border border-blue-200 dark:border-blue-900 bg-blue-50 dark:bg-blue-950/30 text-xs text-blue-800 dark:text-blue-300 flex items-start gap-2">
                    <Info className="w-4 h-4 mt-0.5 shrink-0 text-blue-600" />
                    <div className="space-y-1">
                      {result.recommendations.map((recommendation, index) => (
                        <p key={index}>{recommendation}</p>
                      ))}
                    </div>
                  </div>
                )}
              </div>
            </div>
          )}
        </>
      )}
    </div>
  );
};

function buildPaths(entities: ImpactedEntity[]) {
  return entities.filter((entity) => entity.dependencyPath || entity.name);
}
