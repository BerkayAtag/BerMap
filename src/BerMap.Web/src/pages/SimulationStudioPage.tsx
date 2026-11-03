import React, { useMemo, useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { Link } from 'react-router-dom';
import { Card } from '@/components/ui/Card';
import { Button } from '@/components/ui/Button';
import {
  Play,
  RotateCcw,
  AlertTriangle,
  ArrowRight,
  Search,
  Loader2,
  Camera,
  Info,
  PlugZap,
} from 'lucide-react';
import { api } from '@/lib/api';
import { formatNumber, getErrorMessage } from '@/lib/format';
import { useLocale } from '@/lib/i18n';
import type {
  GraphQueryResult,
  ImpactAnalysisResult,
  InfrastructureNode,
  SnapshotSummary,
} from '@/types';

const SCENARIOS = ['host_failure', 'datastore_outage', 'network_cut'] as const;
type ScenarioType = (typeof SCENARIOS)[number];

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

  const [selectedScenario, setSelectedScenario] = useState<ScenarioType>('host_failure');
  const [search, setSearch] = useState('');
  const [selectedTarget, setSelectedTarget] = useState<string>('');
  const [selectedSnapshot, setSelectedSnapshot] = useState<string>('');
  const [isRunning, setIsRunning] = useState(false);
  const [runError, setRunError] = useState<string | null>(null);
  const [result, setResult] = useState<ImpactAnalysisResult | null>(null);

  const topologyQuery = useQuery<GraphQueryResult>({
    queryKey: ['graph', 'topology'],
    queryFn: () => api.get<GraphQueryResult>('/graph/topology?depth=3'),
    refetchInterval: 60_000,
  });

  const snapshotsQuery = useQuery<SnapshotSummary[]>({
    queryKey: ['snapshots'],
    queryFn: () => api.get<SnapshotSummary[]>('/snapshots'),
  });

  const nodes = topologyQuery.data?.nodes ?? [];
  const snapshots = snapshotsQuery.data ?? [];
  const topologyError = topologyQuery.isError ? getErrorMessage(topologyQuery.error) : null;

  const filteredNodes = useMemo(() => {
    const term = search.trim().toLowerCase();
    const base = term
      ? nodes.filter(
          (node: InfrastructureNode) =>
            node.name.toLowerCase().includes(term) ||
            node.fqdn.toLowerCase().includes(term) ||
            node.nodeType.toLowerCase().includes(term),
        )
      : nodes;
    return base.slice(0, 300);
  }, [nodes, search]);

  const scenarioMeta = SCENARIO_LABELS[selectedScenario];

  const reset = () => {
    setResult(null);
    setRunError(null);
  };

  const handleRunSimulation = async () => {
    if (!selectedTarget) return;
    setIsRunning(true);
    setRunError(null);
    setResult(null);
    try {
      const payload: { targetNodeId: string; scenarioType: ScenarioType; snapshotId?: string } = {
        targetNodeId: selectedTarget,
        scenarioType: selectedScenario,
      };
      if (selectedSnapshot) payload.snapshotId = selectedSnapshot;
      const response = await api.post<ImpactAnalysisResult>('/simulations/run', payload);
      setResult(response);
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
              BerSim Engine
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

      {/* Topology error banner */}
      {topologyError && (
        <div
          role="alert"
          className="p-3 rounded-lg border border-rose-200 dark:border-rose-900 bg-rose-50 dark:bg-rose-950/30 text-sm text-rose-700 dark:text-rose-300 flex items-center gap-2"
        >
          <AlertTriangle className="w-4 h-4" />
          <span className="flex-1">{topologyError}</span>
          <Button variant="outline" size="sm" onClick={() => void topologyQuery.refetch()}>
            {isTr ? 'Tekrar Dene' : 'Retry'}
          </Button>
        </div>
      )}

      {/* Empty state: no graph yet */}
      {!topologyQuery.isLoading && !topologyError && nodes.length === 0 ? (
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
          <Link to="/integrations" className="mt-4 inline-block">
            <Button variant="primary" size="sm" className="gap-2 bg-brand-primary">
              <PlugZap className="w-4 h-4" />
              {isTr ? 'Keşif başlat' : 'Start discovery'}
            </Button>
          </Link>
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
                <div className="flex items-center gap-2 px-3 py-2 rounded-lg bg-neutral-100 dark:bg-dark-border text-sm mb-2">
                  <Search className="w-4 h-4 text-neutral-400 shrink-0" />
                  <input
                    type="text"
                    value={search}
                    onChange={(event) => setSearch(event.target.value)}
                    placeholder={isTr ? 'Ad, FQDN veya tür ara...' : 'Search name, FQDN or type...'}
                    className="bg-transparent text-sm focus:outline-none w-full text-neutral-800 dark:text-neutral-200"
                  />
                </div>
                {topologyQuery.isLoading ? (
                  <p className="text-xs text-neutral-500 py-6 text-center">
                    {isTr ? 'Envanter yükleniyor...' : 'Loading inventory...'}
                  </p>
                ) : filteredNodes.length === 0 ? (
                  <p className="text-xs text-neutral-500 py-6 text-center">
                    {search
                      ? isTr ? 'Eşleşen bileşen yok.' : 'No matching components.'
                      : isTr ? 'Kullanılabilir bileşen yok.' : 'No components available.'}
                  </p>
                ) : (
                  <div className="max-h-72 overflow-y-auto space-y-1.5">
                    {filteredNodes.map((node) => (
                      <button
                        key={node.id}
                        type="button"
                        onClick={() => {
                          setSelectedTarget(node.id);
                          reset();
                        }}
                        className={`w-full text-left p-2.5 rounded-lg border text-xs transition-all ${
                          selectedTarget === node.id
                            ? 'border-brand-primary bg-brand-primary/5 dark:bg-brand-primary-dark/10 text-neutral-900 dark:text-white font-medium'
                            : 'border-neutral-200 dark:border-dark-border text-neutral-600 dark:text-neutral-400 hover:border-neutral-300'
                        }`}
                      >
                        <div className="flex items-center justify-between gap-2">
                          <span className="font-semibold truncate">{node.name}</span>
                          <span className="shrink-0 text-[10px] px-1.5 py-0.5 rounded bg-neutral-100 dark:bg-dark-border font-mono">
                            {node.nodeType}
                          </span>
                        </div>
                        {node.fqdn && (
                          <div className="text-[10px] text-neutral-500 font-mono truncate mt-0.5">{node.fqdn}</div>
                        )}
                      </button>
                    ))}
                  </div>
                )}
              </div>
            </Card>

            {/* Step 3: Snapshot (real list) & Parameters */}
            <Card className="p-5 flex flex-col justify-between">
              <div>
                <div className="flex items-center gap-2 text-xs font-bold uppercase tracking-wider text-neutral-500 mb-3">
                  <span className="w-5 h-5 rounded-full bg-brand-primary text-white flex items-center justify-center text-xs">
                    3
                  </span>
                  {isTr ? 'Snapshot & Parametreler' : 'Snapshot & Parameters'}
                </div>
                <div className="space-y-3 text-sm">
                  <div>
                    <label className="text-xs font-medium text-neutral-500">
                      {isTr ? 'Simülasyon Tabanı:' : 'Simulation Base:'}
                    </label>
                    <select
                      value={selectedSnapshot}
                      onChange={(event) => {
                        setSelectedSnapshot(event.target.value);
                        reset();
                      }}
                      disabled={snapshotsQuery.isLoading}
                      className="mt-1 h-10 w-full rounded-lg border border-neutral-300 dark:border-dark-border bg-white dark:bg-dark-card px-3 text-xs text-neutral-800 dark:text-neutral-200 focus:outline-none focus:ring-2 focus:ring-brand-primary/20"
                    >
                      <option value="">
                        {isTr ? 'Güncel canlı graf' : 'Current live graph'}
                      </option>
                      {snapshots.map((snapshot) => (
                        <option key={snapshot.id} value={snapshot.id}>
                          {snapshot.name} · v{snapshot.snapshotVersion} ·{' '}
                          {formatNumber(snapshot.nodeCount)} {isTr ? 'düğüm' : 'nodes'}
                        </option>
                      ))}
                    </select>
                    <p className="mt-1.5 text-[11px] text-neutral-500 flex items-start gap-1">
                      <Camera className="w-3 h-3 mt-0.5 shrink-0" />
                      {snapshotsQuery.isLoading
                        ? isTr
                          ? 'Snapshot listesi yükleniyor...'
                          : 'Loading snapshot list...'
                        : snapshots.length === 0
                          ? isTr
                            ? 'Henüz snapshot yok; simülasyon güncel canlı graf üzerinden çalışacak.'
                            : 'No snapshots yet; the simulation will run over the current live graph.'
                          : isTr
                            ? 'Bir snapshot seçilirse etki analizi o donmuş sürüm üzerinde hesaplanır.'
                            : 'If a snapshot is selected, impact is computed over that frozen version.'}
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
            <div className="space-y-6 animate-in fade-in duration-300">
              <div className="p-5 rounded-xl border border-neutral-200 dark:border-dark-border bg-white dark:bg-dark-card shadow-sm">
                <div className="flex flex-col md:flex-row md:items-center justify-between border-b border-neutral-200 dark:border-dark-border pb-4 gap-4">
                  <div>
                    <span className="text-xs uppercase font-bold tracking-wider text-brand-primary dark:text-brand-primary-dark">
                      {isTr ? 'Simülasyon Çıktısı (Blast Radius Report)' : 'Simulation Output (Blast Radius Report)'}
                    </span>
                    <h3 className="text-lg font-bold text-neutral-900 dark:text-white mt-1">
                      {isTr
                        ? `Hedef: ${result.targetNodeName} — ${scenarioMeta.tr} Analizi`
                        : `Target: ${result.targetNodeName} — ${scenarioMeta.en} Analysis`}
                    </h3>
                    <p className="text-[11px] text-neutral-500 font-mono mt-0.5">
                      scenario: {result.scenarioType}
                      {selectedSnapshot && (
                        <>
                          {' '}
                          · snapshot: {shortSnapshotName(snapshots, selectedSnapshot)}
                        </>
                      )}
                    </p>
                  </div>

                  <div className="flex items-center gap-3">
                    <div className={`px-3 py-1 rounded-full border text-xs font-semibold ${riskTone}`}>
                      {riskLabel} ({isTr ? 'Skor' : 'Score'}: {result.riskScore}/100)
                    </div>
                  </div>
                </div>

                {/* Impact Metric Cards (real counts) */}
                <div className="grid grid-cols-1 sm:grid-cols-4 gap-4 my-5">
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
                  {buildPaths(result.confirmedImpacts).length === 0 && buildPaths(result.probableImpacts).length === 0 ? (
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

function shortSnapshotName(snapshots: SnapshotSummary[], snapshotId: string): string {
  const found = snapshots.find((snapshot) => snapshot.id === snapshotId);
  return found ? `${found.name} (v${found.snapshotVersion})` : snapshotId;
}

function buildPaths(entities: ImpactAnalysisResult['confirmedImpacts']) {
  return entities.filter((entity) => entity.dependencyPath || entity.name);
}
