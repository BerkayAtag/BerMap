import { apiRequest, type SnapshotDto } from '../support/api';
import { test, expect, captureDownload, parseCsv, expectFormulaSafe } from '../support/fixtures';
import { BASELINE_SNAPSHOT } from '../support/seed';
import { adminToken } from '../support/state';

test.describe('Denetim Günlükleri', () => {
  test('işleme göre süzülür, satır ayrıntısı açılır ve süzülmüş CSV iner', async ({ page, problems }) => {
    await page.goto('/audit');
    await expect(page.getByText(/Toplam Kayıt: \d+/)).toBeVisible();

    await page.getByRole('combobox', { name: 'İşlem' }).fill('Auth.Login');
    await page.getByRole('button', { name: 'Uygula' }).click();
    const rows = page.getByRole('table').getByRole('row').filter({ has: page.getByRole('cell') });
    await expect(rows.first()).toContainText('Auth.Login');
    const actions = await rows.locator('td:nth-child(3)').allInnerTexts();
    expect(actions.length).toBeGreaterThan(0);
    expect(new Set(actions)).toEqual(new Set(['Auth.Login']));

    const first = rows.first().getByRole('button').first();
    await first.click();
    await expect(first).toHaveAttribute('aria-expanded', 'true');

    const file = await captureDownload(page, () => page.getByRole('button', { name: 'CSV Dışa Aktar' }).click());
    expect(file.filename).toMatch(/^bermap-audit-[\d-]+\.csv$/);
    const csv = parseCsv(file.text);
    expect(csv[0]).toEqual(['createdAt', 'username', 'userId', 'action', 'entityType', 'entityId', 'success', 'ipAddress', 'details']);
    expect(csv.length).toBeGreaterThan(1);
    expect(new Set(csv.slice(1).map((row) => row[3]))).toEqual(new Set(['Auth.Login']));
    expectFormulaSafe(csv);

    await page.getByRole('button', { name: 'Filtreleri Temizle' }).click();
    await expect(page.getByRole('combobox', { name: 'İşlem' })).toHaveValue('');
    problems.expectNone();
  });
});

test.describe('Raporlar', () => {
  test('envanter, denetim ve snapshot dışa aktarımlarının hepsi iner', async ({ page, problems }) => {
    const snapshots = await apiRequest<Array<SnapshotDto & { nodeCount: number; relationshipCount: number }>>(
      'GET',
      '/api/snapshots',
      { token: adminToken() },
    );
    const baseline = snapshots.find((s) => s.name === BASELINE_SNAPSHOT)!;

    await page.goto('/reports');

    const inventory = await captureDownload(page, () => page.getByRole('button', { name: 'Envanter CSV' }).click());
    expect(inventory.filename).toMatch(/^bermap-inventory-\d{4}-\d{2}-\d{2}\.csv$/);
    expect(inventory.text.startsWith('﻿')).toBe(true);
    const inventoryRows = parseCsv(inventory.text);
    expect(inventoryRows[0].slice(0, 4)).toEqual(['id', 'name', 'nodeType', 'fqdn']);
    const names = inventoryRows.slice(1).map((row) => row[1]);
    // Every page, not only the first 25 rows the Inventory screen shows.
    expect(names.length).toBeGreaterThanOrEqual(37);
    for (const name of ['app-core-01', 'sql-dwh-01', 'esx06.corp.bermap.demo', 'IST-DC1']) expect(names).toContain(name);
    expectFormulaSafe(inventoryRows);

    const audit = await captureDownload(page, () => page.getByRole('button', { name: 'Denetim CSV' }).click());
    expect(audit.filename).toMatch(/^bermap-audit-.*\.csv$/);
    const auditRows = parseCsv(audit.text);
    expect(auditRows[0][0]).toBe('createdAt');
    expect(auditRows.map((row) => row[3])).toContain('Snapshot.Created');
    expectFormulaSafe(auditRows);

    const item = page.getByRole('listitem').filter({ hasText: BASELINE_SNAPSHOT });
    const nodes = await captureDownload(page, () => item.getByRole('button', { name: 'Düğümler' }).click());
    expect(nodes.filename).toMatch(/\.csv$/);
    const nodeRows = parseCsv(nodes.text);
    expect(nodeRows.length - 1).toBe(baseline.nodeCount);
    expectFormulaSafe(nodeRows);

    const relationships = await captureDownload(page, () => item.getByRole('button', { name: 'İlişkiler' }).click());
    expect(relationships.filename).toMatch(/\.csv$/);
    expect(relationships.filename).not.toBe(nodes.filename);
    const relationshipRows = parseCsv(relationships.text);
    expect(relationshipRows.length - 1).toBe(baseline.relationshipCount);
    expectFormulaSafe(relationshipRows);

    await expect(item.getByRole('link', { name: 'Detay' })).toHaveAttribute('href', `/snapshots?id=${baseline.id}`);
    await expect(page.getByRole('link', { name: 'Filtreli dışa aktarım' })).toHaveAttribute('href', '/audit');
    problems.expectNone();
  });
});
