/**
 * Focus-trap arithmetic, kept free of React and the DOM so `npm test` covers it.
 *
 * Given `count` tabbable elements and the index of the focused one (-1 when focus
 * is outside them), returns the index Tab (or Shift+Tab) must move to so focus
 * stays inside, or null when the browser's own move already stays inside.
 */
export function wrapFocusIndex(count: number, current: number, shiftKey: boolean): number | null {
  if (count <= 0) return null;
  if (current < 0 || current >= count) return shiftKey ? count - 1 : 0;
  if (shiftKey && current === 0) return count - 1;
  if (!shiftKey && current === count - 1) return 0;
  return null;
}
