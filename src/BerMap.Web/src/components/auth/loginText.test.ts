import { test } from 'node:test';
import assert from 'node:assert/strict';
import { loginErrorText, signOutReasonText } from './loginText.ts';

const serverInvalid = { status: 401, code: 'invalid_credentials', message: 'Kullanıcı adı veya parola hatalı.' };

test('a wrong password is shown in the console language, not the server text', () => {
  assert.equal(loginErrorText(serverInvalid, false), 'Invalid username or password.');
  assert.equal(loginErrorText(serverInvalid, true), 'Kullanıcı adı veya parola hatalı.');
  assert.equal(loginErrorText({ status: 401, message: 'Unauthorized' }, false), 'Invalid username or password.');
});

test('the empty-field 400 and the disabled account are localized by code', () => {
  const required = { status: 400, code: 'invalid_credentials', message: 'Kullanıcı adı ve parola zorunludur.' };
  assert.equal(loginErrorText(required, false), 'Username and password are required.');
  assert.equal(
    loginErrorText({ status: 401, code: 'account_disabled', message: 'Kullanıcı hesabı devre dışı.' }, false),
    'This user account is disabled.',
  );
});

test('rate limiting is localized whatever the server says', () => {
  assert.equal(
    loginErrorText({ status: 429, message: 'Too Many Requests' }, false),
    'Too many sign-in attempts. Please try again in a minute.',
  );
  assert.match(loginErrorText({ status: 429, message: 'Çok fazla istek' }, true), /^Çok fazla giriş denemesi/);
});

test('unknown failures keep the server text as a fallback', () => {
  assert.equal(loginErrorText({ status: 502, message: 'Bad Gateway' }, false), 'Login failed. (Bad Gateway)');
  assert.equal(loginErrorText({ status: 500, message: 'Sunucu hatası' }, true), 'Sunucu hatası');
  assert.equal(loginErrorText({}, false), 'Login failed.');
  assert.equal(loginErrorText(null, true), 'Giriş başarısız.');
});

test('every sign-out reason has a text in both languages', () => {
  for (const reason of ['session_ended', 'role_changed', 'password_reset'] as const) {
    assert.ok(signOutReasonText(reason, true).length > 10);
    assert.ok(signOutReasonText(reason, false).length > 10);
    assert.notEqual(signOutReasonText(reason, true), signOutReasonText(reason, false));
  }
});
