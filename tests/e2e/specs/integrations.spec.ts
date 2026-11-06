import type { Locator, Page } from '@playwright/test';
import { apiRequest, uniqueName, type IntegrationDto } from '../support/api';
import { test, expect } from '../support/fixtures';
import { adminToken } from '../support/state';

/** The integration card: the closest ancestor of its heading that holds the card actions. */
function card(page: Page, name: string): Locator {
  return page
    .getByRole('heading', { level: 3, name, exact: true })
    .locator('xpath=ancestor::*[.//button[@title="Entegrasyonu sil"]][1]');
}

async function getIntegration(id: string): Promise<IntegrationDto> {
  return apiRequest<IntegrationDto>('GET', `/api/integrations/${id}`, { token: adminToken() });
}

async function findIntegration(name: string): Promise<IntegrationDto | undefined> {
  const list = await apiRequest<IntegrationDto[]>('GET', '/api/integrations', { token: adminToken() });
  return list.find((i) => i.name === name);
}

test.describe('Entegrasyonlar', () => {
  test('VMware entegrasyonu sihirbazla oluşturulur, düzenlenir, devre dışı bırakılır ve silinir', async ({
    page,
    problems,
  }) => {
    const name = uniqueName('E2E vCenter');
    const renamed = `${name} (düzenlendi)`;

    await page.goto('/integrations');
    await page.getByRole('button', { name: 'Yeni Entegrasyon Ekle' }).click();
    const wizard = page.getByRole('dialog', { name: 'Yeni Altyapı Entegrasyonu Ekle' });

    // 1. System type
    await wizard.getByRole('button', { name: /VMware vCenter Server/ }).click();
    await expect(wizard.getByRole('button', { name: /VMware vCenter Server/ })).toHaveAttribute('aria-pressed', 'true');
    await wizard.getByRole('button', { name: 'İlerle' }).click();

    // 2. Connection: the name has a default; a missing server address is reported on the field.
    await wizard.getByRole('button', { name: 'İlerle' }).click();
    await expect(wizard).toContainText('Sunucu adresi zorunludur.');
    await expect(wizard.getByLabel('Entegrasyon Adı')).not.toHaveValue('');
    await wizard.getByLabel('Entegrasyon Adı').fill(name);
    await wizard.getByLabel('Açıklama (Opsiyonel)').fill('E2E testi; gerçek bir vCenter değildir');
    await wizard.getByLabel(/Sunucu Adresi/).fill('vcenter-e2e.corp.bermap.demo');
    await expect(wizard.getByLabel('Port', { exact: true })).toHaveValue('443');
    await wizard.getByRole('button', { name: 'İlerle' }).click();

    // 3. Credentials
    await wizard.getByLabel(/Kullanıcı Adı \/ Servis Hesabı/).fill('svc-e2e@vsphere.local');
    await wizard.getByLabel(/Parola \/ Secret/).fill('e2e-not-a-real-secret');
    await wizard.getByRole('button', { name: 'İlerle' }).click();

    // 4. Verification: save without contacting the fake vCenter.
    await expect(wizard).toContainText(name);
    await expect(wizard).toContainText('vcenter-e2e.corp.bermap.demo');
    await wizard.getByRole('checkbox', { name: 'Kaydettikten sonra tam keşfi hemen başlat' }).uncheck();
    await wizard.getByRole('button', { name: 'Kaydet', exact: true }).click();
    await expect(wizard).toBeHidden();

    const integrationCard = card(page, name);
    await expect(integrationCard).toContainText('vcenter-e2e.corp.bermap.demo:443');
    const created = await findIntegration(name);
    expect(created).toBeDefined();
    expect(created!.adapterType).toBe('VMware');
    expect(created!.collectorManaged).toBe(false);
    // The wizard documents the tuning defaults it does not show.
    expect(created!.configuration).toMatchObject({ server: 'vcenter-e2e.corp.bermap.demo', port: 443, maxConcurrency: 8, retryCount: 3 });

    // An administrator tunes a key through the API; editing in the console must keep it.
    await apiRequest('PUT', `/api/integrations/${created!.id}`, {
      token: adminToken(),
      body: { configuration: { ...created!.configuration, maxConcurrency: 3, customTuning: 'korunmalı' } },
    });
    await page.reload();
    await card(page, name).getByRole('button', { name: 'Entegrasyonu düzenle' }).click();
    const editor = page.getByRole('dialog', { name: 'Entegrasyonu Düzenle' });
    await expect(editor.getByLabel('Entegrasyon Adı')).toHaveValue(name);
    await editor.getByLabel('Entegrasyon Adı').fill(renamed);
    await editor.getByRole('button', { name: 'İlerle' }).click();
    await expect(editor.getByLabel(/Parola \/ Secret/)).toHaveValue('');
    await editor.getByRole('button', { name: 'İlerle' }).click();
    await editor.getByRole('button', { name: 'Değişiklikleri Kaydet' }).click();
    await expect(editor).toBeHidden();
    await expect(page.getByRole('heading', { level: 3, name: renamed })).toBeVisible();

    const edited = await getIntegration(created!.id);
    expect(edited.name).toBe(renamed);
    expect(edited.configuration).toMatchObject({
      server: 'vcenter-e2e.corp.bermap.demo',
      maxConcurrency: 3,
      retryCount: 3,
      customTuning: 'korunmalı',
    });

    // Toggle off and on again.
    const editedCard = card(page, renamed);
    await editedCard.getByRole('button', { name: 'Entegrasyonu devre dışı bırak' }).click();
    await expect(page.getByText(`${renamed} devre dışı bırakıldı.`)).toBeVisible();
    await expect(editedCard.getByRole('button', { name: 'Entegrasyonu etkinleştir' })).toBeVisible();
    expect((await getIntegration(created!.id)).isEnabled).toBe(false);
    await editedCard.getByRole('button', { name: 'Entegrasyonu etkinleştir' }).click();
    await expect(page.getByText(`${renamed} etkinleştirildi.`)).toBeVisible();
    expect((await getIntegration(created!.id)).isEnabled).toBe(true);

    // Delete asks for confirmation first; dismissing keeps the integration.
    page.once('dialog', (dialog) => void dialog.dismiss());
    await editedCard.getByRole('button', { name: 'Entegrasyonu sil' }).click();
    await expect(page.getByRole('heading', { level: 3, name: renamed })).toBeVisible();
    page.once('dialog', (dialog) => {
      expect(dialog.message()).toContain(`${renamed} entegrasyonu silinsin mi?`);
      void dialog.accept();
    });
    await editedCard.getByRole('button', { name: 'Entegrasyonu sil' }).click();
    await expect(page.getByText(`${renamed} silindi.`)).toBeVisible();
    await expect(page.getByRole('heading', { level: 3, name: renamed })).toHaveCount(0);
    expect(await findIntegration(renamed)).toBeUndefined();

    problems.expectNone();
  });

  test('collector yönetimli entegrasyonlarda test ve keşif sunucuda yapılmaz', async ({ page }) => {
    await page.goto('/integrations');
    const vcenter = card(page, 'vCenter · IST-DC1');
    await expect(vcenter).toContainText('Collector yönetimli');
    await expect(vcenter).toContainText('Test ve keşif collector tarafında yapılır');
    await expect(vcenter.getByRole('button', { name: 'Test Et' })).toBeDisabled();
    await expect(vcenter.getByRole('button', { name: 'Keşfi Başlat' })).toBeDisabled();
    await expect(vcenter).toContainText('Aktif');
  });
});
