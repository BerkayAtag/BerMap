import { apiRequest, type IntegrationDto } from '../support/api';
import { enrollmentToken } from '../support/env';
import { test, expect, captureDownload, parseCsv, expectFormulaSafe } from '../support/fixtures';
import { nodeByName } from '../support/graph';
import { DOMAIN } from '../support/seed';
import { adminToken } from '../support/state';

const SEARCH = /FQDN, IP, MAC, BIOS UUID veya domain SID ara/;

/** Text of a discovery-sourced value a spreadsheet would run as a formula. */
const PROBE = {
  integration: 'E2E · CSV formül denemesi',
  name: '=HYPERLINK("https://example.invalid","e2e-csv-probe")',
  fqdn: `+e2e-csv-probe.${DOMAIN}`,
  os: '@SUM(1+1)',
};

/**
 * Feeds one node with formula-like values through the collector protocol into
 * its own collector-managed integration (created once, reused on reruns).
 */
async function ensureFormulaProbe(): Promise<void> {
  const token = adminToken();
  const list = await apiRequest<IntegrationDto[]>('GET', '/api/integrations', { token });
  let integration = list.find((i) => i.name === PROBE.integration);
  integration ??= await apiRequest<IntegrationDto>('POST', '/api/integrations', {
    token,
    body: {
      name: PROBE.integration,
      description: 'CSV dışa aktarımının formül enjeksiyonuna karşı korunduğunu doğrular',
      adapterType: 'VMware',
      configuration: { server: `vcenter-probe.${DOMAIN}`, port: 443, username: 'probe' },
      isEnabled: true,
      collectorManaged: true,
    },
  });
  await apiRequest('POST', '/api/collectors/submit-batch?final=true', {
    headers: { 'X-BerMap-Enrollment-Token': enrollmentToken() },
    body: {
      success: true,
      status: 'Completed',
      integrationId: integration.id,
      sourceVersion: 'e2e',
      nodes: [
        {
          externalId: 'vm-e2e-csv-probe',
          nodeType: 'Application',
          name: PROBE.name,
          fqdn: PROBE.fqdn,
          ipAddress: '10.99.0.1',
          status: 'healthy',
          sourceSystem: 'VMware',
          properties: { GuestOS: PROBE.os, PowerState: 'POWERED_OFF' },
        },
      ],
      relationships: [],
    },
  });
}

