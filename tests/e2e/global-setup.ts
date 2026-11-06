import fs from 'node:fs/promises';
import path from 'node:path';
import { ensureUser, login, waitForReady } from './support/api';
import { ADMIN_USERNAME, AUTH_DIR, BASE_URL, adminPassword, enrollmentToken } from './support/env';
import { ROLE_USERS, type RoleName, writeSetupState } from './support/state';
import { seedDemoData } from './support/seed';

/*
 * Runs once before the suite: waits for the server, seeds the synthetic demo
 * data through the real API and signs in one account per role. Each role's
 * bearer token is stored as a Playwright storage state (localStorage), so tests
 * start signed in without going through the rate-limited login form.
 */
export default async function globalSetup(): Promise<void> {
  await waitForReady();

  const admin = await login(ADMIN_USERNAME, adminPassword());
  const seed = await seedDemoData(admin.token, enrollmentToken());

  const tokens: Partial<Record<RoleName, string>> = { admin: admin.token };
  const userIds: Partial<Record<RoleName, string>> = { admin: admin.user.id };
  for (const role of Object.keys(ROLE_USERS) as Array<keyof typeof ROLE_USERS>) {
    const user = ROLE_USERS[role];
    const created = await ensureUser(admin.token, user);
    userIds[role] = created.id;
    tokens[role] = (await login(user.username, user.password)).token;
  }

  await fs.mkdir(AUTH_DIR, { recursive: true });
  for (const [role, token] of Object.entries(tokens)) {
    await fs.writeFile(
      path.join(AUTH_DIR, `${role}.json`),
      JSON.stringify(
        {
          cookies: [],
          origins: [
            {
              origin: new URL(BASE_URL).origin,
              localStorage: [
                { name: 'bermap-token', value: token },
                { name: 'bermap-locale', value: 'tr' },
                { name: 'bermap-theme', value: 'light' },
              ],
            },
          ],
        },
        null,
        2,
      ),
    );
  }

  await writeSetupState({ ...seed, tokens: tokens as Record<RoleName, string>, userIds: userIds as Record<RoleName, string> });
}
