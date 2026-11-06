import type { Locator, Page } from '@playwright/test';
import { test, expect, expectNoHorizontalOverflow } from '../support/fixtures';

/*
 * Every console page loads with the demo data, with no console errors, no
 * failed /api request and no sideways overflow: on a desktop and on a 390 px
 * phone, in the light and the dark theme.
 */

interface PageCase {
  path: string;
  nav: string;
  /** Visible once the page has rendered its data. */
  ready: (page: Page) => Locator;
}

const PAGES: PageCase[] = [
  { path: '/', nav: 'Dashboard', ready: (p) => p.getByRole('heading', { level: 1, name: 'Altyapı İstihbarat Paneli' }) },
  { path: '/map', nav: 'Altyapı Haritası', ready: (p) => p.getByText('Gösterilen:') },
  { path: '/inventory', nav: 'Envanter', ready: (p) => p.getByRole('cell', { name: /^app-core-01 / }) },
  { path: '/simulation', nav: 'Simülasyon', ready: (p) => p.getByRole('heading', { level: 1, name: 'Simülasyon ve Etki Analizi' }) },
  { path: '/snapshots', nav: 'Snapshot', ready: (p) => p.getByRole('heading', { level: 3, name: /Baseline · Kasım 2026/ }).first() },
  { path: '/capacity', nav: 'Kapasite İstihbaratı', ready: (p) => p.getByRole('link', { name: 'CL-PROD-01' }) },
  { path: '/reports', nav: 'Raporlar', ready: (p) => p.getByRole('button', { name: 'Envanter CSV' }) },
  { path: '/integrations', nav: 'Entegrasyonlar', ready: (p) => p.getByRole('heading', { level: 3, name: 'vCenter · IST-DC1' }) },
  { path: '/collectors', nav: 'Collectorlar', ready: (p) => p.getByRole('heading', { level: 3, name: 'collector-ist-01' }) },
  { path: '/audit', nav: 'Denetim Günlükleri', ready: (p) => p.getByText(/Toplam Kayıt: \d+/) },
  { path: '/users', nav: 'Kullanıcılar', ready: (p) => p.getByRole('main').getByText('e2e-viewer').filter({ visible: true }).first() },
];

const LAYOUTS = [
  { name: 'masaüstü', viewport: { width: 1440, height: 900 } },
  { name: '390 px', viewport: { width: 390, height: 844 } },
] as const;

for (const theme of ['light', 'dark'] as const) {
  for (const layout of LAYOUTS) {
    test.describe(`Sayfalar · ${layout.name} · ${theme === 'light' ? 'açık' : 'koyu'} tema`, () => {
      test.use({ viewport: layout.viewport, colorScheme: theme });
      test.beforeEach(async ({ page }) => {
        await page.addInitScript((value) => localStorage.setItem('bermap-theme', value), theme);
      });

      for (const target of PAGES) {
        test(`${target.path} hatasız yüklenir`, async ({ page, problems }) => {
          await page.goto(target.path);
          await expect(target.ready(page)).toBeVisible();
          await expect(page.locator('html')).toHaveAttribute('data-theme', theme);
          await page.waitForLoadState('networkidle');
          await expect(page.getByRole('alert')).toHaveCount(0);
          await expectNoHorizontalOverflow(page);
          problems.expectNone();
        });
      }
    });
  }
}

