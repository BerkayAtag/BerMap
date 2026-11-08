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
  test('collector kimliği düz HTTP\'de de kopyalanır; kopyalanamazsa tam kimlik seçili gösterilir', async ({
    page,
    problems,
  }) => {
    const seeded = await findIntegration('vCenter · IST-DC1');
    expect(seeded).toBeDefined();
    const integrationId = seeded!.id;

    // Plain HTTP (the default install): no secure context, no navigator.clipboard.
    // execCommand('copy') is recorded and its result is controlled by the test.
    await page.addInitScript(() => {
      Object.defineProperty(window, 'isSecureContext', { configurable: true, get: () => false });
      Object.defineProperty(Navigator.prototype, 'clipboard', { configurable: true, get: () => undefined });
      const state = window as unknown as { __copied: string[]; __copyWorks: boolean };
      state.__copied = [];
      state.__copyWorks = true;
      document.execCommand = (command: string) => {
        if (command !== 'copy' || !state.__copyWorks) return false;
        const active = document.activeElement as HTMLTextAreaElement | null;
        state.__copied.push(active?.value?.slice(active.selectionStart ?? 0, active.selectionEnd ?? undefined) ?? '');
        return true;
      };
    });
    const copied = () => page.evaluate(() => (window as unknown as { __copied: string[] }).__copied);
    const setCopyWorks = (works: boolean) =>
      page.evaluate((value) => {
        (window as unknown as { __copyWorks: boolean }).__copyWorks = value;
      }, works);

    await page.goto('/integrations');
    expect(await page.evaluate(() => typeof navigator.clipboard)).toBe('undefined');
    const vcenter = card(page, 'vCenter · IST-DC1');
    const chip = vcenter.getByRole('button', { name: /Collector yapılandırması için entegrasyon kimliğini kopyala/ });
    await expect(chip).toContainText(integrationId.slice(0, 8));

    // The fallback copies the full id and says so.
    await chip.click();
    await expect(vcenter.getByRole('status').filter({ hasText: 'Kopyalandı' })).toBeVisible();
    expect(await copied()).toEqual([integrationId]);

    // When the browser refuses, the full id appears selected for Ctrl+C.
    await setCopyWorks(false);
    await chip.click();
    const fullId = vcenter.getByRole('textbox', { name: 'Entegrasyon kimliği' });
    await expect(fullId).toHaveValue(integrationId);
    await expect(fullId).toBeFocused();
    expect(await fullId.evaluate((input: HTMLInputElement) => [input.selectionStart, input.selectionEnd])).toEqual([
      0,
      integrationId.length,
    ]);
    await expect(vcenter).toContainText('Tarayıcı panoya kopyalamaya izin vermedi');
    await page.keyboard.press('Escape');
    await expect(fullId).toBeHidden();

    // The edit wizard always shows the full id, with its own copy button.
    await vcenter.getByRole('button', { name: 'Entegrasyonu düzenle' }).click();
    const editor = page.getByRole('dialog', { name: 'Entegrasyonu Düzenle' });
    const idField = editor.getByRole('textbox', { name: 'Entegrasyon Kimliği' });
    await expect(idField).toHaveValue(integrationId);
    await expect(idField).toHaveAttribute('readonly', '');
    await editor.getByRole('button', { name: 'Kopyala', exact: true }).click();
    await expect(editor).toContainText('Tarayıcı panoya kopyalamaya izin vermedi');
    await expect(idField).toBeFocused();
    await setCopyWorks(true);
    await editor.getByRole('button', { name: 'Kopyala', exact: true }).click();
    await expect(editor.getByRole('status').filter({ hasText: 'Kopyalandı.' })).toBeVisible();
    expect(await copied()).toEqual([integrationId, integrationId]);
    await editor.getByRole('button', { name: 'Kapat' }).click();
    await expect(editor).toBeHidden();
    problems.expectNone();
  });

  test('Active Directory sihirbazında LDAPS, StartTLS ve düz LDAP portu birlikte değişir', async ({ page, problems }) => {
    await page.goto('/integrations');
    await page.getByRole('button', { name: 'Yeni Entegrasyon Ekle' }).click();
    const wizard = page.getByRole('dialog', { name: 'Yeni Altyapı Entegrasyonu Ekle' });
    await wizard.getByRole('button', { name: /Active Directory/ }).click();
    await wizard.getByRole('button', { name: 'İlerle' }).click();

    const port = wizard.getByLabel('Port', { exact: true });
    const ldaps = wizard.getByRole('checkbox', { name: 'LDAPS (TLS) kullan' });
    const startTls = wizard.getByRole('checkbox', { name: 'StartTLS kullan' });
    await expect(port).toHaveValue('636');
    await expect(ldaps).toBeChecked();

    await startTls.check();
    await expect(ldaps).not.toBeChecked();
    await expect(port).toHaveValue('389');
    await ldaps.check();
    await expect(startTls).not.toBeChecked();
    await expect(port).toHaveValue('636');
    // Plaintext LDAP (neither) uses 389 as well.
    await ldaps.uncheck();
    await expect(port).toHaveValue('389');
    await ldaps.check();
    await expect(port).toHaveValue('636');

    // A port the user typed (Global Catalog over TLS) is kept.
    await port.fill('3269');
    await startTls.check();
    await expect(port).toHaveValue('3269');
    await ldaps.check();
    await expect(port).toHaveValue('3269');

    // StartTLS on the implicit-TLS port is flagged.
    await startTls.check();
    await port.fill('636');
    await expect(wizard).toContainText('636 yalnızca LDAPS (örtük TLS) portudur');
    await port.fill('389');
    await expect(wizard).not.toContainText('636 yalnızca LDAPS');

    // The verification step shows the StartTLS endpoint on 389.
    await wizard.getByLabel(/Sunucu Adresi/).fill('dc01.corp.bermap.demo');
    await wizard.getByRole('button', { name: 'İlerle' }).click();
    await wizard.getByLabel(/Kullanıcı Adı \/ Servis Hesabı/).fill('svc-e2e@corp.bermap.demo');
    await wizard.getByLabel(/Parola \/ Secret/).fill('e2e-not-a-real-secret');
    await wizard.getByRole('button', { name: 'İlerle' }).click();
    await expect(wizard).toContainText('dc01.corp.bermap.demo:389');
    await expect(wizard).toContainText('StartTLS / sıkı doğrulama');
    await wizard.getByRole('button', { name: 'Kapat' }).click();
    await expect(wizard).toBeHidden();
    problems.expectNone();
  });

  test('düzenlemede sunucu değişirse parola yeniden istenir', async ({ page, problems }) => {
    const name = uniqueName('E2E uç nokta');
    const created = await apiRequest<IntegrationDto>('POST', '/api/integrations', {
      token: adminToken(),
      body: {
        name,
        description: '',
        adapterType: 'VMware',
        configuration: { server: 'vc-a.corp.bermap.demo', port: 443, username: 'svc-e2e@vsphere.local' },
        secret: 'e2e-not-a-real-secret',
        scheduleCron: '0 */6 * * *',
        timeoutSeconds: 1800,
        isEnabled: false,
      },
    });
    try {
      await page.goto('/integrations');
      await card(page, name).getByRole('button', { name: 'Entegrasyonu düzenle' }).click();
      const editor = page.getByRole('dialog', { name: 'Entegrasyonu Düzenle' });
      await editor.getByRole('button', { name: 'İlerle' }).click();
      // Unchanged endpoint: the secret stays optional.
      await expect(editor.getByText('Parola / Secret — opsiyonel')).toBeVisible();
      await editor.getByRole('button', { name: 'Geri' }).click();

      await editor.getByLabel(/Sunucu Adresi/).fill('vc-b.corp.bermap.demo');
      await editor.getByRole('button', { name: 'İlerle' }).click();
      await expect(editor.getByText('Parola / Secret — zorunlu')).toBeVisible();
      await expect(editor.getByLabel(/Parola \/ Secret/)).toHaveAttribute('aria-required', 'true');
      await editor.getByRole('button', { name: 'İlerle' }).click();
      await expect(editor).toContainText('kayıtlı parola yeni uç noktaya gönderilmez');
      await expect(editor.getByLabel(/Parola \/ Secret/)).toBeVisible();

      await editor.getByLabel(/Parola \/ Secret/).fill('e2e-new-secret');
      await editor.getByRole('button', { name: 'İlerle' }).click();
      await editor.getByRole('button', { name: 'Değişiklikleri Kaydet' }).click();
      await expect(editor).toBeHidden();
      expect((await getIntegration(created.id)).configuration).toMatchObject({ server: 'vc-b.corp.bermap.demo' });
      problems.expectNone();
    } finally {
      await apiRequest('DELETE', `/api/integrations/${created.id}`, { token: adminToken() });
    }
  });

  test('sunucu SECRET_REQUIRED_FOR_ENDPOINT_CHANGE dönerse sihirbaz parola alanında açıklar', async ({
    page,
    problems,
  }) => {
    // Collector-managed with a stored secret: the console cannot know a secret is
    // stored, so the server's refusal is what asks for it.
    const name = uniqueName('E2E collector parola');
    const created = await apiRequest<IntegrationDto>('POST', '/api/integrations', {
      token: adminToken(),
      body: {
        name,
        description: '',
        adapterType: 'VMware',
        configuration: { server: 'vc-c.corp.bermap.demo', port: 443, username: 'svc-e2e@vsphere.local' },
        secret: 'e2e-not-a-real-secret',
        scheduleCron: '0 */6 * * *',
        timeoutSeconds: 1800,
        isEnabled: true,
        collectorManaged: true,
      },
    });
    try {
      await page.goto('/integrations');
      await card(page, name).getByRole('button', { name: 'Entegrasyonu düzenle' }).click();
      const editor = page.getByRole('dialog', { name: 'Entegrasyonu Düzenle' });
      await editor.getByLabel('Port', { exact: true }).fill('8443');
      await editor.getByRole('button', { name: 'İlerle' }).click();
      await editor.getByRole('button', { name: 'İlerle' }).click();
      const refused = page.waitForResponse((r) => r.request().method() === 'PUT' && r.url().includes(created.id));
      await editor.getByRole('button', { name: 'Değişiklikleri Kaydet' }).click();
      expect((await refused).status()).toBe(400);

      await expect(editor).toContainText(
        'Sunucu, port, kullanıcı adı veya TLS ayarı değişti: kayıtlı parola yeni uç noktaya gönderilmez.',
      );
      await expect(editor.getByText('Parola / Secret — zorunlu')).toBeVisible();
      await editor.getByLabel(/Parola \/ Secret/).fill('e2e-new-secret');
      await editor.getByRole('button', { name: 'İlerle' }).click();
      await editor.getByRole('button', { name: 'Değişiklikleri Kaydet' }).click();
      await expect(editor).toBeHidden();
      expect((await getIntegration(created.id)).configuration).toMatchObject({ port: 8443 });
      problems.expectNone([/PUT \/api\/integrations\/[0-9a-f-]+ -> 400/, /status of 400/]);
    } finally {
      await apiRequest('DELETE', `/api/integrations/${created.id}`, { token: adminToken() });
    }
  });
});
