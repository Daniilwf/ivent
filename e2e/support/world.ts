import { expect, test, type Browser, type Page } from '@playwright/test';
import { Api, commandId, seedAdmin, type Schemas } from './api.ts';

// Each scenario plays in its own season with its own accounts (TESTING.md «Изоляция», I1): the seed's admin creates an
// admin, players and a spectator for the test through the API, the test's admin creates the season. Nothing is shared
// between tests but the pool of games, the site's clock and its random source (D-230).

export type Role = 'player' | 'admin' | 'spectator';

export type Account = { id: string; login: string; name: string; password: string; role: Role };

export type Player = Account & { playerId: string };

export type World = {
  seasonId: string;
  seasonName: string;
  admin: Account;
  /** The test's admin, signed in through the API */
  adminApi: Api;
  players: Player[];
  spectator: Account | null;
};

/** The admin's pages follow the season at most every 10 s (AdminScreen's refreshEveryMs): what they wait for live */
export const adminRefresh = { timeout: 15_000 };

/** The password every test account gets after its temporary one */
export const testPassword = 'e2e-password-1';

export function baseURL(): string {
  const url = test.info().project.use.baseURL;
  if (!url) throw new Error('The project has no baseURL.');
  return url;
}

/** A tag unique to this run of this test: logins and season names carry it */
export function uniqueTag(): string {
  return `${Date.now().toString(36)}${Math.floor(Math.random() * 36 ** 4)
    .toString(36)
    .padStart(4, '0')}`;
}

let root: Promise<Api> | null = null;

/** The development seed's admin, one session per worker */
function rootAdmin(): Promise<Api> {
  const session = root ?? Api.signIn(baseURL(), seedAdmin.login, seedAdmin.password);
  root = session;
  return session;
}

/** An account made by an admin; its temporary password is changed at once, as its owner would on the first visit */
export async function createAccount(
  admin: Api,
  role: Role,
  login: string,
  name: string,
): Promise<Account> {
  const created = await admin.post<Schemas['AccountActionResponse']>('/api/admin/accounts', {
    login,
    name,
    role,
  });
  const temporary = created.temporaryPassword;
  if (!temporary) throw new Error(`No temporary password for ${login}.`);
  const owner = await Api.signIn(baseURL(), login, temporary);
  await owner.post('/api/auth/password', { currentPassword: temporary, newPassword: testPassword });
  await owner.dispose();
  return { id: created.account.id, login, name, password: testPassword, role };
}

/**
 * A season of its own: the test's admin, the players by name (in this order), a spectator when asked; started unless
 * `start: false`. Accounts are named after the tag, so parallel tests never meet.
 */
export async function setUpSeason(options: {
  players: string[];
  spectator?: boolean;
  start?: boolean;
}): Promise<World> {
  const tag = uniqueTag();
  const seed = await rootAdmin();
  const admin = await createAccount(seed, 'admin', `a-${tag}`, `Админ ${tag}`);
  const accounts = await Promise.all(
    options.players.map((name, i) =>
      createAccount(seed, 'player', `p${String(i + 1)}-${tag}`, name),
    ),
  );
  const spectator = options.spectator
    ? await createAccount(seed, 'spectator', `s-${tag}`, 'Зритель')
    : null;

  const adminApi = await Api.signIn(baseURL(), admin.login, admin.password);
  const seasonId = crypto.randomUUID();
  const seasonName = `Сезон ${tag}`;
  await adminApi.post('/api/admin/seasons', { seasonId, name: seasonName });
  for (const account of accounts)
    await adminApi.post(`/api/admin/seasons/${seasonId}/players`, { userId: account.id });
  if (options.start !== false)
    await adminApi.post(`/api/admin/seasons/${seasonId}/status`, { to: 'active' });

  const views = await adminApi.get<Schemas['AdminPlayerView'][]>(
    `/api/admin/seasons/${seasonId}/players`,
  );
  const players = accounts.map((account) => {
    const view = views.find((v) => v.userId === account.id);
    if (!view) throw new Error(`${account.login} is not in the season.`);
    return { ...account, playerId: view.id };
  });
  return { seasonId, seasonName, admin, adminApi, players, spectator };
}

/** The API as this account */
export function apiOf(account: { login: string; password: string }): Promise<Api> {
  return Api.signIn(baseURL(), account.login, account.password);
}

/** The season as this account's API sees it */
export function seasonOf(api: Api, seasonId: string): Promise<Schemas['SeasonView']> {
  return api.get<Schemas['SeasonView']>(`/api/seasons/${seasonId}`);
}

/** Signs in through the sign-in form in a browser of its own (the project's device) and waits for the site */
export async function signIn(
  browser: Browser,
  account: { login: string; password: string },
): Promise<Page> {
  const context = await browser.newContext();
  const page = await context.newPage();
  await page.goto('/');
  await signInHere(page, account);
  return page;
}

/** Fills the sign-in form on this page */
export async function signInHere(page: Page, account: { login: string; password: string }) {
  await page.getByTestId('login-name').fill(account.login);
  await page.getByTestId('login-password').fill(account.password);
  await page.getByTestId('login-submit').click();
  await expect(page.getByTestId('user-menu')).toBeVisible();
}

