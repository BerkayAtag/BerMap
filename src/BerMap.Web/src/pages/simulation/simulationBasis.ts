import type { ApiError, ImpactAnalysisResult, SimulationRunRequest, SnapshotSummary } from '@/types';

/*
 * "Simülasyon tabanı" — which graph a simulation runs on: the live graph
 * (current inventory) or the frozen graph of one snapshot. Pure helpers shared
 * by the Studio page and the risk report; no React, no network.
 */

export type SimulationBasis =
  | { kind: 'live' }
  | { kind: 'snapshot'; id: string; name: string; createdAt: string | null };

export const LIVE_BASIS: SimulationBasis = { kind: 'live' };

type SnapshotChoice = Pick<SnapshotSummary, 'id' | 'name' | 'graphCapturedAt'>;

/** The ?snapshot= deep-link value; blank means the live graph. */
export function parseSnapshotParam(value: string | null | undefined): string | null {
  const trimmed = value?.trim();
  return trimmed ? trimmed : null;
}

/**
 * The basis picked in the Studio. An id that is not in the loaded list (still
 * loading, or the snapshot no longer exists) keeps its id with an empty name.
 */
export function basisFromSelection(
  snapshotId: string | null,
  snapshots: readonly SnapshotChoice[] | undefined,
): SimulationBasis {
  if (!snapshotId) return LIVE_BASIS;
  const snapshot = snapshots?.find((item) => item.id === snapshotId);
  return {
    kind: 'snapshot',
    id: snapshotId,
    name: snapshot?.name ?? '',
    createdAt: snapshot?.graphCapturedAt ?? null,
  };
}

/** The basis a finished run actually used, as reported by the engine. */
export function basisFromResult(
  result: Pick<ImpactAnalysisResult, 'snapshotId' | 'snapshotName' | 'snapshotCreatedAt'>,
): SimulationBasis {
  if (!result.snapshotId) return LIVE_BASIS;
  return {
    kind: 'snapshot',
    id: result.snapshotId,
    name: result.snapshotName ?? '',
    createdAt: result.snapshotCreatedAt ?? null,
  };
}

/** Long local date and time, as printed in the risk report; '' when unknown. */
export function formatBasisDate(value: string | null | undefined, isTr: boolean): string {
  if (!value) return '';
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return '';
  return date.toLocaleString(isTr ? 'tr-TR' : 'en-US', { dateStyle: 'long', timeStyle: 'short' });
}

function snapshotName(basis: Extract<SimulationBasis, { kind: 'snapshot' }>): string {
  return basis.name || basis.id.slice(0, 8);
}

/** Short label for the review card, the result header and the selector. */
export function basisLabel(basis: SimulationBasis, isTr: boolean): string {
  if (basis.kind === 'live') return isTr ? 'Canlı graf' : 'Live graph';
  const date = formatBasisDate(basis.createdAt, isTr);
  return `Snapshot: ${snapshotName(basis)}${date ? ` (${date})` : ''}`;
}

/** One selector option per snapshot: name and capture date. */
export function snapshotOptionLabel(snapshot: SnapshotChoice, isTr: boolean): string {
  const date = formatBasisDate(snapshot.graphCapturedAt, isTr);
  return date ? `${snapshot.name} — ${date}` : snapshot.name;
}

/** Metadata rows the risk report prints for the basis. */
export function reportBasisRows(basis: SimulationBasis, isTr: boolean): [string, string][] {
  const label = isTr ? 'Simülasyon tabanı' : 'Simulation basis';
  if (basis.kind === 'live') {
    return [[label, isTr ? 'Canlı graf (güncel envanter)' : 'Live graph (current inventory)']];
  }
  const rows: [string, string][] = [[label, `Snapshot — ${snapshotName(basis)}`]];
  const date = formatBasisDate(basis.createdAt, isTr);
  if (date) rows.push([isTr ? 'Snapshot tarihi' : 'Snapshot date', date]);
  rows.push([isTr ? 'Snapshot kimliği' : 'Snapshot id', basis.id]);
  return rows;
}

/** POST /api/simulations/run body; snapshotId is sent only for a snapshot basis. */
export function buildSimulationRequest(
  targetNodeId: string,
  scenarioType: string,
  snapshotId: string | null,
): SimulationRunRequest {
  return snapshotId ? { targetNodeId, scenarioType, snapshotId } : { targetNodeId, scenarioType };
}

export type SimulationErrorKind =
  | 'invalid_simulation'
  | 'snapshot_not_found'
  | 'not_in_snapshot'
  | 'target_not_found'
  | 'other';

/** Maps a failed run to the message the Studio shows. */
export function classifySimulationError(error: unknown): SimulationErrorKind {
  const apiError = (error ?? {}) as Partial<ApiError>;
  if (apiError.code === 'invalid_simulation') return 'invalid_simulation';
  if (apiError.code === 'snapshot_not_found') return 'snapshot_not_found';
  if (apiError.code === 'not_in_snapshot') return 'not_in_snapshot';
  if (apiError.status === 404) return 'target_not_found';
  return 'other';
}
