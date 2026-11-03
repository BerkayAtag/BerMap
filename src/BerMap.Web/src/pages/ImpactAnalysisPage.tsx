import React, { useMemo, useRef, useState } from 'react';
import { Card } from '@/components/ui/Card';
import { Button } from '@/components/ui/Button';
import { ButtonLink } from '@/components/ui/ButtonLink';
import { Loader2, Search, ShieldAlert, ShieldCheck, AlertTriangle, RefreshCw, PlugZap, Info } from 'lucide-react';
import { api } from '@/lib/api';
import { formatNumber, getErrorMessage } from '@/lib/format';
import { useLocale } from '@/lib/i18n';
import { useT } from '@/lib/i18n-runtime';
import { NodePicker } from '@/components/inventory/NodePicker';
import { useInventoryNodes } from '@/hooks/useInventoryNodes';
import type { ImpactAnalysisResult, ImpactedEntity, InventoryNode } from '@/types';

const EMPTY_RESULTS: Record<ScenarioType, ImpactAnalysisResult | undefined> = {
  host_failure: undefined,
  datastore_outage: undefined,
  network_cut: undefined,
};

const EMPTY_ERRORS: Record<ScenarioType, string | undefined> = {
  host_failure: undefined,
  datastore_outage: undefined,
  network_cut: undefined,
};

const SCENARIO_TYPES = ['host_failure', 'datastore_outage', 'network_cut'] as const;
type ScenarioType = (typeof SCENARIO_TYPES)[number];

