import { test, expect, useRole } from '../support/fixtures';
import { ROLE_USERS } from '../support/state';

/*
 * The console hides or disables what the API would refuse for the role.
 * Every role still loads its pages without a failed /api request.
 */

const ALL_NAV = [
  'Dashboard',
  'Altyapı Haritası',
  'Envanter',
  'Simülasyon',
  'Snapshot',
  'Kapasite İstihbaratı',
  'Raporlar',
  'Entegrasyonlar',
  'Collectorlar',
];

test.describe('Salt-Okunur Görüntüleyici', () => {
  useRole('viewer');

  test('menüde denetim ve kullanıcılar yok, yönetim düğmeleri devre dışı', async ({ page, problems }) => {
    await page.goto('/');
    await expect(page.getByRole('button', { name: `Kullanıcı menüsü: ${ROLE_USERS.viewer.displayName}` })).toBeVisible();
    const nav = page.getByRole('navigation', { name: 'Ana gezinme' });
    for (const name of ALL_NAV) await expect(nav.getByRole('link', { name, exact: true })).toBeVisible();
    await expect(nav.getByRole('link', { name: 'Denetim Günlükleri' })).toHaveCount(0);
    await expect(nav.getByRole('link', { name: 'Kullanıcılar' })).toHaveCount(0);

    await page.goto('/integrations');
    await expect(page.getByRole('heading', { level: 3, name: 'vCenter · IST-DC1' })).toBeVisible();
    await expect(page.getByRole('button', { name: 'Yeni Entegrasyon Ekle' })).toBeDisabled();
    await expect(page.getByRole('button', { name: 'Entegrasyonu sil' }).first()).toBeDisabled();
    await expect(page.getByRole('button', { name: 'Entegrasyonu düzenle' }).first()).toBeDisabled();

    await page.goto('/snapshots');
    await expect(page.getByRole('button', { name: 'Yeni Snapshot Al' })).toBeDisabled();

    await page.goto('/collectors');
    await expect(page.getByRole('heading', { level: 3, name: 'collector-ist-01' })).toBeVisible();
    await expect(page.getByRole('button', { name: 'Kaldır' }).first()).toBeDisabled();

    await page.goto('/reports');
    await expect(page.getByRole('button', { name: 'Envanter CSV' })).toBeEnabled();
    await expect(page.getByRole('button', { name: 'Denetim CSV' })).toHaveCount(0);

    await page.waitForLoadState('networkidle');
    problems.expectNone();
  });

  test('denetim ve kullanıcı sayfaları erişimi reddeder', async ({ page, problems }) => {
    await page.goto('/audit');
    await expect(page.getByRole('heading', { name: 'Denetim günlüklerini görme yetkiniz yok' })).toBeVisible();
    await page.goto('/users');
    await expect(page.getByRole('heading', { name: 'Sayfa Bulunamadı' })).toBeVisible();
    problems.expectNone();
  });
});

test.describe('Altyapı Operatörü', () => {
  useRole('operator');

  test('snapshot alabilir, entegrasyon ve collector yönetemez, denetimi göremez', async ({ page, problems }) => {
    await page.goto('/');
    const nav = page.getByRole('navigation', { name: 'Ana gezinme' });
    await expect(nav.getByRole('link', { name: 'Envanter', exact: true })).toBeVisible();
    await expect(nav.getByRole('link', { name: 'Denetim Günlükleri' })).toHaveCount(0);
    await expect(nav.getByRole('link', { name: 'Kullanıcılar' })).toHaveCount(0);

    await page.goto('/snapshots');
    await expect(page.getByRole('button', { name: 'Yeni Snapshot Al' })).toBeEnabled();

    await page.goto('/integrations');
    await expect(page.getByRole('heading', { level: 3, name: 'vCenter · IST-DC1' })).toBeVisible();
    await expect(page.getByRole('button', { name: 'Yeni Entegrasyon Ekle' })).toBeDisabled();
    await expect(page.getByRole('button', { name: 'Entegrasyonu devre dışı bırak' }).first()).toBeDisabled();

    await page.goto('/collectors');
    await expect(page.getByRole('heading', { level: 3, name: 'collector-ist-01' })).toBeVisible();
    await expect(page.getByRole('button', { name: 'Kaldır' }).first()).toBeDisabled();

    await page.goto('/reports');
    await expect(page.getByRole('button', { name: 'Denetim CSV' })).toHaveCount(0);
    await page.waitForLoadState('networkidle');
    problems.expectNone();
  });
});

test.describe('Güvenlik Denetçisi', () => {
  useRole('auditor');

  test('denetim kaydını görür ve dışa aktarır, kullanıcı ve entegrasyon yönetemez', async ({ page, problems }) => {
    await page.goto('/');
    const nav = page.getByRole('navigation', { name: 'Ana gezinme' });
    await expect(nav.getByRole('link', { name: 'Denetim Günlükleri' })).toBeVisible();
    await expect(nav.getByRole('link', { name: 'Kullanıcılar' })).toHaveCount(0);

    await nav.getByRole('link', { name: 'Denetim Günlükleri' }).click();
    await expect(page.getByText(/Toplam Kayıt: \d+/)).toBeVisible();
    await expect(page.getByRole('button', { name: 'CSV Dışa Aktar' })).toBeEnabled();

    await page.goto('/reports');
    await expect(page.getByRole('button', { name: 'Denetim CSV' })).toBeEnabled();

    await page.goto('/integrations');
    await expect(page.getByRole('heading', { level: 3, name: 'vCenter · IST-DC1' })).toBeVisible();
    await expect(page.getByRole('button', { name: 'Yeni Entegrasyon Ekle' })).toBeDisabled();

    await page.goto('/snapshots');
    await expect(page.getByRole('button', { name: 'Yeni Snapshot Al' })).toBeDisabled();

    await page.goto('/users');
    await expect(page.getByRole('heading', { name: 'Sayfa Bulunamadı' })).toBeVisible();
    await page.waitForLoadState('networkidle');
    problems.expectNone();
  });
});

test.describe('Platform Yöneticisi', () => {
  test('tüm menü öğelerini ve yönetim düğmelerini görür', async ({ page }) => {
    await page.goto('/');
    const nav = page.getByRole('navigation', { name: 'Ana gezinme' });
    for (const name of [...ALL_NAV, 'Denetim Günlükleri', 'Kullanıcılar']) {
      await expect(nav.getByRole('link', { name, exact: true })).toBeVisible();
    }
    await page.goto('/integrations');
    await expect(page.getByRole('button', { name: 'Yeni Entegrasyon Ekle' })).toBeEnabled();
    await page.goto('/snapshots');
    await expect(page.getByRole('button', { name: 'Yeni Snapshot Al' })).toBeEnabled();
  });
});
