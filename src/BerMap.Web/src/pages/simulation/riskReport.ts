import type { ImpactAnalysisResult, ImpactedEntity } from '@/types';
import {
  localizeRecommendations,
  parseDependencyPath,
  riskLevel,
  riskLevelLabel,
  scenarioLabel,
  type ScenarioType,
} from './scenarios';
import { basisFromResult, reportBasisRows } from './simulationBasis';

/*
 * "Değişiklik risk raporu" — a self-contained change-risk report built from one
 * simulation run. It is rendered entirely client-side (no libraries, no network):
 * a print-optimised HTML document for window.print(), and a Markdown file that can
 * be attached to a change request.
 */

export interface RiskReportInput {
  result: ImpactAnalysisResult;
  scenario: ScenarioType;
  target: { id: string; name: string; nodeType: string; fqdn?: string | null };
  generatedAt: Date;
  generatedBy: string;
  isTr: boolean;
}

interface ReportImpactGroup {
  title: string;
  entities: { name: string; type: string; path: string }[];
}

interface ReportModel {
  title: string;
  isTr: boolean;
  meta: [string, string][];
  riskScore: number;
  riskText: string;
  headroom: [string, string][];
  groups: ReportImpactGroup[];
  recommendations: string[];
  footer: string;
}

function formatPercentValue(value: number, isTr: boolean): string {
  const text = value.toLocaleString(isTr ? 'tr-TR' : 'en-US', { maximumFractionDigits: 1 });
  return `${text}%`;
}

function pathText(entity: ImpactedEntity, isTr: boolean): string {
  return parseDependencyPath(entity.dependencyPath, entity.name)
    .map((segment) =>
      segment.kind === 'edge' ? `[${segment.text === 'DOWN' && isTr ? 'KESİNTİ' : segment.text}]` : segment.text,
    )
    .join(' → ');
}

function buildModel(input: RiskReportInput): ReportModel {
  const { result, scenario, target, generatedAt, generatedBy, isTr } = input;
  const level = riskLevel(result.riskScore);
  const headroom = result.clusterHeadroom;
  const code = headroom.bottleneckCode;
  const n1 = headroom.isPostFailure ? ' (N-1)' : '';
  const date = generatedAt.toLocaleString(isTr ? 'tr-TR' : 'en-US', { dateStyle: 'long', timeStyle: 'short' });

  const headroomRows: [string, string][] = [];
  if (code === 'NotApplicable') {
    headroomRows.push([
      isTr ? 'Cluster kapasitesi' : 'Cluster headroom',
      isTr ? 'Bu senaryo için uygulanamaz' : 'Not applicable to this scenario',
    ]);
  } else if (code === 'Unknown') {
    headroomRows.push([
      isTr ? 'Cluster kapasitesi' : 'Cluster headroom',
      isTr ? 'Kapasite metrikleri toplanmamış' : 'Capacity metrics not collected',
    ]);
  } else {
    if (headroom.clusterName) headroomRows.push(['Cluster', headroom.clusterName]);
    headroomRows.push(
      [(isTr ? 'Kalan CPU' : 'Remaining CPU') + n1, formatPercentValue(headroom.remainingCpuPercent, isTr)],
      [(isTr ? 'Kalan RAM' : 'Remaining RAM') + n1, formatPercentValue(headroom.remainingRamPercent, isTr)],
      [
        isTr ? 'Failover kapasitesi' : 'Failover capacity',
        headroom.hasCapacityForFailover ? (isTr ? 'Yeterli' : 'Sufficient') : (isTr ? 'Yetersiz' : 'Insufficient'),
      ],
    );
    if (code === 'Cpu' || code === 'Ram') {
      headroomRows.push([isTr ? 'Darboğaz' : 'Bottleneck', code === 'Cpu' ? 'CPU' : 'RAM']);
    }
  }

  const group = (title: string, entities: ImpactedEntity[]): ReportImpactGroup => ({
    title: `${title} (${entities.length})`,
    entities: entities.map((entity) => ({ name: entity.name, type: entity.type, path: pathText(entity, isTr) })),
  });

  return {
    title: isTr ? 'Değişiklik Risk Raporu' : 'Change Risk Report',
    isTr,
    meta: [
      [isTr ? 'Tarih' : 'Date', date],
      [isTr ? 'Hazırlayan' : 'Prepared by', generatedBy],
      [isTr ? 'Hedef' : 'Target', result.targetNodeName || target.name],
      [isTr ? 'Hedef türü' : 'Target type', target.nodeType],
      ...(target.fqdn ? ([['FQDN', target.fqdn]] as [string, string][]) : []),
      [isTr ? 'Hedef kimliği' : 'Target id', target.id],
      [isTr ? 'Senaryo' : 'Scenario', `${scenarioLabel(scenario, isTr)} (${scenario})`],
      // The basis the engine actually ran on (live graph or a snapshot's name and date).
      ...reportBasisRows(basisFromResult(result), isTr),
    ],
    riskScore: result.riskScore,
    riskText: `${riskLevelLabel(level, isTr)} — ${result.riskScore}/100`,
    headroom: headroomRows,
    groups: [
      group(isTr ? 'Doğrulanmış etkiler' : 'Confirmed impacts', result.confirmedImpacts),
      group(isTr ? 'Olası etkiler' : 'Probable impacts', result.probableImpacts),
      group(isTr ? 'Mümkün etkiler' : 'Possible impacts', result.possibleImpacts),
    ],
    recommendations: localizeRecommendations(result, isTr),
    footer: isTr
      ? 'BerMap BerSim motoru tarafından deterministik olarak hesaplanmıştır. Üretim sistemlerine hiçbir çağrı yapılmamıştır.'
      : 'Computed deterministically by the BerMap BerSim engine. No calls were made to production systems.',
  };
}

