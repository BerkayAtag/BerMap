import { test } from 'node:test';
import assert from 'node:assert/strict';
import { copyText } from './clipboard.ts';

/** A minimal document: one body, textareas that record selection, execCommand stubbed. */
function fakeDocument(execResult: boolean | 'throw') {
  const log: string[] = [];
  const body = {
    children: [] as unknown[],
    appendChild(node: unknown) {
      this.children.push(node);
      log.push('append');
    },
  };
  const doc = {
    body,
    activeElement: { focus: () => log.push('refocus') },
    createElement: () => {
      const element = {
        value: '',
        style: {} as Record<string, string>,
        setAttribute: () => undefined,
        focus: () => log.push('focus'),
        select: () => log.push(`select:${element.value}`),
        setSelectionRange: () => undefined,
        remove: () => log.push('remove'),
      };
      return element;
    },
    execCommand: (command: string) => {
      log.push(`exec:${command}`);
      if (execResult === 'throw') throw new Error('not allowed');
      return execResult;
    },
  };
  return { doc: doc as unknown as Document, log };
}

test('uses the async clipboard in a secure context', async () => {
  const written: string[] = [];
  const { doc, log } = fakeDocument(true);
  const ok = await copyText('id-1', {
    isSecureContext: true,
    clipboard: { writeText: async (text) => void written.push(text) },
    document: doc,
  });
  assert.equal(ok, true);
  assert.deepEqual(written, ['id-1']);
  assert.deepEqual(log, []);
});

test('falls back to execCommand on plain HTTP (no navigator.clipboard)', async () => {
  const { doc, log } = fakeDocument(true);
  const ok = await copyText('id-2', { isSecureContext: false, clipboard: undefined, document: doc });
  assert.equal(ok, true);
  assert.deepEqual(log, ['append', 'focus', 'select:id-2', 'exec:copy', 'remove', 'refocus']);
});

test('falls back when the async clipboard rejects (permission denied)', async () => {
  const { doc, log } = fakeDocument(true);
  const ok = await copyText('id-3', {
    isSecureContext: true,
    clipboard: { writeText: () => Promise.reject(new Error('NotAllowedError')) },
    document: doc,
  });
  assert.equal(ok, true);
  assert.ok(log.includes('exec:copy'));
});

test('reports failure instead of throwing when nothing can copy', async () => {
  const refused = fakeDocument(false);
  assert.equal(await copyText('id-4', { isSecureContext: false, document: refused.doc }), false);
  assert.ok(refused.log.includes('remove'));

  const throwing = fakeDocument('throw');
  assert.equal(await copyText('id-5', { isSecureContext: false, document: throwing.doc }), false);
  assert.ok(throwing.log.includes('remove'));

  assert.equal(await copyText('id-6', { isSecureContext: false }), false);
});
