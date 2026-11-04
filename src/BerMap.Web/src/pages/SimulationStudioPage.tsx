import React, { useState } from 'react';
import { Link, useSearchParams } from 'react-router-dom';
import { useQuery } from '@tanstack/react-query';
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
  Printer,
  Download,
  ExternalLink,
} from 'lucide-react';
import { api } from '@/lib/api';
import { formatNumber, getErrorMessage, getErrorStatus } from '@/lib/format';
import { downloadBlob } from '@/lib/csv';
import { useLocale } from '@/lib/i18n';
import { useT } from '@/lib/i18n-runtime';
import { NodePicker } from '@/components/inventory/NodePicker';
import { useInventoryNodes } from '@/hooks/useInventoryNodes';
import { useAuthStore } from '@/stores/authStore';
import {
  SCENARIOS,
  SCENARIO_LABELS,
  SCENARIO_TARGET_TYPES,
  isValidTarget,
  localizeRecommendations,
  normalizeScenario,
  parseDependencyPath,
  riskLevel,
  riskLevelLabel,
  scenarioForNodeType,
  type ScenarioType,
} from './simulation/scenarios';
import {
  buildRiskReportHtml,
  buildRiskReportMarkdown,
  openRiskReportPrintView,
  riskReportFileName,
  type RiskReportInput,
} from './simulation/riskReport';
import type { ApiError, ClusterHeadroom, ImpactAnalysisResult, ImpactedEntity, InventoryNode, InventoryNodeDetail } from '@/types';

type TargetSummary = Pick<InventoryNode, 'id' | 'name' | 'nodeType' | 'fqdn'> &
  Partial<Pick<InventoryNode, 'status' | 'isStale' | 'sourceSystems'>>;

/** A result together with the parameters that produced it. */
interface SimulationRun {
  scenario: ScenarioType;
  target: TargetSummary;
  result: ImpactAnalysisResult;
  ranAt: Date;
}

const RISK_TONE = {
  high: 'bg-rose-50 dark:bg-rose-950/40 text-rose-700 dark:text-rose-400 border-rose-200 dark:border-rose-900',
  medium: 'bg-amber-50 dark:bg-amber-950/40 text-amber-700 dark:text-amber-400 border-amber-200 dark:border-amber-900',
  low: 'bg-emerald-50 dark:bg-emerald-950/40 text-emerald-700 dark:text-emerald-400 border-emerald-200 dark:border-emerald-900',
} as const;

const STEP_BADGE = 'w-5 h-5 rounded-full bg-brand-primary text-white flex items-center justify-center text-xs';
const STEP_TITLE = 'flex items-center gap-2 text-xs font-bold uppercase tracking-wider text-neutral-500 mb-3';

