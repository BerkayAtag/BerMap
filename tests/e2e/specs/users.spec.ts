import type { Browser, BrowserContext } from '@playwright/test';
import { apiRequest, login, uniqueName, type UserDto } from '../support/api';
import { BASE_URL } from '../support/env';
import { test, expect } from '../support/fixtures';
import { adminToken } from '../support/state';

/*
 * User management through the console. Every test works on accounts it creates
 * itself, so the role accounts used by other tests keep their sessions.
 */

/** A browser context signed in with `token`, in Turkish. */
async function signedInContext(browser: Browser, token: string): Promise<BrowserContext> {
  return browser.newContext({
    storageState: {
      cookies: [],
      origins: [
        {
          origin: new URL(BASE_URL).origin,
          localStorage: [
            { name: 'bermap-token', value: token },
            { name: 'bermap-locale', value: 'tr' },
          ],
        },
      ],
    },
  });
}

async function findUser(username: string): Promise<UserDto> {
  const users = await apiRequest<UserDto[]>('GET', '/api/users', { token: adminToken() });
  const user = users.find((u) => u.username === username);
  expect(user, `user ${username}`).toBeDefined();
  return user!;
}

test.describe('Kullanıcılar', () => {
  test('kullanıcı oluşturulur, rolü düzenlenir, parolası sıfırlanır ve devre dışı bırakılır', async ({ page, problems }) => {
    test.setTimeout(150_000);
    const username = uniqueName('e2e-ui');
    const firstPassword = 'Ilk-Parola-2026-xyz';
    const resetPassword = 'Sifirlanan-Parola-2026-xyz';

    await page.goto('/users');
    await page.getByRole('button', { name: 'Yeni Kullanıcı' }).click();
    const create = page.getByRole('dialog', { name: 'Yeni Kullanıcı' });
    await create.getByLabel('Kullanıcı Adı').fill(username);
    await create.getByLabel('Görünen Ad (Opsiyonel)').fill('E2E Arayüz Kullanıcısı');
    await create.getByRole('combobox', { name: 'Rol' }).selectOption({ label: 'Salt-Okunur Görüntüleyici' });
    await create.getByLabel('Parola', { exact: true }).fill(firstPassword);
    await create.getByLabel('Parola (Tekrar)').fill(firstPassword);
    await create.getByRole('button', { name: 'Oluştur' }).click();
    await expect(create).toBeHidden();
    await expect(page.getByRole('status').filter({ hasText: `${username} kullanıcısı oluşturuldu.` })).toBeVisible();

    const row = page.getByRole('row').filter({ hasText: username });
    await expect(row).toContainText('Salt-Okunur Görüntüleyici');
    await expect(row).toContainText('Etkin');

    // Role change
    await row.getByRole('button', { name: 'Düzenle' }).click();
    const edit = page.getByRole('dialog', { name: 'Kullanıcıyı Düzenle' });
    await expect(edit).toContainText(username);
    await edit.getByRole('combobox', { name: 'Rol' }).selectOption({ label: 'Altyapı Operatörü' });
    await edit.getByRole('button', { name: 'Kaydet' }).click();
    await expect(edit).toBeHidden();
    await expect(page.getByRole('status').filter({ hasText: `${username} güncellendi.` })).toBeVisible();
    await expect(row).toContainText('Altyapı Operatörü');
    expect((await findUser(username)).role).toBe('InfrastructureOperator');

    // Password reset: the new password signs in.
    await row.getByRole('button', { name: 'Parola Sıfırla' }).click();
    const reset = page.getByRole('dialog', { name: 'Parola Sıfırla' });
    await reset.getByLabel('Yeni Parola', { exact: true }).fill(resetPassword);
    await reset.getByLabel('Yeni Parola (Tekrar)').fill(resetPassword);
    await reset.getByRole('button', { name: 'Parolayı Sıfırla' }).click();
    await expect(reset).toBeHidden();
    await expect(page.getByRole('status').filter({ hasText: `${username} için parola sıfırlandı.` })).toBeVisible();
    const signedIn = await login(username, resetPassword);
    expect(signedIn.user.role).toBe('InfrastructureOperator');

    // Deactivation revokes the session issued above.
    await row.getByRole('button', { name: 'Düzenle' }).click();
    await edit.getByRole('checkbox', { name: /Hesap etkin/ }).uncheck();
    await edit.getByRole('button', { name: 'Kaydet' }).click();
    await expect(edit).toBeHidden();
    await expect(row).toContainText('Devre dışı');
    expect((await findUser(username)).isActive).toBe(false);
    const me = await fetch(`${BASE_URL}/api/auth/me`, { headers: { Authorization: `Bearer ${signedIn.token}` } });
    expect(me.status).toBe(401);

    problems.expectNone();
  });

  test('istemci tarafı parola kuralları ve yinelenen kullanıcı adı hata gösterir', async ({ page }) => {
    await page.goto('/users');
    await page.getByRole('button', { name: 'Yeni Kullanıcı' }).click();
    const create = page.getByRole('dialog', { name: 'Yeni Kullanıcı' });
    await create.getByLabel('Kullanıcı Adı').fill('e2e-viewer');
    await create.getByLabel('Parola', { exact: true }).fill('kisa');
    await create.getByLabel('Parola (Tekrar)').fill('kisa');
    await create.getByRole('button', { name: 'Oluştur' }).click();
    await expect(create).toContainText('Parola en az 12 karakter olmalıdır.');

    await create.getByLabel('Parola', { exact: true }).fill('Yeterince-Uzun-Parola-1');
    await create.getByLabel('Parola (Tekrar)').fill('Yeterince-Uzun-Parola-1');
    await create.getByRole('button', { name: 'Oluştur' }).click();
    await expect(create).toContainText('Bu kullanıcı adı zaten kullanılıyor.');

    await create.getByRole('button', { name: 'Vazgeç' }).click();
    await expect(create).toBeHidden();
  });

  test('kendi hesabı devre dışı bırakılamaz', async ({ page }) => {
    await page.goto('/users');
    await page.getByRole('row').filter({ hasText: 'siz' }).getByRole('button', { name: 'Düzenle' }).click();
    const edit = page.getByRole('dialog', { name: 'Kullanıcıyı Düzenle' });
    await expect(edit.getByRole('checkbox', { name: /Hesap etkin/ })).toBeDisabled();
    await page.keyboard.press('Escape');
    await expect(edit).toBeHidden();
  });

  test('kullanıcı menüsünden kendi parolasını değiştirir ve oturumu açık kalır', async ({ browser }) => {
    test.setTimeout(150_000);
    const username = uniqueName('e2e-self');
    const oldPassword = 'Eski-Parola-2026-xyz';
    const newPassword = 'Yeni-Parola-2026-xyz';
    await apiRequest('POST', '/api/users', {
      token: adminToken(),
      body: { username, displayName: 'E2E Kendi Parolası', email: '', role: 'ReadOnlyViewer', password: oldPassword },
    });
    const session = await login(username, oldPassword);

    const context = await browser.newContext({
      storageState: {
        cookies: [],
        origins: [
          {
            origin: new URL(BASE_URL).origin,
            localStorage: [
              { name: 'bermap-token', value: session.token },
              { name: 'bermap-locale', value: 'tr' },
            ],
          },
        ],
      },
    });
    const page = await context.newPage();
    await page.goto('/');
    await page.getByRole('button', { name: 'Kullanıcı menüsü: E2E Kendi Parolası' }).click();
    await page.getByRole('menuitem', { name: 'Parolayı değiştir' }).click();

    const dialog = page.getByRole('dialog', { name: 'Parolayı Değiştir' });
    await dialog.getByLabel('Mevcut Parola').fill(oldPassword);
    await dialog.getByLabel('Yeni Parola', { exact: true }).fill(newPassword);
    await dialog.getByLabel('Yeni Parola (Tekrar)').fill(newPassword);
    // change-password shares the sign-in rate limit (10 per minute per address)
    // with globalSetup and the other sign-ins: a 429 waits for the next window.
    for (let attempt = 0; attempt < 3; attempt++) {
      const response = page.waitForResponse((r) => r.url().endsWith('/api/auth/change-password'));
      await dialog.getByRole('button', { name: 'Parolayı Değiştir' }).click();
      if ((await response).status() !== 429) break;
      await page.waitForTimeout(21_000);
    }
    await expect(dialog).toContainText('Parolanız değiştirildi');
    await dialog.getByRole('button', { name: 'Kapat' }).last().click();
    await expect(dialog).toBeHidden();

    // The response carried a fresh token: the console stays signed in after a reload.
    const storedToken = await page.evaluate(() => localStorage.getItem('bermap-token'));
    expect(storedToken).not.toBe(session.token);
    await page.reload();
    await expect(page.getByRole('heading', { level: 1, name: 'Altyapı İstihbarat Paneli' })).toBeVisible();
    await expect(page).toHaveURL(/\/$/);

    // The old token was revoked; the new password signs in.
    const oldMe = await fetch(`${BASE_URL}/api/auth/me`, { headers: { Authorization: `Bearer ${session.token}` } });
    expect(oldMe.status).toBe(401);
    await login(username, newPassword);
    await context.close();
  });
  test('yönetici kendi rolünü değiştirince uyarılır, oturumu kapanır ve giriş sayfası nedenini söyler', async ({ browser }) => {
    const username = uniqueName('e2e-selfrole');
    const password = 'Kendi-Rolu-2026-xyz';
    await apiRequest('POST', '/api/users', {
      token: adminToken(),
      body: { username, displayName: 'E2E Kendi Rolü', email: '', role: 'PlatformAdministrator', password },
    });
    // login() waits out the sign-in rate limit when the suite has used it up.
    test.setTimeout(150_000);
    const session = await login(username, password);
    const context = await signedInContext(browser, session.token);
    const page = await context.newPage();

    await page.goto('/users');
    const selfRow = page.getByRole('row').filter({ hasText: username });
    await expect(selfRow).toContainText('siz');
    await selfRow.getByRole('button', { name: 'Düzenle' }).click();
    const edit = page.getByRole('dialog', { name: 'Kullanıcıyı Düzenle' });
    // No warning until the role actually changes.
    await expect(edit.getByRole('button', { name: 'Kaydet', exact: true })).toBeVisible();
    await expect(edit).not.toContainText('oturumunuz kapatılır');

    await edit.getByRole('combobox', { name: 'Rol' }).selectOption({ label: 'Altyapı Operatörü' });
    await expect(edit.getByRole('note')).toContainText('Kendi rolünüzü değiştiriyorsunuz. Kaydettiğinizde oturumunuz kapatılır');
    await expect(edit.getByRole('note')).toContainText('bu sayfaya da erişemezsiniz');
    await edit.getByRole('button', { name: 'Kaydet ve Oturumu Kapat' }).click();

    await expect(page).toHaveURL(/\/login$/);
    await expect(page.getByRole('status')).toContainText('Rolünüz değiştirildiği için oturumunuz kapatıldı');
    expect(await page.evaluate(() => localStorage.getItem('bermap-token'))).toBeNull();
    expect((await findUser(username)).role).toBe('InfrastructureOperator');
    const oldMe = await fetch(`${BASE_URL}/api/auth/me`, { headers: { Authorization: `Bearer ${session.token}` } });
    expect(oldMe.status).toBe(401);
    await context.close();
  });

  test('yönetici kendi parolasını sıfırlarken uyarılır, ardından yeni parolayla giriş istenir', async ({ browser }) => {
    const username = uniqueName('e2e-selfreset');
    const password = 'Kendi-Parola-2026-xyz';
    const resetTo = 'Sifirlanan-Kendi-2026-xyz';
    await apiRequest('POST', '/api/users', {
      token: adminToken(),
      body: { username, displayName: 'E2E Kendi Sıfırlama', email: '', role: 'PlatformAdministrator', password },
    });
    // login() waits out the sign-in rate limit when the suite has used it up.
    test.setTimeout(150_000);
    const session = await login(username, password);
    const context = await signedInContext(browser, session.token);
    const page = await context.newPage();

    await page.goto('/users');
    await page.getByRole('row').filter({ hasText: username }).getByRole('button', { name: 'Parola Sıfırla' }).click();
    const reset = page.getByRole('dialog', { name: 'Parola Sıfırla' });
    await expect(reset.getByRole('note')).toContainText('Kendi parolanızı sıfırlıyorsunuz');
    await expect(reset.getByRole('note')).toContainText('Parolayı değiştir');
    await reset.getByLabel('Yeni Parola', { exact: true }).fill(resetTo);
    await reset.getByLabel('Yeni Parola (Tekrar)').fill(resetTo);
    await reset.getByRole('button', { name: 'Sıfırla ve Oturumu Kapat' }).click();

    await expect(page).toHaveURL(/\/login$/);
    await expect(page.getByRole('status')).toContainText('Parolanız sıfırlandığı için oturumunuz kapatıldı');
    const oldMe = await fetch(`${BASE_URL}/api/auth/me`, { headers: { Authorization: `Bearer ${session.token}` } });
    expect(oldMe.status).toBe(401);
    await context.close();
  });

  test('başka bir kullanıcının parolası sıfırlanırken oturum uyarısı gösterilmez', async ({ page }) => {
    await page.goto('/users');
    await page.getByRole('row').filter({ hasText: 'e2e-viewer' }).getByRole('button', { name: 'Parola Sıfırla' }).click();
    const reset = page.getByRole('dialog', { name: 'Parola Sıfırla' });
    await expect(reset.getByRole('button', { name: 'Parolayı Sıfırla' })).toBeVisible();
    await expect(reset.getByRole('note')).toHaveCount(0);
    await reset.getByRole('button', { name: 'Vazgeç' }).click();
    await expect(reset).toBeHidden();
  });
});