export const ImpactAnalysisPage: React.FC = () => {
  const locale = useLocale();
  const isTr = locale === 'tr';
  const t = useT();

  const [target, setTarget] = useState<InventoryNode | null>(null);
  const [results, setResults] = useState(EMPTY_RESULTS);
  const [errors, setErrors] = useState(EMPTY_ERRORS);
  const [running, setRunning] = useState(false);
  // Incremented on every run / target change so late responses from an older run are discarded.
  const runIdRef = useRef(0);

  const inventoryProbe = useInventoryNodes({ page: 1, pageSize: 1 }, { refetchInterval: 60_000 });
  const inventoryTotal = inventoryProbe.data?.totalCount ?? 0;
  const error = inventoryProbe.isError ? getErrorMessage(inventoryProbe.error) : null;
  const targetId = target?.id ?? '';

  const selectTarget = (node: InventoryNode) => {
    if (node.id === targetId) return;
    runIdRef.current += 1;
    setTarget(node);
    setResults(EMPTY_RESULTS);
    setErrors(EMPTY_ERRORS);
    setRunning(false);
  };

  const runAllScenarios = async () => {
    if (!targetId) return;
    const runId = ++runIdRef.current;
    setRunning(true);
    setResults(EMPTY_RESULTS);
    setErrors(EMPTY_ERRORS);

    const outcomes = await Promise.allSettled(
      SCENARIO_TYPES.map((scenarioType: ScenarioType) =>
        api.post<ImpactAnalysisResult>('/simulations/run', {
          targetNodeId: targetId,
          scenarioType,
        }),
      ),
    );

    if (runId !== runIdRef.current) return;
    setRunning(false);
    setResults({
      host_failure:
        outcomes[0].status === 'fulfilled' ? outcomes[0].value : undefined,
      datastore_outage:
        outcomes[1].status === 'fulfilled' ? outcomes[1].value : undefined,
      network_cut:
        outcomes[2].status === 'fulfilled' ? outcomes[2].value : undefined,
    });
    setErrors({
      host_failure: outcomes[0].status === 'rejected' ? getErrorMessage(outcomes[0].reason) : undefined,
      datastore_outage: outcomes[1].status === 'rejected' ? getErrorMessage(outcomes[1].reason) : undefined,
      network_cut: outcomes[2].status === 'rejected' ? getErrorMessage(outcomes[2].reason) : undefined,
    });
  };

  const impactSummary = useMemo(() => {
    const entries = Object.values(results).filter(Boolean) as ImpactAnalysisResult[];
    const levels = new Map<string, number>();
    entries.forEach((result) => {
      result.confirmedImpacts.forEach(() => levels.set('confirmed', (levels.get('confirmed') ?? 0) + 1));
      result.probableImpacts.forEach(() => levels.set('probable', (levels.get('probable') ?? 0) + 1));
      result.possibleImpacts.forEach(() => levels.set('possible', (levels.get('possible') ?? 0) + 1));
    });
    const worst = entries.reduce((max, result) => Math.max(max, result.riskScore), -1);
    return { entries, confirmed: levels.get('confirmed') ?? 0, probable: levels.get('probable') ?? 0, possible: levels.get('possible') ?? 0, worstRisk: worst };
  }, [results]);

  return (
    <div className="space-y-6 max-w-7xl mx-auto">
      {/* Header */}
      <div className="flex flex-col sm:flex-row sm:items-center sm:justify-between gap-4">
        <div>
          <div className="flex flex-wrap items-center gap-2">
            <h1 className="text-2xl font-bold tracking-tight text-neutral-900 dark:text-white">
              {isTr ? 'Etki Analizi — Gerçek Envanter' : 'Impact Analysis — Real Inventory'}
            </h1>
            <span className="text-xs px-2.5 py-0.5 rounded-full bg-rose-600 text-white font-bold">
              {t('berSimEngine')}
            </span>
          </div>
          <p className="text-sm text-neutral-500 dark:text-neutral-400 mt-1 max-w-3xl">
            {isTr
              ? 'Gerçek envanter grafiğinden bir bileşen seçin; üç arıza senaryosu (host kesintisi, datastore erişim kaybı, ağ kopması) gerçek deterministik BerSim motoru üzerinden hesaplanır. Üretim ortamına hiçbir çağrı yapılmaz.'
              : 'Pick a component from the real inventory graph; three failure scenarios (host outage, datastore loss, network cut) are computed by the real deterministic BerSim engine. Zero calls are made to production.'}
          </p>
        </div>
        <div className="flex items-center gap-2">
          <Button
            variant="outline"
            size="sm"
            className="gap-2"
            onClick={() => void inventoryProbe.refetch()}
            disabled={inventoryProbe.isFetching}
          >
            <RefreshCw className={`w-4 h-4 ${inventoryProbe.isFetching ? 'animate-spin' : ''}`} />
            {isTr ? 'Envanteri Yenile' : 'Refresh Inventory'}
          </Button>
        </div>
      </div>

      {error && (
        <div role="alert" className="p-3 rounded-lg border border-rose-200 dark:border-rose-900 bg-rose-50 dark:bg-rose-950/30 text-sm text-rose-700 dark:text-rose-300 flex items-center gap-2">
          <AlertTriangle className="w-4 h-4" />
          {error}
        </div>
      )}

      {inventoryProbe.isLoading ? (
        <Card className="p-8 text-center text-sm text-neutral-500">
          {isTr ? 'Envanter yükleniyor...' : 'Loading inventory...'}
        </Card>
      ) : error ? null : inventoryTotal === 0 ? (
        <Card className="p-10 text-center">
          <PlugZap className="w-10 h-10 mx-auto text-neutral-300 dark:text-neutral-600" />
          <h2 className="mt-3 font-bold text-neutral-900 dark:text-white">
            {isTr ? 'Analiz edilecek varlık yok' : 'No entities to analyze'}
          </h2>
          <p className="mt-1 text-sm text-neutral-500 dark:text-neutral-400 max-w-xl mx-auto">
            {isTr
              ? 'Etki analizi yalnızca gerçek keşif verisi üzerinde çalışır. Integrations sayfasından Active Directory veya vCenter keşfi başlatın; keşif ve uzlaştırma tamamlandığında bileşenler buraya otomatik gelir.'
              : 'Impact analysis only runs on real discovery data. Start an Active Directory or vCenter discovery from the Integrations page; components appear here automatically once discovery and reconciliation complete.'}
          </p>
          <ButtonLink to="/integrations" variant="primary" size="sm" className="mt-4 gap-2 bg-brand-primary">
            <PlugZap className="w-4 h-4" />
            {isTr ? 'Keşif başlat' : 'Start discovery'}
          </ButtonLink>
        </Card>
      ) : (
        <div className="grid grid-cols-1 lg:grid-cols-3 gap-6">
          {/* Target selection */}
          <Card className="p-5">
            <div className="flex items-center gap-2 text-xs font-bold uppercase tracking-wider text-neutral-500">
              <Search className="w-4 h-4" aria-hidden="true" />
              {isTr ? '1. Hedef Bileşen' : '1. Target Component'}
            </div>
            <div className="mt-3">
              <NodePicker
                selectedId={targetId || null}
                onSelect={selectTarget}
                label={isTr ? 'Hedef bileşen' : 'Target component'}
                listClassName="max-h-96"
              />
            </div>
          </Card>

          {/* Run panel */}
          <Card className="p-5 flex flex-col">
            <div className="text-xs font-bold uppercase tracking-wider text-neutral-500 mb-3">
              {isTr ? '2. Senaryo Kümesi (üçü birlikte çalışır)' : '2. Scenario Set (all three run together)'}
            </div>
            <ul className="space-y-2 text-xs text-neutral-600 dark:text-neutral-300">
              <li className="p-2.5 rounded-lg border border-neutral-200 dark:border-dark-border">
                <strong>host_failure</strong> — {isTr ? 'Host kapanması ve VM failover etkisi' : 'Host shutdown and VM failover blast radius'}
              </li>
              <li className="p-2.5 rounded-lg border border-neutral-200 dark:border-dark-border">
                <strong>datastore_outage</strong> — {isTr ? 'Depolama erişim kaybı' : 'Storage path disconnect and volume lock'}
              </li>
              <li className="p-2.5 rounded-lg border border-neutral-200 dark:border-dark-border">
                <strong>network_cut</strong> — {isTr ? 'Core switch veya uplink kesintisi' : 'Core switch or uplink loss partition'}
              </li>
            </ul>

            <div className="mt-4 p-3 rounded-lg bg-blue-50 dark:bg-blue-950/30 border border-blue-200 dark:border-blue-900 text-xs text-blue-800 dark:text-blue-300 flex items-start gap-2">
              <Info className="w-4 h-4 mt-0.5 shrink-0 text-blue-600" />
              {isTr
                ? 'Motor deterministiktir: aynı hedef ve senaryo her zaman aynı sonucu üretir. Sonuçlar üretim üzerinde asla çalıştırılmaz.'
                : 'The engine is deterministic: the same target and scenario always produce the same result. Results are never executed against production.'}
            </div>

            <div className="mt-auto pt-4">
              <Button
                variant="primary"
                size="sm"
                className="w-full gap-2 bg-brand-primary"
                onClick={() => void runAllScenarios()}
                disabled={!targetId || running}
              >
                {running && <Loader2 className="w-4 h-4 animate-spin" />}
                {running
                  ? isTr
                    ? 'Hesaplanıyor...'
                    : 'Computing...'
                  : isTr
                    ? 'Etki Analizini Çalıştır'
                    : 'Run Impact Analysis'}
              </Button>
              {target && (
                <p className="mt-2 text-[11px] text-neutral-500 text-center">
                  {isTr ? 'Hedef:' : 'Target:'} <span className="font-mono">{target.name}</span>
                </p>
              )}
            </div>
          </Card>

          {/* Results */}
          <Card className="p-5 flex flex-col">
            <div className="text-xs font-bold uppercase tracking-wider text-neutral-500 mb-3">
              {isTr ? '3. Sonuç Raporu' : '3. Result Report'}
            </div>

            {!running && Object.values(results).every((result) => !result) && Object.values(errors).every((item) => !item) && (
              <p className="text-xs text-neutral-500 py-6 text-center">
                {isTr
                  ? 'Bir hedef seçin ve “Etki Analizini Çalıştır” düğmesine basın.'
                  : 'Select a target and press “Run Impact Analysis”.'}
              </p>
            )}

            {impactSummary.entries.length > 0 && (
              <div className="mb-4 grid grid-cols-3 gap-2 text-center text-xs">
                <div className="p-2.5 rounded-lg bg-rose-50 dark:bg-rose-950/30 border border-rose-200 dark:border-rose-900 text-rose-700 dark:text-rose-400">
                  <div className="text-xl font-bold">{formatNumber(impactSummary.confirmed)}</div>
                  <div>{isTr ? 'Doğrulanan' : 'Confirmed'}</div>
                </div>
                <div className="p-2.5 rounded-lg bg-amber-50 dark:bg-amber-950/30 border border-amber-200 dark:border-amber-900 text-amber-700 dark:text-amber-400">
                  <div className="text-xl font-bold">{formatNumber(impactSummary.probable)}</div>
                  <div>{isTr ? 'Olası' : 'Probable'}</div>
                </div>
                <div className="p-2.5 rounded-lg bg-neutral-50 dark:bg-dark-bg border border-neutral-200 dark:border-dark-border text-neutral-600 dark:text-neutral-400">
                  <div className="text-xl font-bold">{formatNumber(impactSummary.possible)}</div>
                  <div>{isTr ? 'Mümkün' : 'Possible'}</div>
                </div>
                {impactSummary.worstRisk >= 0 && (
                  <div className="col-span-3 p-2.5 rounded-lg border border-neutral-200 dark:border-dark-border flex items-center justify-between text-[11px]">
                    <span>{isTr ? 'En yüksek risk skoru' : 'Highest risk score'}</span>
                    <span className="font-bold text-rose-600 dark:text-rose-400">
                      {impactSummary.worstRisk}/100
                    </span>
                  </div>
                )}
              </div>
            )}

            <div className="space-y-4">
              {SCENARIO_TYPES.map((scenarioType) => {
                const result = results[scenarioType];
                const scenarioError = errors[scenarioType];
                return (
                  <div key={scenarioType} className="border border-neutral-200 dark:border-dark-border rounded-lg p-3 space-y-2">
                    <div className="flex items-center gap-2 text-[11px] font-bold uppercase text-neutral-500">
                      <span lang="en" className="font-mono">{scenarioType}</span>
                      {result && (
                        <span className="ml-auto text-neutral-800 dark:text-neutral-200">
                          {isTr ? 'Risk' : 'Risk'}: <strong>{result.riskScore}/100</strong>
                        </span>
                      )}
                    </div>
                    {scenarioError && (
                      <p className="text-[11px] text-rose-600 dark:text-rose-400 flex items-center gap-1.5">
                        <AlertTriangle className="w-3.5 h-3.5" />
                        {scenarioError}
                      </p>
                    )}
                    {result && (
                      <>
                        <ImpactGroup
                          title={isTr ? 'Doğrulanmış etkiler' : 'Confirmed impacts'}
                          entities={result.confirmedImpacts}
                          tone="confirmed"
                        />
                        <ImpactGroup
                          title={isTr ? 'Olası etkiler' : 'Probable impacts'}
                          entities={result.probableImpacts}
                          tone="probable"
                        />
                        <ImpactGroup
                          title={isTr ? 'Mümkün etkiler' : 'Possible impacts'}
                          entities={result.possibleImpacts}
                          tone="possible"
                        />
                        {(result.recommendations.length > 0 || result.clusterHeadroom) && (
                          <div className="text-[11px] space-y-1 pt-1 border-t border-neutral-100 dark:border-dark-border">
                            {result.clusterHeadroom && (
                              <p className="text-neutral-600 dark:text-neutral-400">
                                {isTr ? 'Kalan kapasite:' : 'Remaining capacity:'}{' '}
                                CPU {formatNumber(result.clusterHeadroom.remainingCpuPercent)}% · RAM{' '}
                                {formatNumber(result.clusterHeadroom.remainingRamPercent)}%
                                {result.clusterHeadroom.hasCapacityForFailover
                                  ? isTr
                                    ? ' · failover için yeterli'
                                    : ' · sufficient for failover'
                                  : isTr
                                    ? ' · failover için yetersiz'
                                    : ' · insufficient for failover'}
                              </p>
                            )}
                            {result.recommendations.slice(0, 3).map((recommendation, index) => (
                              <p key={index} className="text-neutral-500">
                                • {recommendation}
                              </p>
                            ))}
                          </div>
                        )}
                      </>
                    )}
                    {!result && !scenarioError && running && (
                      <p className="text-[11px] text-neutral-500 flex items-center gap-1.5">
                        <Loader2 className="w-3 h-3 animate-spin" />
                        {isTr ? 'Hesaplanıyor...' : 'Computing...'}
                      </p>
                    )}
                  </div>
                );
              })}
            </div>
          </Card>
        </div>
      )}
    </div>
  );
};

