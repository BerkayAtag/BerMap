import type { Page } from '@playwright/test';
import { test, expect } from '../support/fixtures';
import { ADMIN_USERNAME, adminPassword } from '../support/env';
import { authFile } from '../support/state';

/*
 * The login endpoint allows 10 attempts per minute per client address and
 * globalSetup already used some, so this is the only file that signs in
 * through the form. A 429 waits for the next window instead of failing.
 */
async function submitLogin(page: Page, password: string): Promise<void> {
  for (let attempt = 0; attempt < 3; attempt++) {
    const response = page.waitForResponse((r) => r.url().endsWith('/api/auth/login'));
    await page.getByLabel('Parola').fill(password);
    await page.getByRole('button', { name: 'Giriş Yap' }).click();
    if ((await response).status() !== 429) return;
    await page.waitForTimeout(21_000);
  }
}

test.describe('Giriş', () => {
  test.use({ storageState: { cookies: [], origins: [] } });

  test('korumalı rota girişe yönlendirir, hatalı parola reddedilir, doğru parola hedef sayfayı açar', async ({
    page,
    problems,
  }) => {
    test.setTimeout(150_000);
    await page.goto('/inventory?q=sql');
    await expect(page).toHaveURL(/\/login$/);
    await expect(page.getByRole('heading', { name: /BerMap Web Console/ })).toBeVisible();

    await page.getByLabel('Kullanıcı Adı').fill(ADMIN_USERNAME);
    await submitLogin(page, 'yanlis-parola-123456');
    await expect(page.getByRole('alert')).toContainText('Kullanıcı adı veya parola hatalı');
    await expect(page).toHaveURL(/\/login$/);
    // The rejected attempt (and a rate-limited one) are the only expected failures.
    problems.expectNone([/POST \/api\/auth\/login -> (401|429)/, /status of (401|429)/]);
    problems.clear();

    await submitLogin(page, adminPassword());
    // Back to the page the guard interrupted, query string included.
    await expect(page).toHaveURL(/\/inventory\?q=sql$/);
    await expect(page.getByRole('heading', { level: 1, name: /Altyapı Envanteri/ })).toBeVisible();
    await expect(page.getByRole('textbox', { name: /FQDN, IP, MAC/ })).toHaveValue('sql');
    await expect(page.getByRole('button', { name: 'Kullanıcı menüsü: Platform Administrator' })).toBeVisible();
    expect(await page.evaluate(() => localStorage.getItem('bermap-token'))).toBeTruthy();
    problems.expectNone([/POST \/api\/auth\/login -> 429/, /status of 429/]);
  });

  test('geçersiz token oturumu sonlandırır', async ({ page }) => {
    await page.addInitScript(() => {
      if (!sessionStorage.getItem('seeded')) {
        sessionStorage.setItem('seeded', '1');
        localStorage.setItem('bermap-token', 'not-a-valid-token');
      }
    });
    await page.goto('/capacity');
    await expect(page).toHaveURL(/\/login$/);
    expect(await page.evaluate(() => localStorage.getItem('bermap-token'))).toBeNull();
  });
});

test.describe('Çıkış', () => {
  test.use({ storageState: authFile('admin') });

  test('çıkış oturumu kapatır ve korumalı sayfalar yeniden girişe yönlenir', async ({ page }) => {
    await page.goto('/');
    await expect(page.getByRole('heading', { level: 1, name: 'Altyapı İstihbarat Paneli' })).toBeVisible();

    await page.getByRole('button', { name: /Kullanıcı menüsü/ }).click();
    await page.getByRole('menuitem', { name: 'Çıkış Yap' }).click();
    await expect(page).toHaveURL(/\/login$/);
    expect(await page.evaluate(() => localStorage.getItem('bermap-token'))).toBeNull();

    await page.goto('/snapshots');
    await expect(page).toHaveURL(/\/login$/);
    // Sign-out is local only: the shared admin token stays valid for the other tests.
  });
});
