import { apiRequest, uniqueName, type CollectorDto } from '../support/api';
import { enrollmentToken } from '../support/env';
import { test, expect } from '../support/fixtures';
import { COLLECTOR_NAME, DOMAIN, ensureCollector } from '../support/seed';
import { adminToken } from '../support/state';

test.describe('Collectorlar', () => {
  test('kayıtlı collector heartbeat ve metrikleriyle listelenir', async ({ page, problems }) => {
    await page.goto('/collectors');
    const heading = page.getByRole('heading', { level: 3, name: COLLECTOR_NAME });
    const card = heading.locator('xpath=ancestor::*[.//button[normalize-space()="Kaldır"]][1]');
    await expect(card).toContainText(`collector01.${DOMAIN}`);
    await expect(card).toContainText('10.20.1.15');
    await expect(card).toContainText('ActiveDirectory');
    await expect(card).toContainText('VMware');

    await page.getByRole('button', { name: 'Kayıt Bilgisi' }).click();
    const guide = page.getByRole('heading', { name: 'Collector Enrollment ve Heartbeat Sözleşmesi' });
    await expect(guide).toBeVisible();
    await page.getByRole('button', { name: 'Kurulum bilgisini kapat' }).click();
    await expect(guide).toBeHidden();
    problems.expectNone();
  });

  test('collector onay sonrası kaldırılır', async ({ page, problems }) => {
    const name = uniqueName('e2e-collector');
    await ensureCollector(enrollmentToken(), name, `${name}.${DOMAIN}`);

    await page.goto('/collectors');
    const card = page
      .getByRole('heading', { level: 3, name })
      .locator('xpath=ancestor::*[.//button[normalize-space()="Kaldır"]][1]');
    await card.getByRole('button', { name: 'Kaldır' }).click();

    const confirm = page.getByRole('dialog', { name: 'Collector kaldırılsın mı?' });
    await expect(confirm).toContainText(`${name}.${DOMAIN}`);
    await confirm.getByRole('button', { name: 'Vazgeç' }).click();
    await expect(confirm).toBeHidden();
    await expect(page.getByRole('heading', { level: 3, name })).toBeVisible();

    await card.getByRole('button', { name: 'Kaldır' }).click();
    await confirm.getByRole('button', { name: 'Kaldır' }).click();
    await expect(page.getByText(`${name} kaldırıldı.`)).toBeVisible();
    await expect(page.getByRole('heading', { level: 3, name })).toHaveCount(0);

    const collectors = await apiRequest<CollectorDto[]>('GET', '/api/collectors', { token: adminToken() });
    expect(collectors.map((c) => c.name)).not.toContain(name);
    expect(collectors.map((c) => c.name)).toContain(COLLECTOR_NAME);
    problems.expectNone();
  });
});