function ImpactGroup({
  title,
  entities,
  tone,
}: {
  title: string;
  entities: ImpactedEntity[];
  tone: 'confirmed' | 'probable' | 'possible';
}) {
  const locale = useLocale();
  const isTr = locale === 'tr';
  if (entities.length === 0) {
    return (
      <p className="text-[11px] text-neutral-400">
        {title}: {isTr ? 'yok' : 'none'}
      </p>
    );
  }

  const Icon = tone === 'confirmed' ? ShieldAlert : ShieldCheck;
  const toneClass =
    tone === 'confirmed'
      ? 'text-rose-600 dark:text-rose-400'
      : tone === 'probable'
        ? 'text-amber-600 dark:text-amber-400'
        : 'text-neutral-500 dark:text-neutral-400';

  return (
    <div>
      <div className={`text-[10px] uppercase font-bold flex items-center gap-1 ${toneClass}`}>
        <Icon className="w-3 h-3" />
        {title} ({entities.length})
      </div>
      <div className="mt-1 space-y-1">
        {entities.slice(0, 8).map((entity) => (
          <div key={`${entity.id}-${entity.impactLevel}`} className="text-[11px] text-neutral-700 dark:text-neutral-300 flex items-center gap-1.5">
            <span className="font-medium truncate">{entity.name}</span>
            <span className="text-[9px] px-1 py-0.5 rounded bg-neutral-100 dark:bg-dark-border font-mono text-neutral-500 shrink-0">
              {entity.type}
            </span>
          </div>
        ))}
        {entities.length > 8 && (
          <p className="text-[10px] text-neutral-500">
            +{entities.length - 8} {isTr ? 'daha' : 'more'}
          </p>
        )}
      </div>
    </div>
  );
}
