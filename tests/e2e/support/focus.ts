import type { Locator, Page } from '@playwright/test';
import { expect } from './fixtures';

/*
 * Keyboard focus checks for modal dialogs (WAI-ARIA dialog pattern): focus
 * moves into the dialog when it opens, Tab and Shift+Tab never leave it, and
 * closing it gives focus back to the control that opened it.
 */

/** True when the focused element is the dialog or inside it. */
async function focusInside(dialog: Locator): Promise<boolean> {
  return dialog.evaluate((element) => element === document.activeElement || element.contains(document.activeElement));
}

/** A short description of the focused element, for failure messages. */
async function focusedElement(page: Page): Promise<string> {
  return page.evaluate(() => {
    const element = document.activeElement as HTMLElement | null;
    if (!element || element === document.body) return '<body>';
    const name = element.getAttribute('aria-label') || element.textContent?.trim().slice(0, 40) || '';
    return `<${element.tagName.toLowerCase()}> ${name}`;
  });
}

/** Asserts focus is in the dialog now and stays there for `presses` Tab and Shift+Tab presses. */
export async function expectFocusTrapped(page: Page, dialog: Locator, presses = 12): Promise<void> {
  await expect(dialog).toBeVisible();
  await expect.poll(() => focusInside(dialog), { message: 'focus moves into the dialog on open' }).toBe(true);
  for (const key of ['Tab', 'Shift+Tab']) {
    for (let index = 0; index < presses; index++) {
      await page.keyboard.press(key);
      expect(await focusInside(dialog), `${key} #${index + 1} left the dialog (${await focusedElement(page)})`).toBe(true);
    }
  }
}

/** Opens a dialog with the keyboard from `opener`, checks the trap, closes it with Escape and expects focus back on the opener. */
export async function expectDialogFocusCycle(
  page: Page,
  opener: Locator,
  dialog: Locator,
  options: { presses?: number; close?: () => Promise<void> } = {},
): Promise<void> {
  await opener.focus();
  await page.keyboard.press('Enter');
  await expectFocusTrapped(page, dialog, options.presses);
  if (options.close) await options.close();
  else await page.keyboard.press('Escape');
  await expect(dialog).toBeHidden();
  await expect(opener, 'focus returns to the opener on close').toBeFocused();
}