test.describe('Gezinme', () => {
  test('masaüstünde menü öğeleri her sayfayı açar ve etkin öğeyi işaretler', async ({ page, problems }) => {
    await page.goto('/');
    const nav = page.getByRole('navigation', { name: 'Ana gezinme' });
    for (const target of PAGES) {
      await nav.getByRole('link', { name: target.nav, exact: true }).click();
      await expect(page).toHaveURL(new RegExp(`${target.path === '/' ? '/' : target.path}$`));
      await expect(target.ready(page)).toBeVisible();
      await expect(nav.getByRole('link', { name: target.nav, exact: true })).toHaveAttribute('aria-current', 'page');
    }
    problems.expectNone();
  });

  test('kenar çubuğu daraltılır ve genişletilir', async ({ page }) => {
    await page.goto('/');
    await page.getByRole('button', { name: 'Daralt' }).click();
    await expect(page.getByRole('button', { name: 'Genişlet' })).toHaveAttribute('aria-expanded', 'false');
    // Icon-only links keep their accessible names.
    await expect(page.getByRole('link', { name: 'Envanter' })).toBeVisible();
    await page.getByRole('button', { name: 'Genişlet' }).click();
    await expect(page.getByRole('button', { name: 'Daralt' })).toHaveAttribute('aria-expanded', 'true');
  });

  test('tema ve dil değiştirilir, tercih yeniden yüklemede korunur', async ({ page }) => {
    await page.goto('/');
    await page.getByRole('button', { name: 'Koyu Temaya Geç' }).click();
    await expect(page.locator('html')).toHaveAttribute('data-theme', 'dark');

    await page.getByRole('button', { name: /Dili değiştir/ }).click();
    await page.getByRole('menuitemradio', { name: 'English' }).click();
    await expect(page.getByRole('link', { name: 'Inventory', exact: true })).toBeVisible();
    await page.reload();
    await expect(page.locator('html')).toHaveAttribute('data-theme', 'dark');
    await expect(page.locator('html')).toHaveAttribute('lang', 'en');

    await page.getByRole('button', { name: /Change language/ }).click();
    await page.getByRole('menuitemradio', { name: 'Türkçe' }).click();
    await page.getByRole('button', { name: 'Açık Temaya Geç' }).click();
    await expect(page.getByRole('link', { name: 'Envanter', exact: true })).toBeVisible();
    await expect(page.locator('html')).toHaveAttribute('data-theme', 'light');
  });

  test('global arama sonuca ve envanter aramasına götürür', async ({ page }) => {
    await page.goto('/');
    const search = page.getByRole('combobox', { name: 'Global arama' });
    await search.fill('sql-core');
    await page.getByRole('option', { name: /sql-core-01/ }).first().click();
    await expect(page).toHaveURL(/\/inventory\?node=[0-9a-f-]{36}$/);

    await search.fill('payments');
    await search.press('Enter');
    await expect(page).toHaveURL(/\/inventory\?q=payments$/);
    await expect(page.getByRole('cell', { name: /^app-payments-01 / })).toBeVisible();
  });
});

test.describe('Mobil çekmece menü', () => {
  test.use({ viewport: { width: 390, height: 844 } });

  test('açılır, Escape ve kapat düğmesiyle kapanır, gezinmede kendiliğinden kapanır', async ({ page, problems }) => {
    await page.goto('/');
    const drawer = page.locator('#bm-sidebar');
    const toggle = page.getByRole('button', { name: 'Menüyü aç' });
    await expect(drawer).not.toBeInViewport();

    await toggle.click();
    await expect(drawer).toBeInViewport({ ratio: 0.9 });
    await expect(page.getByRole('banner').getByRole('button', { name: 'Menüyü kapat' })).toHaveAttribute('aria-expanded', 'true');
    await page.keyboard.press('Escape');
    await expect(drawer).not.toBeInViewport();

    await toggle.click();
    await expect(drawer).toBeInViewport({ ratio: 0.9 });
    await drawer.getByRole('button', { name: 'Menüyü kapat' }).click();
    await expect(drawer).not.toBeInViewport();

    await toggle.click();
    await drawer.getByRole('link', { name: 'Envanter' }).click();
    await expect(page).toHaveURL(/\/inventory$/);
    await expect(drawer).not.toBeInViewport();
    await expectNoHorizontalOverflow(page);

    // The compact search opens from its icon and closes again.
    await page.getByRole('button', { name: 'Aramayı aç' }).click();
    await expect(page.getByRole('combobox', { name: 'Global arama' })).toBeFocused();
    await page.getByRole('button', { name: 'Aramayı kapat' }).click();
    await expect(page.getByRole('combobox', { name: 'Global arama' })).toHaveCount(0);
    problems.expectNone();
  });
});
