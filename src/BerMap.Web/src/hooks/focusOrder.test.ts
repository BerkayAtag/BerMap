import { test } from 'node:test';
import assert from 'node:assert/strict';
import { wrapFocusIndex } from './focusOrder.ts';

test('Tab on the last element wraps to the first', () => {
  assert.equal(wrapFocusIndex(4, 3, false), 0);
});

test('Shift+Tab on the first element wraps to the last', () => {
  assert.equal(wrapFocusIndex(4, 0, true), 3);
});

test('moves inside the dialog are left to the browser', () => {
  assert.equal(wrapFocusIndex(4, 1, false), null);
  assert.equal(wrapFocusIndex(4, 2, true), null);
});

test('focus outside the dialog is pulled back in', () => {
  assert.equal(wrapFocusIndex(4, -1, false), 0);
  assert.equal(wrapFocusIndex(4, -1, true), 3);
});

test('a single element keeps focus on itself', () => {
  assert.equal(wrapFocusIndex(1, 0, false), 0);
  assert.equal(wrapFocusIndex(1, 0, true), 0);
});

test('no elements: nothing to move to', () => {
  assert.equal(wrapFocusIndex(0, -1, false), null);
});
