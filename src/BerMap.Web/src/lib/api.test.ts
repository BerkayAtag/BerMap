import { test } from 'node:test';
import assert from 'node:assert/strict';
import { api, getStoredToken, setSessionToken, setUnauthorizedHandler } from './api.ts';

test('session works when browser storage is unavailable', async (t) => {
  t.mock.method(globalThis, 'fetch', async (_url: string | URL | Request, options?: RequestInit) => {
    assert.equal((options?.headers as Record<string, string>).Authorization, 'Bearer memory-token');
    return new Response('{"ok":true}');
  });
  Object.defineProperty(globalThis, 'localStorage', { configurable: true, get() { throw new Error('blocked'); } });
  try {
    setSessionToken('memory-token');
    assert.deepEqual(await api.get('/inventory'), { ok: true });
    setSessionToken(null);
    assert.equal(getStoredToken(), null);
  } finally {
    Reflect.deleteProperty(globalThis, 'localStorage');
  }
});

test('late unauthorized response cannot end a replacement session', async (t) => {
  let finish!: (response: Response) => void;
  t.mock.method(globalThis, 'fetch', () => new Promise<Response>((resolve) => { finish = resolve; }));
  let signOuts = 0;
  setUnauthorizedHandler(() => { signOuts++; });
  try {
    setSessionToken('old');
    const pending = api.get('/auth/me');
    setSessionToken('new');
    finish(new Response('{}', { status: 401 }));
    await assert.rejects(pending);
    assert.equal(signOuts, 0);
    assert.equal(getStoredToken(), 'new');
    const current = api.get('/inventory');
    finish(new Response('{}', { status: 401 }));
    await assert.rejects(current);
    assert.equal(signOuts, 1);
  } finally {
    setUnauthorizedHandler(null);
    setSessionToken(null);
  }
});