test.describe('Envanter', () => {
  test('arama sonuçları süzer ve adres çubuğuna yazılır', async ({ page, problems }) => {
    await page.goto('/inventory');
    await expect(page.getByRole('cell', { name: /^app-core-01 / })).toBeVisible();

    await page.getByRole('textbox', { name: SEARCH }).fill('sql-');
    await expect(page).toHaveURL(/[?&]q=sql-/);
    for (const name of ['sql-core-01', 'sql-payments-01', 'sql-dwh-01']) {
      await expect(page.getByRole('cell', { name: new RegExp(`^${name} `) })).toBeVisible();
    }
    await expect(page.getByRole('cell', { name: /^app-core-01 / })).toHaveCount(0);
    await expect(page.getByText(/^3 kayıt$/)).toBeVisible();

    await page.getByRole('button', { name: 'Aramayı temizle' }).click();
    await expect(page).not.toHaveURL(/[?&]q=/);
    await expect(page.getByRole('cell', { name: /^app-core-01 / })).toBeVisible();

    await page.getByRole('combobox', { name: 'Varlık türü' }).selectOption({ label: 'ESXi Hypervisor' });
    for (let i = 1; i <= 6; i++) {
      await expect(page.getByRole('cell', { name: new RegExp(`^esx0${i}\\.corp\\.bermap\\.demo`) })).toBeVisible();
    }
    // Only hypervisors remain (other tests may add their own hosts).
    const types = await page.getByRole('table').getByRole('row').locator('td:nth-child(2)').allInnerTexts();
    expect(new Set(types)).toEqual(new Set(['Hypervisor']));
    problems.expectNone();
  });

  test('?q= derin bağlantısı aramayı doldurur', async ({ page }) => {
    await page.goto('/inventory?q=payments');
    await expect(page.getByRole('textbox', { name: SEARCH })).toHaveValue('payments');
    await expect(page.getByRole('cell', { name: /^app-payments-01 / })).toBeVisible();
    await expect(page.getByRole('cell', { name: /^sql-payments-01 / })).toBeVisible();
    await expect(page.getByRole('cell', { name: /^sql-core-01 / })).toHaveCount(0);
  });

  test('?node= derin bağlantısı varlık detayını açar; simülasyon bağlantısı hedefi taşır', async ({ page, problems }) => {
    const node = await nodeByName('sql-core-01', 'VirtualMachine');
    await page.goto(`/inventory?node=${node.id}`);
    const detail = page.getByRole('dialog', { name: 'Varlık detayı' });
    await expect(detail.getByRole('heading', { level: 2, name: 'sql-core-01' })).toBeVisible();
    await expect(detail).toContainText(`sql-core-01.${DOMAIN}`);
    await expect(detail).toContainText('VirtualMachine');

    await page.keyboard.press('Escape');
    await expect(detail).toBeHidden();
    await expect(page).not.toHaveURL(/node=/);

    // Opening a row puts the node back into the URL.
    await page.getByRole('textbox', { name: SEARCH }).fill('sql-core');
    await page.getByRole('cell', { name: /^sql-core-01 / }).click();
    await expect(page).toHaveURL(new RegExp(`node=${node.id}`));
    await detail.getByRole('link', { name: 'Simülasyon' }).click();
    await expect(page).toHaveURL(new RegExp(`/simulation\\?target=${node.id}`));
    await expect(page.getByRole('heading', { level: 1, name: 'Simülasyon ve Etki Analizi' })).toBeVisible();
    problems.expectNone();
  });

  test('bilinmeyen ?node= kimliği "bulunamadı" paneli gösterir', async ({ page }) => {
    await page.goto('/inventory?node=00000000-0000-0000-0000-000000000000');
    const detail = page.getByRole('dialog', { name: 'Varlık detayı' });
    await expect(detail.getByRole('heading', { level: 2, name: 'Varlık bulunamadı' })).toBeVisible();
    await expect(detail).toContainText('Bu bağlantıdaki varlık envanterde yok.');
    await detail.getByRole('button', { name: 'Detayı kapat' }).click();
    await expect(page).not.toHaveURL(/node=/);
  });

  test('sayfa CSV dışa aktarımı UTF-8 BOM ile ve formül enjeksiyonuna karşı korumalı iner', async ({ page }) => {
    await ensureFormulaProbe();
    await page.goto('/inventory?q=e2e-csv-probe');
    await expect(page.getByText(/^1 kayıt$/)).toBeVisible();

    const file = await captureDownload(page, () => page.getByRole('button', { name: 'Sayfayı CSV Dışa Aktar' }).click());
    expect(file.filename).toMatch(/^bermap-inventory-\d{4}-\d{2}-\d{2}\.csv$/);
    expect(file.text.startsWith('﻿')).toBe(true);
    const rows = parseCsv(file.text);
    expect(rows[0].slice(0, 3)).toEqual(['Name', 'FQDN', 'Node Type']);
    expect(rows).toHaveLength(2);
    const [name, fqdn, , os] = rows[1];
    expect(name).toBe(`'${PROBE.name}`);
    expect(fqdn).toBe(`'${PROBE.fqdn}`);
    expect(os).toBe(`'${PROBE.os}`);
    expectFormulaSafe(rows);

    // The whole page of demo data is formula-safe too.
    await page.goto('/inventory');
    await expect(page.getByRole('cell', { name: /^app-core-01 / })).toBeVisible();
    const all = await captureDownload(page, () => page.getByRole('button', { name: 'Sayfayı CSV Dışa Aktar' }).click());
    const allRows = parseCsv(all.text);
    // Header plus one full page of rows.
    expect(allRows.length).toBeGreaterThan(20);
    expectFormulaSafe(allRows);
  });
});
