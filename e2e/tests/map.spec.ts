import { expect, request, test, type APIRequestContext } from '@playwright/test';

// 2.10: a completion walks the token to a fork, the player picks the branch that is not the default one, and the
// token goes on along it. The test plays in a season of its own on the graph map, with a player of its own, so the
// other tests of the shared development seed never see it (a graph season never goes back to linear, D-301).
const password = 'dev-password';

type Api = { context: APIRequestContext; csrf: string };

async function csrfOf(context: APIRequestContext): Promise<string> {
  const token = (await (await context.get('/api/auth/antiforgery')).json()) as { token: string };
  return token.token;
}

async function signIn(baseURL: string, login: string, secret: string): Promise<Api> {
  const context = await request.newContext({ baseURL, ignoreHTTPSErrors: true });
  // Every POST carries the antiforgery token, the sign-in too; a signed-in user gets a new one
  const signedIn = await context.post('/api/auth/login', {
    data: { login, password: secret },
    headers: { 'X-CSRF-TOKEN': await csrfOf(context) },
  });
  expect(signedIn.ok(), await signedIn.text()).toBe(true);
  return { context, csrf: await csrfOf(context) };
}

async function send(api: Api, method: 'post' | 'put', url: string, data: object) {
  const response = await api.context[method](url, {
    data: { commandId: crypto.randomUUID(), ...data },
    headers: { 'X-CSRF-TOKEN': api.csrf },
  });
  expect(response.ok(), `${url}: ${await response.text()}`).toBe(true);
  return (await response.json()) as Record<string, unknown>;
}

/** start → fork, then two branches of twelve cells (a… the default, b…) that meet before the finish */
function forkMap() {
  const cells: object[] = [
    { id: 'start', type: 'start', x: 0, y: 200 },
    { id: 'fork', type: 'fork', x: 120, y: 200 },
  ];
  const edges: object[] = [
    { from: 'start', to: 'fork', isDefaultForward: true, isPrimaryBackward: true },
  ];
  for (const [branch, y] of [
    ['a', 80],
    ['b', 320],
  ] as const) {
    let previous = 'fork';
    for (let i = 1; i <= 12; i++) {
      const id = `${branch}${String(i)}`;
      cells.push({ id, type: 'empty', x: 120 + i * 110, y });
      edges.push({
        from: previous,
        to: id,
        isDefaultForward: previous !== 'fork' || branch === 'a',
        isPrimaryBackward: true,
      });
      previous = id;
    }
    edges.push({
      from: previous,
      to: 'finish',
      isDefaultForward: true,
      isPrimaryBackward: branch === 'a',
    });
  }
  cells.push({ id: 'finish', type: 'finish', x: 1560, y: 200 });
  return { cells, edges, zones: [] };
}

test('a completion stops at the fork, the player picks a branch and the token walks it', async ({
  page,
  baseURL,
}, testInfo) => {
  const site = baseURL ?? 'http://localhost:5080';
  const admin = await signIn(site, 'admin', password);

  // A player of its own: created by the admin, the temporary password changed once
  const login = `map${testInfo.project.name.slice(0, 5)}${String(Date.now() % 1_000_000)}`;
  const created = await send(admin, 'post', '/api/admin/accounts', {
    login,
    name: 'Картограф',
    role: 'player',
  });
  const account = created['account'] as { id: string };
  const temporary = created['temporaryPassword'] as string;
  const player = await signIn(site, login, temporary);
  await send(player, 'post', '/api/auth/password', {
    currentPassword: temporary,
    newPassword: password,
  });

  // A season of its own on the graph map: three dice a completion, so a throw always passes the fork
  const seasonId = crypto.randomUUID();
  await send(admin, 'post', '/api/admin/seasons', { seasonId, name: 'Развилка E2E' });
  const rules = (await (await admin.context.get(`/api/seasons/${seasonId}/rules`)).json()) as {
    version: number;
    ruleset: {
      features: { mapMode: string };
      reward: { diceCount: { min: number; max: number } };
    };
  };
  rules.ruleset.features.mapMode = 'graph';
  rules.ruleset.reward.diceCount.min = 3;
  rules.ruleset.reward.diceCount.max = 3;
  await send(admin, 'put', `/api/admin/seasons/${seasonId}/rules`, {
    expectedVersion: rules.version,
    ruleset: rules.ruleset,
  });
  await send(admin, 'post', `/api/admin/seasons/${seasonId}/map/publish`, {
    map: forkMap(),
    comment: 'Карта с развилкой',
  });
  await send(admin, 'post', `/api/admin/seasons/${seasonId}/players`, { userId: account.id });
  await send(admin, 'post', `/api/admin/seasons/${seasonId}/status`, { to: 'active' });

  // The player plays one game in the browser
  await page.goto('/');
  await page.getByTestId('login-name').fill(login);
  await page.getByTestId('login-password').fill(password);
  await page.getByTestId('login-submit').click();
  await expect(page.getByRole('heading', { level: 1, name: 'Развилка E2E' })).toBeVisible();
  await page.getByTestId('roll').click();
  await expect(page.getByTestId('offer')).toBeVisible();
  await page.getByTestId('start').click();
  await expect(page.getByTestId('active-run')).toBeVisible();
  const hours = page.getByTestId('complete-hours');
  if (await hours.isVisible()) {
    await hours.fill('5');
    await page.getByTestId('complete-hours-source').fill('HowLongToBeat');
  }
  await page.getByTestId('complete-difficulty').getByText('Нормальная', { exact: true }).click();
  await page.getByTestId('complete-submit').click();

  // The token stands on the fork and the page asks for the branch: in the turn card on a phone, on the map on a desktop
  const branch = page.getByTestId('branch');
  await expect(branch).toBeVisible();
  if (testInfo.project.name === 'phone')
    await expect(page.getByTestId('turn').getByTestId('branch')).toBeVisible();
  else await expect(page.getByTestId('branch-hint')).toBeVisible();
  await expect(page.getByTestId('cell-fork').getByText('Картограф')).toHaveCount(1);
  await expect(page.getByTestId('roll')).toHaveCount(0);

  // The branch that is not the default one, picked with the keyboard
  await branch.getByTestId('branch-b1').focus();
  await page.keyboard.press('Enter');

  await expect(branch).toHaveCount(0);
  await expect(page.getByTestId('roll')).toBeVisible();
  const cell = await page
    .getByTestId('cells')
    .locator('[data-testid^="token-"]', { hasText: 'Картограф' })
    .locator('xpath=ancestor::li[1]')
    .getAttribute('data-testid');
  expect(cell).toMatch(/^cell-b\d+$/);

  await admin.context.dispose();
  await player.context.dispose();
});
