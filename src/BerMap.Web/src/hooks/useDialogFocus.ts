import { useEffect, useState, type RefObject } from 'react';
import { wrapFocusIndex } from '@/hooks/focusOrder';

const FOCUSABLE = [
  'a[href]',
  'area[href]',
  'button:not([disabled])',
  'input:not([disabled]):not([type="hidden"])',
  'select:not([disabled])',
  'textarea:not([disabled])',
  'iframe',
  'summary',
  '[contenteditable]:not([contenteditable="false"])',
  '[tabindex]',
].join(',');

/** Elements inside `root` that Tab can reach, in document order. */
function tabbableElements(root: HTMLElement): HTMLElement[] {
  return Array.from(root.querySelectorAll<HTMLElement>(FOCUSABLE)).filter(
    (element) =>
      element.tabIndex >= 0 &&
      !element.closest('[inert]') &&
      // Hidden (display:none, collapsed) elements have no layout boxes.
      element.getClientRects().length > 0,
  );
}

/**
 * The element focused last, tracked because a control that disables itself
 * while it works (a "Verify" button during its request) drops focus to <body>
 * before the dialog it opens renders; focus then still returns to it.
 */
let lastFocused: Element | null = null;
if (typeof document !== 'undefined') {
  document.addEventListener('focusin', (event) => {
    lastFocused = event.target instanceof Element ? event.target : null;
  });
}

/** The element to give focus back to when a dialog opening now closes. */
function currentOpener(): Element | null {
  const active = document.activeElement;
  return active && active !== document.body ? active : lastFocused;
}

/** Open dialogs, innermost last; only the top one handles Tab. */
const openDialogs: HTMLElement[] = [];

export interface DialogFocusOptions {
  /**
   * What receives focus on open when nothing inside the dialog is focused yet
   * (an `autoFocus` field wins): 'first' = the element marked `data-autofocus`,
   * else the first control that is not the close button; 'container' = the
   * dialog itself, for long read-only content.
   */
  initialFocus?: 'first' | 'container';
}

/**
 * Focus management for a modal dialog (WAI-ARIA dialog pattern): while `active`,
 * focus moves into the element behind `ref`, Tab and Shift+Tab cycle inside it,
 * and on close focus returns to the element that had it before opening.
 * Close buttons are marked with `data-dialog-close` so they are not focused first.
 */
export function useDialogFocus(
  ref: RefObject<HTMLElement | null>,
  active = true,
  { initialFocus = 'first' }: DialogFocusOptions = {},
): void {
  // The opener is captured while rendering the opening, before child effects
  // (an autoFocus field) move focus into the dialog.
  const [opener, setOpener] = useState<Element | null>(() => (active ? currentOpener() : null));
  const [wasActive, setWasActive] = useState(active);
  if (active !== wasActive) {
    setWasActive(active);
    setOpener(active ? currentOpener() : null);
  }

  useEffect(() => {
    const dialog = ref.current;
    if (!active || !dialog) return;
    openDialogs.push(dialog);

    if (!dialog.contains(document.activeElement)) {
      const preferred =
        initialFocus === 'container'
          ? null
          : (dialog.querySelector<HTMLElement>('[data-autofocus]') ??
            tabbableElements(dialog).find((element) => !element.hasAttribute('data-dialog-close')) ??
            tabbableElements(dialog)[0] ??
            null);
      if (preferred) {
        preferred.focus();
      } else {
        if (!dialog.hasAttribute('tabindex')) dialog.setAttribute('tabindex', '-1');
        dialog.focus();
      }
    }

    const onKeyDown = (event: KeyboardEvent) => {
      if (event.key !== 'Tab' || event.defaultPrevented) return;
      if (openDialogs[openDialogs.length - 1] !== dialog) return;
      const elements = tabbableElements(dialog);
      if (elements.length === 0) {
        event.preventDefault();
        dialog.focus();
        return;
      }
      const current = elements.indexOf(document.activeElement as HTMLElement);
      const next = wrapFocusIndex(elements.length, current, event.shiftKey);
      if (next !== null) {
        event.preventDefault();
        elements[next].focus();
      }
    };
    document.addEventListener('keydown', onKeyDown);

    return () => {
      document.removeEventListener('keydown', onKeyDown);
      const index = openDialogs.lastIndexOf(dialog);
      if (index >= 0) openDialogs.splice(index, 1);
      // Give focus back unless the user already moved it somewhere else.
      const focusLeftDialog =
        document.activeElement === null ||
        document.activeElement === document.body ||
        dialog.contains(document.activeElement) ||
        !document.activeElement.isConnected;
      if (focusLeftDialog && opener instanceof HTMLElement && opener.isConnected) {
        opener.focus({ preventScroll: true });
      }
    };
  }, [active, opener, ref, initialFocus]);
}
