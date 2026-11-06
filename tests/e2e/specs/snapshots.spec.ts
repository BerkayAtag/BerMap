import { apiRequest, uniqueName, type IntegrationDto, type SnapshotDto } from '../support/api';
import { enrollmentToken } from '../support/env';
import { test, expect } from '../support/fixtures';
import { BASELINE_SNAPSHOT, DOMAIN } from '../support/seed';
import { adminToken } from '../support/state';

const DIFF_INTEGRATION = 'E2E · snapshot farkı';

async function snapshotByName(name: string): Promise<SnapshotDto> {
  const list = await apiRequest<SnapshotDto[]>('GET', '/api/snapshots', { token: adminToken() });
  const snapshot = list.find((s) => s.name === name);
  expect(snapshot, `snapshot ${name}`).toBeDefined();
  return snapshot!;
}

/**
 * Reports a new node through a private collector-managed integration, so two
 * snapshots taken around it differ by a node only this test adds. (A changed
 * value would not do: re-discovery keeps the first name and property values.)
 */
async function addDiffNode(name: string): Promise<void> {
  const token = adminToken();
  const list = await apiRequest<IntegrationDto[]>('GET', '/api/integrations', { token });
  let integration = list.find((i) => i.name === DIFF_INTEGRATION);
  integration ??= await apiRequest<IntegrationDto>('POST', '/api/integrations', {
    token,
    body: {
      name: DIFF_INTEGRATION,
      description: 'Snapshot karşılaştırma testinin değiştirdiği düğüm',
      adapterType: 'VMware',
      configuration: { server: `vcenter-diff.${DOMAIN}`, port: 443, username: 'diff' },
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
          externalId: `app-${name}`,
          nodeType: 'Application',
          name,
          fqdn: `${name}.${DOMAIN}`,
          ipAddress: '10.99.0.2',
          status: 'healthy',
          sourceSystem: 'VMware',
          properties: { Purpose: 'snapshot karşılaştırması' },
        },
      ],
      relationships: [],
    },
  });
}

test.describe('Snapshot', () => {
  test('?id= derin bağlantısı snapshot detayını açar ve bütünlük doğrulanır', async ({ page, problems }) => {
    const baseline = await snapshotByName(BASELINE_SNAPSHOT);
    await page.goto(`/snapshots?id=${baseline.id}`);
    const detail = page.getByRole('dialog', { name: 'Snapshot detayı' });
    await expect(detail).toContainText(BASELINE_SNAPSHOT);
    await expect(detail).toContainText(/Snapshot Düğümleri \(\d+\)/);
    await expect(detail).toContainText('esx01.corp.bermap.demo');
    await expect(detail).toContainText(/Snapshot İlişkileri \(\d+\)/);
    await detail.getByRole('button', { name: 'Detayı kapat' }).click();
    await expect(detail).toBeHidden();
    await expect(page).not.toHaveURL(/id=/);

    const card = page
      .getByRole('heading', { level: 3, name: new RegExp(`^${BASELINE_SNAPSHOT}`) })
      .first()
      .locator('xpath=ancestor::*[.//button[normalize-space()="Doğrula"]][1]');
    await card.getByRole('button', { name: 'Doğrula' }).click();
    const verification = page.getByRole('dialog', { name: 'Snapshot doğrulama sonucu' });
    await expect(verification).toContainText('Graf bütünlüğü doğrulandı');
    await verification.getByRole('button', { name: 'Doğrulama sonucunu kapat' }).click();
    await expect(verification).toBeHidden();
    problems.expectNone();
  });

  test('bilinmeyen ?id= için bulunamadı mesajı gösterilir', async ({ page }) => {
    await page.goto('/snapshots?id=00000000-0000-0000-0000-000000000000');
    await expect(page.getByText('Bu snapshot bulunamadı. Bağlantı eski veya hatalı olabilir.')).toBeVisible();
  });

  test('snapshot oluşturulur ve iki snapshot karşılaştırılır', async ({ page, problems }) => {
    const before = uniqueName('E2E Önce');
    const after = uniqueName('E2E Sonra');
    const added = uniqueName('e2e-diff');

    await page.goto('/snapshots');
    for (const name of [before, after]) {
      if (name === after) await addDiffNode(added);
      await page.getByRole('button', { name: 'Yeni Snapshot Al' }).click();
      const create = page.getByRole('dialog', { name: 'Yeni snapshot' });
      // The browser itself refuses an empty name (required field).
      await expect(create.getByLabel('Snapshot Adı / Etiketi')).toHaveAttribute('required', '');
      await create.getByLabel('Snapshot Adı / Etiketi').fill(name);
      await create.getByLabel('Açıklama (Opsiyonel)').fill('E2E karşılaştırma');
      await create.getByRole('button', { name: 'Snapshot Oluştur' }).click();
      await expect(create).toBeHidden();
      await expect(page.getByText(new RegExp(`${name} snapshot'ı v\\d+ olarak mühürlendi`))).toBeVisible();
      await expect(page.getByRole('heading', { level: 3, name: new RegExp(`^${name}`) })).toBeVisible();
    }

    const a = await snapshotByName(before);
    const b = await snapshotByName(after);
    await page.getByRole('button', { name: 'Snapshot Karşılaştır' }).click();
    const compare = page.getByRole('dialog', { name: 'Snapshot karşılaştırması' });
    await compare.getByLabel(/Kaynak A/).selectOption(a.id);
    await compare.getByLabel(/Hedef B/).selectOption(b.id);
    await compare.getByRole('button', { name: 'Karşılaştır', exact: true }).click();
    await expect(compare).toContainText('Yeni Düğüm');
    await expect(compare).toContainText('Değişen Düğüm');
    // The node reported between the two snapshots is listed as added.
    await expect(compare).toContainText(/Eklenen Düğümler \(\d+\)/);
    await expect(compare).toContainText(added);
    await compare.getByRole('button', { name: 'Karşılaştırmayı kapat' }).click();
    await expect(compare).toBeHidden();
    problems.expectNone();
  });
});
