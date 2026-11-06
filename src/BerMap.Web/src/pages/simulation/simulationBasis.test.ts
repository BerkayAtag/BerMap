import { test } from 'node:test';
import assert from 'node:assert/strict';
import {
  LIVE_BASIS,
  basisFromResult,
  basisFromSelection,
  basisLabel,
  buildSimulationRequest,
  classifySimulationError,
  formatBasisDate,
  parseSnapshotParam,
  reportBasisRows,
  snapshotOptionLabel,
} from './simulationBasis.ts';

const snapshots = [
  { id: 'aaaaaaaa-1111-2222-3333-444444444444', name: 'Bakım öncesi', graphCapturedAt: '2026-10-01T08:30:00Z' },
  { id: 'bbbbbbbb-1111-2222-3333-444444444444', name: 'Q3 baseline', graphCapturedAt: '2026-09-30T18:00:00Z' },
];

test('parseSnapshotParam treats blank values as the live graph', () => {
  assert.equal(parseSnapshotParam(null), null);
  assert.equal(parseSnapshotParam(undefined), null);
  assert.equal(parseSnapshotParam('   '), null);
  assert.equal(parseSnapshotParam(' abc '), 'abc');
});

test('basisFromSelection resolves a listed snapshot and keeps an unknown id', () => {
  assert.deepEqual(basisFromSelection(null, snapshots), LIVE_BASIS);
  assert.deepEqual(basisFromSelection(snapshots[0].id, snapshots), {
    kind: 'snapshot',
    id: snapshots[0].id,
    name: 'Bakım öncesi',
    createdAt: '2026-10-01T08:30:00Z',
  });
  assert.deepEqual(basisFromSelection('missing', undefined), {
    kind: 'snapshot',
    id: 'missing',
    name: '',
    createdAt: null,
  });
});

test('basisFromResult reads the engine result; no snapshotId means live', () => {
  assert.deepEqual(basisFromResult({}), LIVE_BASIS);
  assert.deepEqual(basisFromResult({ snapshotId: null, snapshotName: null, snapshotCreatedAt: null }), LIVE_BASIS);
  assert.deepEqual(
    basisFromResult({ snapshotId: 'id-1', snapshotName: 'S1', snapshotCreatedAt: '2026-10-01T08:30:00Z' }),
    { kind: 'snapshot', id: 'id-1', name: 'S1', createdAt: '2026-10-01T08:30:00Z' },
  );
});

test('formatBasisDate returns an empty string for missing or invalid dates', () => {
  assert.equal(formatBasisDate(null, true), '');
  assert.equal(formatBasisDate('not a date', false), '');
  assert.match(formatBasisDate('2026-10-01T08:30:00Z', true), /2026/);
  assert.match(formatBasisDate('2026-10-01T08:30:00Z', false), /2026/);
});

test('basisLabel names the live graph or the snapshot with its date', () => {
  assert.equal(basisLabel(LIVE_BASIS, true), 'Canlı graf');
  assert.equal(basisLabel(LIVE_BASIS, false), 'Live graph');
  const label = basisLabel(basisFromSelection(snapshots[0].id, snapshots), true);
  assert.match(label, /^Snapshot: Bakım öncesi \(.*2026.*\)$/);
  // Without a name the short id stands in.
  assert.equal(basisLabel(basisFromSelection('abcdef0123456789', []), false), 'Snapshot: abcdef01');
});

test('snapshotOptionLabel shows name and capture date', () => {
  assert.match(snapshotOptionLabel(snapshots[1], false), /^Q3 baseline — .*2026/);
  assert.equal(snapshotOptionLabel({ ...snapshots[1], graphCapturedAt: '' }, false), 'Q3 baseline');
});

test('reportBasisRows states the basis, and for a snapshot its name, date and id', () => {
  assert.deepEqual(reportBasisRows(LIVE_BASIS, true), [['Simülasyon tabanı', 'Canlı graf (güncel envanter)']]);
  assert.deepEqual(reportBasisRows(LIVE_BASIS, false), [['Simulation basis', 'Live graph (current inventory)']]);

  const rows = reportBasisRows(basisFromSelection(snapshots[0].id, snapshots), true);
  assert.equal(rows.length, 3);
  assert.deepEqual(rows[0], ['Simülasyon tabanı', 'Snapshot — Bakım öncesi']);
  assert.equal(rows[1][0], 'Snapshot tarihi');
  assert.match(rows[1][1], /2026/);
  assert.deepEqual(rows[2], ['Snapshot kimliği', snapshots[0].id]);

  // Unknown date: the date row is left out rather than printed empty.
  const undated = reportBasisRows({ kind: 'snapshot', id: 'x', name: 'S', createdAt: null }, false);
  assert.deepEqual(undated, [
    ['Simulation basis', 'Snapshot — S'],
    ['Snapshot id', 'x'],
  ]);
});

test('buildSimulationRequest sends snapshotId only for a snapshot basis', () => {
  assert.deepEqual(buildSimulationRequest('t1', 'host_failure', null), {
    targetNodeId: 't1',
    scenarioType: 'host_failure',
  });
  assert.equal('snapshotId' in buildSimulationRequest('t1', 'host_failure', null), false);
  assert.deepEqual(buildSimulationRequest('t1', 'host_failure', 's1'), {
    targetNodeId: 't1',
    scenarioType: 'host_failure',
    snapshotId: 's1',
  });
});

test('classifySimulationError separates snapshot 404s from a missing target', () => {
  assert.equal(classifySimulationError({ status: 400, code: 'invalid_simulation' }), 'invalid_simulation');
  assert.equal(classifySimulationError({ status: 404, code: 'snapshot_not_found' }), 'snapshot_not_found');
  assert.equal(classifySimulationError({ status: 404, code: 'not_in_snapshot' }), 'not_in_snapshot');
  assert.equal(classifySimulationError({ status: 404, code: 'not_found' }), 'target_not_found');
  assert.equal(classifySimulationError({ status: 500, code: 'internal_error' }), 'other');
  assert.equal(classifySimulationError(new Error('network')), 'other');
  assert.equal(classifySimulationError(null), 'other');
});