/* ---------- Markdown ---------- */

function mdEscape(value: string): string {
  return value.replace(/([\\`*_[\]|<>#])/g, '\\$1').replace(/\r?\n/g, ' ');
}

export function buildRiskReportMarkdown(input: RiskReportInput): string {
  const model = buildModel(input);
  const isTr = model.isTr;
  const lines: string[] = [`# ${model.title}`, ''];
  for (const [label, value] of model.meta) lines.push(`- **${mdEscape(label)}:** ${mdEscape(value)}`);
  lines.push('', `## ${isTr ? 'Risk skoru' : 'Risk score'}`, '', `**${mdEscape(model.riskText)}**`, '');
  lines.push(`## ${isTr ? 'Cluster kapasitesi' : 'Cluster headroom'}`, '');
  for (const [label, value] of model.headroom) lines.push(`- **${mdEscape(label)}:** ${mdEscape(value)}`);
  for (const group of model.groups) {
    lines.push('', `## ${mdEscape(group.title)}`, '');
    if (group.entities.length === 0) {
      lines.push(isTr ? '_Yok._' : '_None._');
      continue;
    }
    lines.push(
      isTr ? '| Bileşen | Tür | Etki yolu |' : '| Component | Type | Impact path |',
      '| --- | --- | --- |',
    );
    for (const entity of group.entities) {
      lines.push(`| ${mdEscape(entity.name)} | ${mdEscape(entity.type)} | ${mdEscape(entity.path)} |`);
    }
  }
  lines.push('', `## ${isTr ? 'Öneriler' : 'Recommendations'}`, '');
  if (model.recommendations.length === 0) lines.push(isTr ? '_Öneri yok._' : '_No recommendations._');
  for (const recommendation of model.recommendations) lines.push(`- ${mdEscape(recommendation)}`);
  lines.push('', '---', '', `_${mdEscape(model.footer)}_`, '');
  return lines.join('\n');
}

/* ---------- Print view ---------- */

function htmlEscape(value: string): string {
  return value
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;')
    .replace(/'/g, '&#39;');
}

const PRINT_STYLES = `
  *{box-sizing:border-box}
  body{font-family:-apple-system,"Segoe UI",Roboto,"Helvetica Neue",Arial,sans-serif;color:#171717;background:#fff;margin:0;padding:32px;font-size:12px;line-height:1.5}
  h1{font-size:22px;margin:0 0 4px}
  h2{font-size:14px;margin:24px 0 8px;padding-bottom:4px;border-bottom:1px solid #d4d4d4;break-after:avoid}
  .brand{font-size:11px;font-weight:700;letter-spacing:.08em;text-transform:uppercase;color:#2563eb}
  dl{display:grid;grid-template-columns:max-content 1fr;gap:4px 16px;margin:12px 0 0}
  dt{color:#525252;font-weight:600}
  dd{margin:0;word-break:break-word}
  .risk{display:inline-block;padding:6px 14px;border-radius:999px;font-weight:700;font-size:14px;border:1px solid}
  .risk.high{background:#fff1f2;color:#be123c;border-color:#fecdd3}
  .risk.medium{background:#fffbeb;color:#b45309;border-color:#fde68a}
  .risk.low{background:#ecfdf5;color:#047857;border-color:#a7f3d0}
  table{width:100%;border-collapse:collapse;margin-top:4px}
  th,td{text-align:left;padding:5px 8px;border-bottom:1px solid #e5e5e5;vertical-align:top}
  th{background:#f5f5f5;font-weight:600}
  tr{break-inside:avoid}
  td.path{font-family:ui-monospace,Menlo,Consolas,monospace;font-size:11px;word-break:break-word}
  .none{color:#737373;font-style:italic}
  ul{margin:4px 0 0;padding-left:18px}
  footer{margin-top:32px;padding-top:8px;border-top:1px solid #d4d4d4;color:#737373;font-size:10px}
  .toolbar{margin-bottom:16px}
  .toolbar button{font:inherit;padding:6px 12px;border-radius:6px;border:1px solid #d4d4d4;background:#fff;cursor:pointer}
  @media print{body{padding:0}.toolbar{display:none}@page{margin:16mm}}
`;

export function buildRiskReportHtml(input: RiskReportInput): string {
  const model = buildModel(input);
  const isTr = model.isTr;
  const level = riskLevel(model.riskScore);
  const rows = (pairs: [string, string][]) =>
    `<dl>${pairs.map(([label, value]) => `<dt>${htmlEscape(label)}</dt><dd>${htmlEscape(value)}</dd>`).join('')}</dl>`;
  const groups = model.groups
    .map((group) => {
      const body =
        group.entities.length === 0
          ? `<p class="none">${isTr ? 'Yok.' : 'None.'}</p>`
          : `<table><thead><tr><th>${isTr ? 'Bileşen' : 'Component'}</th><th>${isTr ? 'Tür' : 'Type'}</th><th>${
              isTr ? 'Etki yolu' : 'Impact path'
            }</th></tr></thead><tbody>${group.entities
              .map(
                (entity) =>
                  `<tr><td>${htmlEscape(entity.name)}</td><td lang="en">${htmlEscape(entity.type)}</td><td class="path">${htmlEscape(
                    entity.path,
                  )}</td></tr>`,
              )
              .join('')}</tbody></table>`;
      return `<h2>${htmlEscape(group.title)}</h2>${body}`;
    })
    .join('');
  const recommendations =
    model.recommendations.length === 0
      ? `<p class="none">${isTr ? 'Öneri yok.' : 'No recommendations.'}</p>`
      : `<ul>${model.recommendations.map((item) => `<li>${htmlEscape(item)}</li>`).join('')}</ul>`;

  return `<!doctype html>
<html lang="${isTr ? 'tr' : 'en'}">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>${htmlEscape(`${model.title} — ${input.result.targetNodeName || input.target.name}`)}</title>
<style>${PRINT_STYLES}</style>
</head>
<body>
<div class="toolbar"><button type="button" onclick="window.print()">${isTr ? 'Yazdır' : 'Print'}</button></div>
<div class="brand">BerMap · BerSim</div>
<h1>${htmlEscape(model.title)}</h1>
${rows(model.meta)}
<h2>${isTr ? 'Risk skoru' : 'Risk score'}</h2>
<span class="risk ${level}">${htmlEscape(model.riskText)}</span>
<h2>${isTr ? 'Cluster kapasitesi' : 'Cluster headroom'}</h2>
${rows(model.headroom)}
${groups}
<h2>${isTr ? 'Öneriler' : 'Recommendations'}</h2>
${recommendations}
<footer>${htmlEscape(model.footer)}</footer>
</body>
</html>`;
}

export function riskReportFileName(input: RiskReportInput, extension: string): string {
  const slug =
    (input.result.targetNodeName || input.target.name)
      .toLowerCase()
      .replace(/[^a-z0-9]+/g, '-')
      .replace(/^-+|-+$/g, '')
      .slice(0, 48) || 'target';
  // Local time, matching the date printed inside the report.
  const d = input.generatedAt;
  const pad = (n: number) => String(n).padStart(2, '0');
  const stamp = `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}-${pad(d.getHours())}-${pad(d.getMinutes())}`;
  return `bermap-risk-report-${slug}-${input.scenario}-${stamp}.${extension}`;
}

/**
 * Opens the print view in a new window and starts printing. Returns false when the
 * browser blocked the window, so the caller can fall back to a download.
 */
export function openRiskReportPrintView(input: RiskReportInput): boolean {
  const view = window.open('', '_blank');
  if (!view) return false;
  view.document.open();
  view.document.write(buildRiskReportHtml(input));
  view.document.close();
  view.focus();
  // Give the new document a frame to lay out before the print dialog snapshots it.
  view.setTimeout(() => view.print(), 150);
  return true;
}
