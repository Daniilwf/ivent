import type { FullConfig } from '@playwright/test';
import { Api, seedAdmin, type Schemas } from './api.ts';
import { createAccountAt } from './world.ts';

// Before any scenario moves the site's clock: a player of the demo season for scenario 13 (tests/late/s13-speed). The
// demo players also play the development season, which is newer and would open instead; a player added to the demo
// season alone opens it. Players join only a running season, and the clock moves past the demo deadline as the
// scenarios run, so the player joins here, first. Only on the E2E site: an address given in E2E_BASE_URL (the smoke
// tests) is left alone unless E2E_LOCAL_SITE=1 says it is an E2E site started by hand (scripts/e2e-server.mjs, D-231).

/** DemoSeed.SeasonId */
export const demoSeasonId = 'de300000-0000-0000-0000-000000000001';

export const speedPlayer = { login: 'e2e-speed', name: 'Гость' };

export default async function globalSetup(config: FullConfig) {
  if (process.env['E2E_BASE_URL'] && process.env['E2E_LOCAL_SITE'] !== '1') return;
  const baseURL = config.projects[0]?.use.baseURL;
  if (!baseURL) throw new Error('No baseURL.');
  const admin = await Api.signIn(baseURL, seedAdmin.login, seedAdmin.password);
  const accounts = await admin.get<Schemas['AccountView'][]>('/api/admin/accounts');
  const existing = accounts.find((a) => a.login === speedPlayer.login);
  if (!existing) {
    const account = await createAccountAt(
      baseURL,
      admin,
      'player',
      speedPlayer.login,
      speedPlayer.name,
    );
    await admin.post(`/api/admin/seasons/${demoSeasonId}/players`, { userId: account.id });
  }
  await admin.dispose();
}