/** The id of the cell where the player's token stands (the map in words, read by tests and screen readers) */
export async function cellOf(page: Page, name: string): Promise<string | null> {
  return page
    .getByTestId('cells')
    .locator('[data-testid^="token-"]', { hasText: name })
    .locator('xpath=ancestor::li[1]')
    .getAttribute('data-testid');
}

/** The player's row of the leaderboard as a sentence: «2. Вася: 7 очк., до финиша 53 кл.» */
export function leaderRow(page: Page, playerId: string) {
  return page.getByTestId(`leader-${playerId}`).getByTestId('leader-sentence');
}

/**
 * Opens an admin section on the test's season: an admin's page opens the newest season, which a parallel test may have
 * created, so the season is picked in «Сезон» first; the sections are then opened from the menu, keeping the pick.
 */
export async function openAdmin(page: Page, world: World, section: string) {
  await page.goto('/admin/season');
  await expect(page.getByTestId('season-list')).toBeVisible();
  const open = page.getByTestId(`season-open-${world.seasonId}`);
  if (await open.isVisible()) await open.click();
  await expect(page.getByTestId('season-status')).toContainText(world.seasonName);
  if (section !== 'season') await goToAdminSection(page, section);
}

/** Another admin section from the menu (a side column on a desktop, a sheet on a phone) */
export async function goToAdminSection(page: Page, section: string) {
  if (!(await page.getByTestId(`admin-nav-${section}`).isVisible()))
    await page.getByTestId('admin-sections').click();
  await page.getByTestId(`admin-nav-${section}`).click();
  // The proof queue is the admin's first page: /admin
  await expect(page).toHaveURL(new RegExp(section === 'proofs' ? '/admin$' : `/admin/${section}$`));
}

/** Moves the site's clock forward (test endpoint, admin only); never back: parallel tests share the clock (D-230) */
export async function advanceClock(admin: Api, minutes: number) {
  await admin.post('/api/test/clock', { advanceMinutes: minutes });
}

/** Rolls and starts a game through the API (setup): the game's title */
export async function rollAndStart(player: Api, seasonId: string): Promise<string> {
  await player.post(`/api/seasons/${seasonId}/roll`);
  let view = await seasonOf(player, seasonId);
  const choice = view.me?.choice;
  if (choice) {
    const option = choice.options.find((o) => o.game);
    if (!option) throw new Error('The choice has no game.');
    await player.post(`/api/seasons/${seasonId}/choose`, {
      choiceId: choice.id,
      optionId: option.id,
    });
  }
  await player.post(`/api/seasons/${seasonId}/start`);
  view = await seasonOf(player, seasonId);
  const run = view.me?.activeRun;
  if (!run) throw new Error('No active run after the start.');
  return run.game.title;
}

/** Completes the active run through the API (setup), with an estimate when the game has no hours */
export async function complete(
  player: Api,
  seasonId: string,
  difficulty: Schemas['Difficulty'] = 'normal',
) {
  const view = await seasonOf(player, seasonId);
  const hours = view.me?.activeRun?.game.hours ?? null;
  return player.post(`/api/seasons/${seasonId}/complete`, {
    difficulty,
    ...(hours === null ? { estimatedHours: 5, hoursSource: 'HowLongToBeat' } : {}),
  });
}

/** Rolls and starts a game on the season's screen: the game's title */
export async function rollAndStartHere(page: Page): Promise<string> {
  await page.getByTestId('roll').click();
  await expect(page.getByTestId('offer').or(page.getByTestId('choice'))).toBeVisible();
  if (await page.getByTestId('choice').isVisible())
    await page.getByTestId('choice').locator('[data-testid^="option-"]').first().click();
  await expect(page.getByTestId('offer')).toBeVisible();
  const title = (await page.locator('#roll-result-title').textContent()) ?? '';
  await page.getByTestId('start').click();
  await expect(page.getByTestId('active-run')).toBeVisible();
  return title.trim();
}

/** Completes the active run with the completion form (with an estimate when the game has no hours) */
export async function completeHere(page: Page, difficulty = 'Нормальная') {
  // A game without pool hours needs an estimate with its source (D-96)
  const hours = page.getByTestId('complete-hours');
  if (await hours.isVisible()) {
    await hours.fill('5');
    await page.getByTestId('complete-hours-source').fill('HowLongToBeat');
  }
  // The difficulty is a row of radio pills (H4): the pill's label is what takes the tap
  await page.getByTestId('complete-difficulty').getByText(difficulty, { exact: true }).click();
  await expect(
    page.getByTestId('complete-difficulty').getByRole('radio', { name: difficulty }),
  ).toBeChecked();
  await page.getByTestId('complete-submit').click();
  await expect(page.getByTestId('last-dice')).toBeVisible();
}

/** The confirm button of the open danger dialog (ConfirmDanger without a test id of its own) */
export function confirmButton(page: Page, name: string) {
  return page.getByRole('alertdialog').getByRole('button', { name, exact: true });
}

export { commandId };
