import { apiRequest, uniqueName } from '../support/api';
import { test, expect } from '../support/fixtures';
import { expectDialogFocusCycle, expectFocusTrapped } from '../support/focus';
import { BASELINE_SNAPSHOT, COLLECTOR_NAME } from '../support/seed';
import { adminToken } from '../support/state';

/*
 * Keyboard use of every modal dialog: opened with Enter, focus moves in, Tab
 * and Shift+Tab stay inside, Escape closes it and focus returns to the opener.
 * Nothing is saved; the dialogs are only opened and closed.
 */
test.describe('İletişim kutuları · klavye odağı', () => {
  test('entegrasyon sihirbazı: yeni ve düzenleme', async ({ page, problems }) => {
    await page.goto('/integrations');
    await expectDialogFocusCycle(
      page,
      page.getByRole('button', { name: 'Yeni Entegrasyon Ekle' }),
      page.getByRole('dialog', { name: 'Yeni Altyapı Entegrasyonu Ekle' }),
    );

    const editButton = page
      .getByRole('heading', { level: 3, name: 'vCenter · IST-DC1', exact: true })
      .locator('xpath=ancestor::*[.//button[@title="Entegrasyonu sil"]][1]')
      .getByRole('button', { name: 'Entegrasyonu düzenle' });
    const editor = page.getByRole('dialog', { name: 'Entegrasyonu Düzenle' });
    await expectDialogFocusCycle(page, editButton, editor);

    // Typing right after opening reaches the dialog: the first field has focus.
    await editButton.focus();
    await page.keyboard.press('Enter');
    await expect(editor.getByLabel('Entegrasyon Adı')).toBeFocused();
    await editor.getByRole('button', { name: 'Kapat' }).click();
    await expect(editor).toBeHidden();
    problems.expectNone();
  });

  test('kullanıcı iletişim kutuları ve parola değiştirme', async ({ page, problems }) => {
    await page.goto('/users');
    await expectDialogFocusCycle(
      page,
      page.getByRole('button', { name: 'Yeni Kullanıcı' }),
      page.getByRole('dialog', { name: 'Yeni Kullanıcı' }),
    );
    const selfRow = page.getByRole('row').filter({ hasText: 'siz' });
    await expectDialogFocusCycle(
      page,
      selfRow.getByRole('button', { name: 'Düzenle' }),
      page.getByRole('dialog', { name: 'Kullanıcıyı Düzenle' }),
    );
    await expectDialogFocusCycle(
      page,
      selfRow.getByRole('button', { name: 'Parola Sıfırla' }),
      page.getByRole('dialog', { name: 'Parola Sıfırla' }),
    );

    // Change password opens from the user menu; focus returns to the menu button.
    const menuButton = page.getByRole('button', { name: /Kullanıcı menüsü/ });
    await menuButton.click();
    await page.getByRole('menuitem', { name: 'Parolayı değiştir' }).click();
    const changePassword = page.getByRole('dialog', { name: 'Parolayı Değiştir' });
    await expectFocusTrapped(page, changePassword);
    await expect(changePassword.getByLabel('Mevcut Parola')).toBeVisible();
    await page.keyboard.press('Escape');
    await expect(changePassword).toBeHidden();
    await expect(menuButton).toBeFocused();
    problems.expectNone();
  });

  test('collector kaldırma onayı', async ({ page, problems }) => {
    await page.goto('/collectors');
    const remove = page
      .getByRole('heading', { level: 3, name: COLLECTOR_NAME })
      .locator('xpath=ancestor::*[.//button[normalize-space()="Kaldır"]][1]')
      .getByRole('button', { name: 'Kaldır' });
    const confirm = page.getByRole('dialog', { name: 'Collector kaldırılsın mı?' });
    await expectDialogFocusCycle(page, remove, confirm);

    // The safe choice has focus first, so Enter right after opening does not delete.
    await remove.focus();
    await page.keyboard.press('Enter');
    await expect(confirm.getByRole('button', { name: 'Vazgeç' })).toBeFocused();
    await page.keyboard.press('Enter');
    await expect(confirm).toBeHidden();
    await expect(page.getByRole('heading', { level: 3, name: COLLECTOR_NAME })).toBeVisible();
    problems.expectNone();
  });

  test('snapshot oluşturma, karşılaştırma, detay ve doğrulama', async ({ page, problems }) => {
    // Comparing needs two snapshots; on a fresh database only the seeded one exists.
    await apiRequest('POST', '/api/snapshots', { token: adminToken(), body: { name: uniqueName('E2E dialog') } });
    await page.goto('/snapshots');
    const create = page.getByRole('dialog', { name: 'Yeni snapshot' });
    await expectDialogFocusCycle(page, page.getByRole('button', { name: /Yeni Snapshot/ }), create);

    // Typing right after opening goes to the name field.
    await page.getByRole('button', { name: /Yeni Snapshot/ }).click();
    await expect(create).toBeVisible();
    const focusedTag = await page.evaluate(() => document.activeElement?.tagName);
    expect(focusedTag).toBe('INPUT');
    await page.keyboard.press('Escape');
    await expect(create).toBeHidden();

    await expectDialogFocusCycle(
      page,
      page.getByRole('button', { name: /Karşılaştır/ }).first(),
      page.getByRole('dialog', { name: 'Snapshot karşılaştırması' }),
    );
    // Other specs add snapshots in parallel and the list re-sorts on refetch,
    // so the row is pinned by name instead of taking the first one.
    const baselineCard = page
      .getByRole('heading', { level: 3, name: new RegExp(`^${BASELINE_SNAPSHOT}`) })
      .first()
      .locator('xpath=ancestor::*[.//button[normalize-space()="Doğrula"]][1]');
    await expectDialogFocusCycle(
      page,
      baselineCard.getByRole('button', { name: 'Detay', exact: true }),
      page.getByRole('dialog', { name: 'Snapshot detayı' }),
    );

    const verify = baselineCard.getByRole('button', { name: 'Doğrula', exact: true });
    const result = page.getByRole('dialog', { name: 'Snapshot doğrulama sonucu' });
    await verify.focus();
    await page.keyboard.press('Enter');
    await expectFocusTrapped(page, result);
    await page.keyboard.press('Escape');
    await expect(result).toBeHidden();
    await expect(verify).toBeFocused();
    problems.expectNone();
  });

  test('envanter varlık detayı', async ({ page, problems }) => {
    await page.goto('/inventory');
    const firstRow = page.locator('tbody tr[tabindex="0"]').first();
    await expect(firstRow).toBeVisible();
    await expectDialogFocusCycle(page, firstRow, page.getByRole('dialog', { name: 'Varlık detayı' }));
    problems.expectNone();
  });
});