export const SimulationStudioPage: React.FC = () => {
  const locale = useLocale();
  const isTr = locale === 'tr';
  const t = useT();
  const user = useAuthStore((state) => state.user);
  const [searchParams, setSearchParams] = useSearchParams();

  // The URL is the source of truth for the selection, so links such as
  // /simulation?target=<id>&scenario=host_failure open a ready-to-run study.
  const targetId = searchParams.get('target')?.trim() || null;
  const urlScenario = normalizeScenario(searchParams.get('scenario'));

  // The node object for a target picked in the list (avoids a refetch).
  const [pickedNode, setPickedNode] = useState<InventoryNode | null>(null);
  const [isRunning, setIsRunning] = useState(false);
  const [runError, setRunError] = useState<string | null>(null);
  const [run, setRun] = useState<SimulationRun | null>(null);
  const [reportNotice, setReportNotice] = useState<string | null>(null);

  const targetQuery = useQuery<InventoryNodeDetail>({
    queryKey: ['inventory', 'node', targetId],
    queryFn: () => api.get<InventoryNodeDetail>(`/inventory/nodes/${encodeURIComponent(targetId ?? '')}`),
    enabled: !!targetId && pickedNode?.id !== targetId,
    retry: (count, error) => getErrorStatus(error) === undefined && count < 2,
  });
  const targetNode: InventoryNode | null =
    pickedNode && pickedNode.id === targetId ? pickedNode : (targetQuery.data?.node ?? null);
  const targetMissing = !!targetId && !targetNode && getErrorStatus(targetQuery.error) === 404;
  const targetLoadError = !!targetId && !targetNode && targetQuery.isError && !targetMissing;

  // With only ?target=, the scenario follows from the node type.
  const scenario: ScenarioType | null = urlScenario ?? (targetNode ? scenarioForNodeType(targetNode.nodeType) : null);
  const targetMatches = !!scenario && !!targetNode && isValidTarget(scenario, targetNode.nodeType);

  // Lightweight existence check: is there any discovered inventory at all?
  const inventoryProbe = useInventoryNodes({ page: 1, pageSize: 1 }, { refetchInterval: 60_000 });
  const inventoryTotal = inventoryProbe.data?.totalCount ?? 0;
  const inventoryError = inventoryProbe.isError ? getErrorMessage(inventoryProbe.error) : null;

  const result = run?.result ?? null;

  const updateSelection = (next: { scenario?: ScenarioType | null; target?: string | null }) => {
    const params = new URLSearchParams(searchParams);
    if ('scenario' in next) {
      if (next.scenario) params.set('scenario', next.scenario);
      else params.delete('scenario');
    }
    if ('target' in next) {
      if (next.target) params.set('target', next.target);
      else params.delete('target');
    }
    setSearchParams(params, { replace: true });
  };

  const selectScenario = (next: ScenarioType) => {
    setRunError(null);
    // A target the new scenario cannot fail is dropped instead of producing a 400.
    const keepTarget = targetNode ? isValidTarget(next, targetNode.nodeType) : !!targetId && !targetNode;
    updateSelection({ scenario: next, target: keepTarget ? targetId : null });
    if (!keepTarget) setPickedNode(null);
  };

  const selectTarget = (node: InventoryNode) => {
    setPickedNode(node);
    setRunError(null);
    updateSelection({ scenario: scenario ?? scenarioForNodeType(node.nodeType), target: node.id });
  };

  const reset = () => {
    setRun(null);
    setRunError(null);
    setReportNotice(null);
    setPickedNode(null);
    updateSelection({ scenario: null, target: null });
  };

  const handleRunSimulation = async () => {
    if (!targetNode || !scenario || !targetMatches) return;
    const target = targetNode;
    setIsRunning(true);
    setRunError(null);
    setReportNotice(null);
    setRun(null);
    try {
      // The engine always runs on the live graph.
      const response = await api.post<ImpactAnalysisResult>('/simulations/run', {
        targetNodeId: target.id,
        scenarioType: scenario,
      });
      setRun({
        scenario,
        target: {
          id: target.id,
          name: target.name,
          nodeType: target.nodeType,
          fqdn: target.fqdn,
          status: target.status,
          isStale: target.isStale,
          sourceSystems: target.sourceSystems,
        },
        result: response,
        ranAt: new Date(),
      });
    } catch (error) {
      const apiError = error as Partial<ApiError>;
      if (apiError.code === 'invalid_simulation') {
        setRunError(
          (isTr
            ? 'Bu senaryo seçilen hedefe uygulanamaz. Senaryoya uygun türde bir hedef seçin.'
            : 'This scenario cannot be applied to the selected target. Pick a target of a type the scenario supports.') +
            (apiError.message ? ` (${apiError.message})` : ''),
        );
      } else if (getErrorStatus(error) === 404) {
        setRunError(
          isTr
            ? 'Hedef bileşen artık envanterde yok. Listeden başka bir hedef seçin.'
            : 'The target component is no longer in the inventory. Pick another target from the list.',
        );
      } else {
        setRunError(getErrorMessage(error));
      }
    } finally {
      setIsRunning(false);
    }
  };

  const reportInput = (): RiskReportInput | null =>
    run
      ? {
          result: run.result,
          scenario: run.scenario,
          target: run.target,
          generatedAt: new Date(),
          generatedBy: user?.displayName || user?.username || '—',
          isTr,
        }
      : null;

  const downloadMarkdown = () => {
    const input = reportInput();
    if (!input) return;
    downloadBlob(
      riskReportFileName(input, 'md'),
      new Blob([buildRiskReportMarkdown(input)], { type: 'text/markdown;charset=utf-8' }),
    );
  };

  const printReport = () => {
    const input = reportInput();
    if (!input) return;
    setReportNotice(null);
    if (!openRiskReportPrintView(input)) {
      // Pop-up blocked: hand over the same print view as a file instead.
      downloadBlob(
        riskReportFileName(input, 'html'),
        new Blob([buildRiskReportHtml(input)], { type: 'text/html;charset=utf-8' }),
      );
      setReportNotice(
        isTr
          ? 'Tarayıcı yeni pencereyi engelledi; yazdırılabilir rapor HTML dosyası olarak indirildi.'
          : 'The browser blocked the new window; the printable report was downloaded as an HTML file instead.',
      );
    }
  };

  const level = result ? riskLevel(result.riskScore) : 'low';
  const recommendations = result ? localizeRecommendations(result, isTr) : [];
  const allowedTypes = scenario ? SCENARIO_TARGET_TYPES[scenario] : undefined;
  const canRun = !isRunning && targetMatches;

  return (
    <div className="space-y-6 max-w-7xl mx-auto">
      {/* Page Title */}
      <div className="flex flex-col sm:flex-row sm:items-center sm:justify-between gap-4">
        <div className="min-w-0">
          <div className="flex flex-wrap items-center gap-2">
            <h1 className="text-2xl font-bold tracking-tight text-neutral-900 dark:text-white">
              {isTr ? 'Simülasyon ve Etki Analizi' : 'Simulation & Impact Analysis'}
            </h1>
            <span className="text-xs px-2.5 py-0.5 rounded-full bg-brand-primary text-white font-bold tracking-wide">
              {t('berSimEngine')}
            </span>
          </div>
          <p className="text-sm text-neutral-500 dark:text-neutral-400 mt-1">
            {isTr
              ? 'Değiştirmeden önce simüle edin: bir senaryo ve uygun bir hedef seçin, gerçek envanter grafiği üzerinde deterministik etki hesabı çalıştırın.'
              : 'Simulate before you change: pick a scenario and a matching target, and run a deterministic impact computation over the real inventory graph.'}
          </p>
        </div>

        <div className="flex items-center gap-3 shrink-0">
          <Button
            variant="outline"
            size="sm"
            onClick={reset}
            className="gap-2"
            disabled={isRunning || (!scenario && !targetId && !run && !runError)}
          >
            <RotateCcw className="w-4 h-4" />
            {isTr ? 'Sıfırla' : 'Reset'}
          </Button>
          <Button
            variant="primary"
            size="sm"
            onClick={() => void handleRunSimulation()}
            disabled={!canRun}
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
          <AlertTriangle className="w-4 h-4 shrink-0" />
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
            {isTr ? 'Simüle edilecek bileşen yok' : 'No components to simulate yet'}
          </h2>
          <p className="mt-1 text-sm text-neutral-500 dark:text-neutral-400 max-w-xl mx-auto">
            {isTr
              ? 'BerSim yalnızca gerçek keşif verisi üzerinde çalışır. Entegrasyonlar sayfasından Active Directory veya vCenter keşfi başlatın; keşif ve uzlaştırma tamamlandığında bileşenler buraya otomatik gelir.'
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
            <Card className="p-5">
              <div className={STEP_TITLE}>
                <span className={STEP_BADGE}>1</span>
                {isTr ? 'Senaryo Türü' : 'Scenario Type'}
              </div>
              <div className="space-y-2">
                {SCENARIOS.map((item) => (
                  <button
                    key={item}
                    type="button"
                    onClick={() => selectScenario(item)}
                    aria-pressed={scenario === item}
                    className={`w-full text-left p-3 rounded-lg border text-sm transition-all ${
                      scenario === item
                        ? 'border-brand-primary bg-brand-primary/5 dark:bg-brand-primary-dark/10 text-neutral-900 dark:text-white font-medium'
                        : 'border-neutral-200 dark:border-dark-border text-neutral-600 dark:text-neutral-400 hover:border-neutral-300'
                    }`}
                  >
                    <div className="font-semibold">{isTr ? SCENARIO_LABELS[item].tr : SCENARIO_LABELS[item].en}</div>
                    <div className="text-xs text-neutral-500 mt-0.5">
                      {isTr ? SCENARIO_LABELS[item].descTr : SCENARIO_LABELS[item].descEn}
                    </div>
                  </button>
                ))}
              </div>
            </Card>

            {/* Step 2: Target Entity (real inventory, filtered to the scenario's node types) */}
            <Card className="p-5 min-w-0">
              <div className={STEP_TITLE}>
                <span className={STEP_BADGE}>2</span>
                {isTr ? 'Gerçek Envanterden Hedef' : 'Target from Real Inventory'}
              </div>
              {scenario ? (
                <NodePicker
                  selectedId={targetId}
                  onSelect={selectTarget}
                  nodeTypes={allowedTypes}
                  label={isTr ? 'Simülasyon hedefi' : 'Simulation target'}
                />
              ) : (
                <p className="p-4 rounded-lg border border-dashed border-neutral-300 dark:border-dark-border text-xs text-neutral-500 text-center">
                  {isTr
                    ? 'Önce bir senaryo seçin; hedef listesi senaryonun uygulanabildiği bileşen türlerine göre süzülür.'
                    : 'Pick a scenario first; the target list is filtered to the component types the scenario applies to.'}
                </p>
              )}
            </Card>

            {/* Step 3: Review the selection */}
            <Card className="p-5 min-w-0">
              <div className={STEP_TITLE}>
                <span className={STEP_BADGE}>3</span>
                {isTr ? 'Seçimi Gözden Geçir' : 'Review Selection'}
              </div>
              <SelectionSummary
                isTr={isTr}
                scenario={scenario}
                targetId={targetId}
                targetNode={targetNode}
                isLoading={!!targetId && !targetNode && targetQuery.isLoading}
                targetMissing={targetMissing}
                targetLoadError={targetLoadError ? getErrorMessage(targetQuery.error) : null}
                targetMatches={targetMatches}
              />
            </Card>
          </div>

          {/* Run error */}
          {runError && (
            <div
              role="alert"
              className="p-3 rounded-lg border border-rose-200 dark:border-rose-900 bg-rose-50 dark:bg-rose-950/30 text-sm text-rose-700 dark:text-rose-300 flex items-center gap-2"
            >
              <AlertTriangle className="w-4 h-4 shrink-0" />
              {runError}
            </div>
          )}

          {/* Simulation Results Section (real engine output) */}
          {result && run && (
            <div className="space-y-6 bm-fade-in">
              <div className="p-5 rounded-xl border border-neutral-200 dark:border-dark-border bg-white dark:bg-dark-card shadow-sm">
                <div className="flex flex-col md:flex-row md:items-center justify-between border-b border-neutral-200 dark:border-dark-border pb-4 gap-4">
                  <div className="min-w-0">
                    <span className="text-xs uppercase font-bold tracking-wider text-brand-primary dark:text-brand-primary-dark">
                      {isTr ? 'Simülasyon Çıktısı (Etki Yarıçapı Raporu)' : 'Simulation Output (Blast Radius Report)'}
                    </span>
                    <h3 className="text-lg font-bold text-neutral-900 dark:text-white mt-1 break-words">
                      {isTr
                        ? `Hedef: ${result.targetNodeName || run.target.name} — ${SCENARIO_LABELS[run.scenario].tr}`
                        : `Target: ${result.targetNodeName || run.target.name} — ${SCENARIO_LABELS[run.scenario].en}`}
                    </h3>
                    <p className="text-[11px] text-neutral-500 font-mono mt-0.5 break-all">
                      scenario: {run.scenario} · {run.target.nodeType}
                      {run.target.fqdn ? ` · ${run.target.fqdn}` : ''} · {isTr ? 'taban: canlı graf' : 'base: live graph'}
                    </p>
                  </div>

                  <div className="flex flex-wrap items-center gap-2">
                    <div className={`px-3 py-1 rounded-full border text-xs font-semibold ${RISK_TONE[level]}`}>
                      {isTr ? 'Risk Seviyesi' : 'Risk Level'}: {riskLevelLabel(level, isTr)} ({isTr ? 'Skor' : 'Score'}:{' '}
                      {result.riskScore}/100)
                    </div>
                    <Button variant="outline" size="sm" className="gap-2" onClick={downloadMarkdown}>
                      <Download className="w-4 h-4" />
                      {isTr ? 'Raporu indir' : 'Download report'}
                    </Button>
                    <Button variant="outline" size="sm" className="gap-2" onClick={printReport}>
                      <Printer className="w-4 h-4" />
                      {isTr ? 'Yazdır' : 'Print'}
                    </Button>
                  </div>
                </div>

                {reportNotice && (
                  <p className="mt-3 text-xs text-amber-700 dark:text-amber-300" role="status">
                    {reportNotice}
                  </p>
                )}

                {/* Impact Metric Cards (real counts) */}
                <div className="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-5 gap-4 my-5">
                  <CountCard
                    label={isTr ? 'Doğrulanmış Etkiler' : 'Confirmed Impacts'}
                    value={result.confirmedImpacts.length}
                    note={isTr ? 'Kesin etki' : 'Certain impact'}
                    noteClass="text-rose-600"
                  />
                  <CountCard
                    label={isTr ? 'Olası Etkiler' : 'Probable Impacts'}
                    value={result.probableImpacts.length}
                    note={isTr ? 'Muhtemel etki' : 'Likely impact'}
                    noteClass="text-amber-600"
                  />
                  <CountCard
                    label={isTr ? 'Mümkün Etkiler' : 'Possible Impacts'}
                    value={result.possibleImpacts.length}
                    note={isTr ? 'Düşük olasılık' : 'Low likelihood'}
                    noteClass="text-neutral-500"
                  />
                  <HeadroomCard resource="Ram" headroom={result.clusterHeadroom} isTr={isTr} />
                  <HeadroomCard resource="Cpu" headroom={result.clusterHeadroom} isTr={isTr} />
                </div>

                {/* Cascading dependency paths from real engine data */}
                <div className="mt-4">
                  <h4 className="text-sm font-bold text-neutral-800 dark:text-neutral-200 mb-2">
                    {isTr ? 'Zincirleme Bağımlılık Etki Yolu' : 'Cascading Dependency Paths'}
                  </h4>
                  {result.confirmedImpacts.length + result.probableImpacts.length + result.possibleImpacts.length === 0 ? (
                    <p className="p-3 rounded-lg border border-neutral-200 dark:border-dark-border bg-neutral-50/50 dark:bg-dark-bg text-xs text-neutral-500">
                      {isTr
                        ? 'Motor bu senaryo için zincirleme bağımlılık yolu raporlamadı.'
                        : 'The engine reported no cascading dependency paths for this scenario.'}
                    </p>
                  ) : (
                    <div className="p-3 rounded-lg border border-neutral-200 dark:border-dark-border bg-neutral-50/50 dark:bg-dark-bg text-xs space-y-2 font-mono">
                      <PathRows entities={result.confirmedImpacts} label={isTr ? 'Doğrulanmış' : 'Confirmed'} tone="text-rose-600 dark:text-rose-400 font-semibold" isTr={isTr} />
                      <PathRows entities={result.probableImpacts} label={isTr ? 'Olası' : 'Probable'} tone="text-amber-600 dark:text-amber-400" isTr={isTr} />
                      <PathRows entities={result.possibleImpacts} label={isTr ? 'Mümkün' : 'Possible'} tone="text-neutral-500 dark:text-neutral-400" isTr={isTr} />
                    </div>
                  )}
                </div>

                {/* Engine recommendations, localized by code */}
                {recommendations.length > 0 && (
                  <div className="mt-4 p-3 rounded-lg border border-blue-200 dark:border-blue-900 bg-blue-50 dark:bg-blue-950/30 text-xs text-blue-800 dark:text-blue-300 flex items-start gap-2">
                    <Info className="w-4 h-4 mt-0.5 shrink-0 text-blue-600" />
                    <ul className="space-y-1">
                      {recommendations.map((recommendation, index) => (
                        <li key={index}>{recommendation}</li>
                      ))}
                    </ul>
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

interface SelectionSummaryProps {
  isTr: boolean;
  scenario: ScenarioType | null;
  targetId: string | null;
  targetNode: InventoryNode | null;
  isLoading: boolean;
  targetMissing: boolean;
  targetLoadError: string | null;
  targetMatches: boolean;
}

const SelectionSummary: React.FC<SelectionSummaryProps> = ({
  isTr,
  scenario,
  targetId,
  targetNode,
  isLoading,
  targetMissing,
  targetLoadError,
  targetMatches,
}) => {
  const row = (label: string, value: React.ReactNode) => (
    <div className="flex items-start justify-between gap-3 py-1.5 border-b border-neutral-100 dark:border-dark-border last:border-0">
      <span className="text-neutral-500 shrink-0">{label}</span>
      <span className="text-right text-neutral-800 dark:text-neutral-200 min-w-0 break-words">{value}</span>
    </div>
  );

  return (
    <div className="space-y-3 text-xs">
      <div>
        {row(isTr ? 'Senaryo' : 'Scenario', scenario ? (isTr ? SCENARIO_LABELS[scenario].tr : SCENARIO_LABELS[scenario].en) : '—')}
        {row(
          isTr ? 'Hedef' : 'Target',
          isLoading ? (
            <Loader2 className="w-3.5 h-3.5 animate-spin inline" aria-label={isTr ? 'Yükleniyor' : 'Loading'} />
          ) : targetNode ? (
            <span className="font-semibold">{targetNode.name}</span>
          ) : (
            '—'
          ),
        )}
        {targetNode && (
          <>
            {row(isTr ? 'Tür' : 'Type', <span lang="en" className="font-mono">{targetNode.nodeType}</span>)}
            {targetNode.fqdn && row('FQDN', <span className="font-mono">{targetNode.fqdn}</span>)}
            {row(
              isTr ? 'Kaynak' : 'Source',
              targetNode.sourceSystems?.length ? targetNode.sourceSystems.join(', ') : targetNode.discoverySource || '—',
            )}
          </>
        )}
        {row(
          isTr ? 'Simülasyon tabanı' : 'Simulation base',
          <span className="inline-flex items-center gap-1">
            <Radio className="w-3.5 h-3.5 text-status-healthy shrink-0" aria-hidden="true" />
            {isTr ? 'Canlı graf' : 'Live graph'}
          </span>,
        )}
      </div>

      {targetMissing && (
        <p className="p-2.5 rounded-lg border border-amber-200 dark:border-amber-900/50 bg-amber-50 dark:bg-amber-950/30 text-amber-800 dark:text-amber-300" role="status">
          {isTr
            ? 'Bağlantıdaki hedef envanterde bulunamadı (silinmiş olabilir). Listeden bir hedef seçin.'
            : 'The linked target was not found in the inventory (it may have been removed). Pick a target from the list.'}
        </p>
      )}
      {targetLoadError && (
        <p className="p-2.5 rounded-lg border border-rose-200 dark:border-rose-900 bg-rose-50 dark:bg-rose-950/30 text-rose-700 dark:text-rose-300" role="alert">
          {targetLoadError}
        </p>
      )}
      {scenario && targetNode && !targetMatches && (
        <p className="p-2.5 rounded-lg border border-amber-200 dark:border-amber-900/50 bg-amber-50 dark:bg-amber-950/30 text-amber-800 dark:text-amber-300" role="status">
          {isTr
            ? `Bu senaryo ${targetNode.nodeType} türüne uygulanamaz. Uygun türler: ${SCENARIO_TARGET_TYPES[scenario].join(', ')}.`
            : `This scenario does not apply to ${targetNode.nodeType}. Supported types: ${SCENARIO_TARGET_TYPES[scenario].join(', ')}.`}
        </p>
      )}
      {targetNode?.isStale && (
        <p className="p-2.5 rounded-lg border border-amber-200 dark:border-amber-900/50 bg-amber-50 dark:bg-amber-950/30 text-amber-800 dark:text-amber-300">
          {isTr
            ? 'Hedef eskimiş (son keşifte görülmedi); sonuç güncel olmayabilir.'
            : 'The target is stale (not seen in the latest discovery); the result may be out of date.'}
        </p>
      )}
      {targetNode && targetId && (
        <Link
          to={`/inventory?node=${encodeURIComponent(targetId)}`}
          className="inline-flex items-center gap-1 text-brand-primary dark:text-brand-primary-dark hover:underline"
        >
          <ExternalLink className="w-3.5 h-3.5" aria-hidden="true" />
          {isTr ? 'Envanterde aç' : 'Open in inventory'}
        </Link>
      )}

      <p className="p-2.5 rounded-lg bg-neutral-50 dark:bg-dark-bg border border-neutral-200 dark:border-dark-border text-neutral-600 dark:text-neutral-400">
        <AlertTriangle className="w-3.5 h-3.5 inline mr-1 text-amber-600" aria-hidden="true" />
        {isTr
          ? 'Simülasyon deterministiktir ve salt-okunurdur; üretim sistemlerine hiçbir çağrı yapılmaz.'
          : 'The simulation is deterministic and read-only; no calls are made to production systems.'}
      </p>
    </div>
  );
};

const CountCard: React.FC<{ label: string; value: number; note: string; noteClass: string }> = ({
  label,
  value,
  note,
  noteClass,
}) => (
  <div className="p-4 rounded-lg bg-neutral-50 dark:bg-dark-bg border border-neutral-200 dark:border-dark-border">
    <span className="text-xs text-neutral-500 font-medium">{label}</span>
    <div className="text-2xl font-bold text-neutral-900 dark:text-white mt-1">{formatNumber(value)}</div>
    <span className={`text-[11px] font-semibold ${noteClass}`}>{note}</span>
  </div>
);

/**
 * Remaining cluster CPU or RAM. The colour comes from the engine's
 * bottleneckCode: the bottleneck resource is amber (rose when failover is not
 * possible), the other resource green; Unknown / NotApplicable are neutral.
 */
const HeadroomCard: React.FC<{ resource: 'Cpu' | 'Ram'; headroom: ClusterHeadroom; isTr: boolean }> = ({
  resource,
  headroom,
  isTr,
}) => {
  const code =
    headroom.bottleneckCode ??
    // Older servers: no code, so only the overall failover verdict is known.
    (headroom.hasCapacityForFailover ? 'None' : undefined);
  const isRam = resource === 'Ram';
  const value = isRam ? headroom.remainingRamPercent : headroom.remainingCpuPercent;
  const n1 = headroom.isPostFailure;
  const label = isRam
    ? n1
      ? isTr ? 'N-1 Sonrası Kalan Bellek' : 'Remaining RAM after N-1'
      : isTr ? 'Kalan Cluster Belleği' : 'Remaining Cluster RAM'
    : n1
      ? isTr ? 'N-1 Sonrası Kalan CPU' : 'Remaining CPU after N-1'
      : isTr ? 'Kalan Cluster CPU' : 'Remaining Cluster CPU';

  let tone = 'text-neutral-500';
  let note: string;
  let shown = true;
  if (code === 'NotApplicable') {
    shown = false;
    note = isTr ? 'Bu senaryo için uygulanamaz' : 'Not applicable to this scenario';
  } else if (code === 'Unknown') {
    shown = false;
    note = isTr ? 'Kapasite metriği yok' : 'No capacity metrics';
  } else if (code === resource) {
    tone = headroom.hasCapacityForFailover ? 'text-amber-600' : 'text-rose-600';
    note = headroom.hasCapacityForFailover
      ? isTr ? 'Darboğaz kaynak' : 'Bottleneck resource'
      : isTr ? 'Darboğaz — failover için yetersiz' : 'Bottleneck — insufficient for failover';
  } else if (code === 'None' || code === 'Cpu' || code === 'Ram') {
    tone = 'text-emerald-600';
    note = headroom.hasCapacityForFailover
      ? isTr ? 'HA failover mümkün' : 'HA failover viable'
      : isTr ? 'Yeterli' : 'Sufficient';
  } else {
    note = headroom.bottleneckResource || '—';
  }

  return (
    <div className="p-4 rounded-lg bg-neutral-50 dark:bg-dark-bg border border-neutral-200 dark:border-dark-border min-w-0">
      <span className="text-xs text-neutral-500 font-medium">{label}</span>
      <div className={`text-2xl font-bold mt-1 ${tone}`}>
        {shown ? `${value.toLocaleString(isTr ? 'tr-TR' : 'en-US', { maximumFractionDigits: 1 })}%` : '—'}
      </div>
      <span className="text-[11px] text-neutral-500 block truncate" title={note}>
        {note}
      </span>
      {headroom.clusterName && shown && (
        <span className="text-[10px] text-neutral-400 font-mono block truncate" title={headroom.clusterName}>
          {headroom.clusterName}
        </span>
      )}
    </div>
  );
};

/** One row per impacted entity; the engine path already begins at the failure. */
const PathRows: React.FC<{ entities: ImpactedEntity[]; label: string; tone: string; isTr: boolean }> = ({
  entities,
  label,
  tone,
  isTr,
}) => (
  <>
    {entities.map((entity) => (
      <div key={`${label}-${entity.id}`} className={`flex flex-wrap items-center gap-x-2 gap-y-1 ${tone}`}>
        <span className="font-sans text-[10px] uppercase tracking-wide opacity-80">[{label}]</span>
        {parseDependencyPath(entity.dependencyPath, entity.name).map((segment, index, segments) =>
          segment.kind === 'edge' ? (
            <span key={index} className="inline-flex items-center gap-1 text-neutral-400">
              <ArrowRight className="w-3 h-3" aria-hidden="true" />
              {segment.text === 'DOWN' ? (
                <span className="text-[10px]">{isTr ? 'KESİNTİ' : 'DOWN'}</span>
              ) : (
                <span lang="en" className="text-[10px]">{segment.text}</span>
              )}
              <ArrowRight className="w-3 h-3" aria-hidden="true" />
            </span>
          ) : (
            <span key={index} className="inline-flex items-center gap-2">
              {index > 0 && segments[index - 1].kind === 'node' && (
                <ArrowRight className="w-3 h-3 text-neutral-400" aria-hidden="true" />
              )}
              {segment.text}
            </span>
          ),
        )}
      </div>
    ))}
  </>
);
