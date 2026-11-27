import { fileURLToPath } from 'node:url';
import { expect, test, type Locator, type Page } from '@playwright/test';
import type { Schemas } from '../support/api.ts';
import { apiOf, complete, live, rollAndStart, setUpSeason, signIn } from '../support/world.ts';
import type { World } from '../support/world.ts';

async function changeRules(
  world: World,
  change: (rules: Schemas['Ruleset']) => Schemas['Ruleset'],
) {
  const rules = await world.adminApi.get<Schemas['RulesView']>(
    `/api/seasons/${world.seasonId}/rules`,
  );
  await world.adminApi.put(`/api/admin/seasons/${world.seasonId}/rules`, {
    expectedVersion: rules.version,
    ruleset: change(rules.ruleset),
  });
}

function setFeatures(world: World, changes: Partial<Schemas['Features']>) {
  return changeRules(world, (rules) => ({
    ...rules,
    features: { ...rules.features, ...changes },
  }));
}

async function designShot(page: Page, name: string) {
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(
    true,
  );
  await page.evaluate(() => document.fonts.ready);
  await page.screenshot({
    path: fileURLToPath(new URL(`../../test-artifacts/design/${name}.png`, import.meta.url)),
    fullPage: true,
  });
}

function activate(control: Locator, phone: boolean) {
  return phone ? control.tap() : control.click();
}

function pollScreenLoads(page: Page) {
  return page.evaluate(
    () =>
      performance
        .getEntriesByType('resource')
        .filter((entry) => /\/PollsScreen(?:-[^/]+\.js|\.tsx)$/.test(new URL(entry.name).pathname))
        .length,
  );
}

test('an anonymous poll shows each player their own vote and reveals the result live at closure', async ({
  browser,
}, info) => {
  const phone = info.project.name === 'phone';
  const world = await setUpSeason({ players: ['Лена', 'Петя'], start: false });
  const [lena, petya] = world.players;
  if (!lena || !petya) throw new Error('No players.');

  const admin = `/api/admin/seasons/${world.seasonId}`;
  const question = 'Какой момент недели запомнился?';
  const finish = 'Финиш на последнем кубике';
  const roll = 'Неожиданная игра с колеса';
  await setFeatures(world, { polls: true });
  await world.adminApi.post(`${admin}/status`, { to: 'active' });
  await world.adminApi.post(`${admin}/polls`, {
    poll: {
      question,
      options: { list: [finish, roll] },
      anonymous: true,
      showLiveCounts: false,
      voters: 'players',
      closesInHours: 168,
    },
  });
  const [poll] = await world.adminApi.get<Schemas['PollView'][]>(
    `/api/seasons/${world.seasonId}/polls`,
  );
  if (!poll) throw new Error('No poll after starting one.');

  const first = await signIn(browser, lena);
  await expect(first.getByRole('heading', { level: 1, name: world.seasonName })).toBeVisible();
  const link = first.getByTestId('nav-polls').first();
  const explicit = `/seasons/${world.seasonId}/polls`;
  await expect(link).toHaveAttribute('href', explicit);
  expect(await pollScreenLoads(first)).toBe(0);
  const nav = first.getByTestId('site-nav');
  await first.evaluate(() => document.fonts.ready);
  expect(await nav.evaluate((element) => element.scrollWidth <= element.clientWidth)).toBe(true);
  await activate(link, phone);
  await expect(first).toHaveURL(new RegExp(`${explicit}$`));
  await expect(first.getByRole('heading', { level: 1, name: 'Голосования' })).toBeVisible();
  await expect.poll(() => pollScreenLoads(first)).toBeGreaterThan(0);
  const firstCard = first.getByRole('article', { name: question });
  await expect(firstCard.getByRole('heading', { level: 3, name: question })).toBeVisible();
  const firstSubmit = firstCard.getByRole('button', { name: 'Голосовать', exact: true });
  await expect(firstSubmit).toBeDisabled();
  await activate(firstCard.getByText(finish, { exact: true }), phone);
  await expect(firstCard.getByRole('radio', { name: finish })).toBeChecked();
  await activate(firstSubmit, phone);
  await expect(firstCard.getByText(`Твой голос: ${finish}`)).toBeVisible(live);

  const second = await signIn(browser, petya);
  await second.goto('/polls');
  const secondCard = second.getByRole('article', { name: question });
  await expect(secondCard.getByText('Анонимно')).toBeVisible();
  await expect(secondCard.getByText('Счёт скрыт до закрытия')).toBeVisible();
  await expect(secondCard.getByText(`Твой голос: ${finish}`)).toHaveCount(0);
  await expect(secondCard.getByRole('radio', { name: finish })).not.toBeChecked();

  await activate(secondCard.getByText(roll, { exact: true }), phone);
  await activate(secondCard.getByRole('button', { name: 'Голосовать', exact: true }), phone);
  await expect(secondCard.getByText(`Твой голос: ${roll}`)).toBeVisible(live);
  await expect(secondCard.getByRole('radio', { name: roll })).not.toHaveAttribute(
    'aria-describedby',
  );
  await expect(firstCard.getByText(`Твой голос: ${finish}`)).toBeVisible();
  await designShot(second, `s5-22-polls-open-${info.project.name}`);

  await activate(secondCard.getByText(finish, { exact: true }), phone);
  await activate(secondCard.getByRole('button', { name: 'Изменить голос' }), phone);
  await expect(secondCard.getByText(`Твой голос: ${finish}`)).toBeVisible(live);
  await expect(firstCard.getByText('Счёт скрыт до закрытия')).toBeVisible();
  await expect(firstCard.getByRole('radio', { name: finish })).not.toHaveAttribute(
    'aria-describedby',
  );

  // Both pages stay open while the admin closes the poll. The changed vote replaces the earlier one.
  await world.adminApi.post(`${admin}/polls/${poll.id}/close`);
  for (const card of [firstCard, secondCard]) {
    await expect(card.getByText('Голосование закрыто')).toBeVisible(live);
    await expect(card.getByText(`Итог: ${finish}`)).toBeVisible();
    await expect(card.getByRole('radio', { name: finish })).toHaveAccessibleDescription('2 голоса');
    await expect(card.getByRole('radio', { name: roll })).toHaveAccessibleDescription('0 голосов');
    await expect(card.getByRole('button', { name: 'Изменить голос' })).toHaveCount(0);
  }
  await designShot(second, `s5-22-polls-closed-${info.project.name}`);

  await setFeatures(world, { polls: false });
  await expect(first.getByRole('heading', { name: 'Голосования выключены' })).toBeVisible(live);
  await expect(first.getByTestId('nav-polls')).toHaveCount(0, live);
  await expect(firstCard).toHaveCount(0);
});

test('an unfinished vote moves to ended without revealing the result when the season finishes', async ({
  browser,
}, info) => {
  const phone = info.project.name === 'phone';
  const world = await setUpSeason({ players: ['Лена'], start: false });
  const [player] = world.players;
  if (!player) throw new Error('No player.');
  const admin = `/api/admin/seasons/${world.seasonId}`;
  const question = 'Какой момент сезона обсудим?';
  const firstChoice = 'Финиш на последнем кубике';
  const secondChoice = 'Неожиданная игра с колеса';
  await setFeatures(world, { polls: true });
  await world.adminApi.post(`${admin}/status`, { to: 'active' });
  await world.adminApi.post(`${admin}/polls`, {
    poll: {
      question,
      options: { list: [firstChoice, secondChoice] },
      anonymous: true,
      showLiveCounts: false,
      voters: 'players',
      closesInHours: 168,
    },
  });

  const page = await signIn(browser, player);
  await page.goto(`/seasons/${world.seasonId}/polls`);
  const card = page.getByRole('article', { name: question });
  await expect(page.getByRole('region', { name: 'Открытые' }).getByRole('article')).toHaveCount(1);
  await activate(card.getByText(firstChoice, { exact: true }), phone);
  await activate(card.getByRole('button', { name: 'Голосовать', exact: true }), phone);
  await expect(card.getByText(`Твой голос: ${firstChoice}`)).toBeVisible(live);
  await expect(card.getByText('Счёт скрыт до закрытия')).toBeVisible();
  const savedVote = card.getByRole('radio', { name: firstChoice });
  const unsentVote = card.getByRole('radio', { name: secondChoice });
  await expect(savedVote).toBeEnabled(live);
  await savedVote.press('ArrowRight');
  await expect(unsentVote).toBeFocused();

  await world.adminApi.post(`${admin}/status`, { to: 'closing' });
  await world.adminApi.post(`${admin}/status`, { to: 'finished' });

  const finished = page.getByRole('region', { name: 'Завершённые' });
  await expect(finished.getByRole('article', { name: question })).toBeVisible(live);
  await expect(page.getByRole('region', { name: 'Открытые' })).toHaveCount(0);
  await expect(card.getByText('Сезон завершён без итога голосования.')).toBeVisible();
  await expect(card.getByText(`Твой голос: ${firstChoice}`)).toBeVisible();
  await expect(savedVote).toBeChecked();
  await expect(unsentVote).toBeDisabled();
  await expect(card.getByText('Счёт скрыт до закрытия')).toHaveCount(0);
  await expect(card.getByText(/^Итог:/)).toHaveCount(0);
  const heading = card.getByRole('heading', { name: question });
  await expect(heading).toBeFocused();
  expect(await heading.evaluate((element) => element.matches(':focus-visible'))).toBe(true);
  expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBeLessThanOrEqual(
    phone ? 390 : 1440,
  );
  await page.evaluate(() => document.fonts.ready);
  await page.screenshot({
    path: info.outputPath(`poll-without-result-${info.project.name}.png`),
    scale: 'css',
  });

  await world.adminApi.post(`${admin}/status`, { to: 'archived' });
  await page.reload();
  await expect(finished.getByRole('article', { name: question })).toBeVisible();
  await expect(card.getByText('Сезон завершён без итога голосования.')).toBeVisible();
});

test('a spectator joins an everyone poll after a rule change and votes only once across tabs', async ({
  browser,
}, info) => {
  const phone = info.project.name === 'phone';
  const world = await setUpSeason({ players: ['Лена', 'Петя'], spectator: true, start: false });
  const [lena, petya] = world.players;
  const spectator = world.spectator;
  if (!lena || !petya || !spectator) throw new Error('The poll needs two players and a spectator.');

  const season = `/api/seasons/${world.seasonId}`;
  const admin = `/api/admin/seasons/${world.seasonId}`;
  const question = 'Какую игру обсудим после сезона?';
  const playersQuestion = 'Какой ролл удивил участников?';
  const portal = 'Portal 2';
  const hades = 'Hades';
  const notVoter = 'В этом голосовании голосуют только игроки сезона.';
  await changeRules(world, (rules) => ({
    ...rules,
    features: { ...rules.features, polls: true },
    social: { ...rules.social, spectatorsCanVote: false },
  }));
  await world.adminApi.post(`${admin}/status`, { to: 'active' });
  for (const [prompt, voters] of [
    [question, 'everyone'],
    [playersQuestion, 'players'],
  ] as const) {
    await world.adminApi.post(`${admin}/polls`, {
      poll: {
        question: prompt,
        options: { list: [portal, hades] },
        anonymous: true,
        showLiveCounts: false,
        voters,
        closesInHours: 168,
      },
    });
  }
  const polls = await world.adminApi.get<Schemas['PollView'][]>(`${season}/polls`);
  const poll = polls.find((entry) => entry.question === question);
  if (!poll) throw new Error('No everyone poll after starting one.');

  const [first, second, viewer] = await Promise.all([
    signIn(browser, lena),
    signIn(browser, petya),
    signIn(browser, spectator),
  ]);
  const scope = `/seasons/${world.seasonId}/polls`;
  // The spectator's default season may change while another worker creates a season.
  await Promise.all([first.goto(scope), second.goto(scope), viewer.goto(scope)]);
  const everyoneCard = (page: Page) => page.getByRole('article', { name: question, exact: true });
  const playersOnly = viewer.getByRole('article', { name: playersQuestion, exact: true });
  const viewerCard = everyoneCard(viewer);
  const submit = (card: Locator) => card.getByRole('button', { name: 'Голосовать', exact: true });
  await expect(viewerCard.getByText(notVoter)).toBeVisible();
  await expect(submit(viewerCard)).toHaveCount(0);
  await expect(playersOnly.getByText(notVoter)).toBeVisible();

  for (const [page, choice] of [
    [first, portal],
    [second, hades],
  ] as const) {
    const card = everyoneCard(page);
    await activate(card.getByText(choice, { exact: true }), phone);
    await activate(submit(card), phone);
    await expect(card.getByText(`Твой голос: ${choice}`)).toBeVisible(live);
  }
  await expect(viewerCard.getByText('Счёт скрыт до закрытия')).toBeVisible();
  await expect(viewerCard.getByText(/^Твой голос:/u)).toHaveCount(0);

  await changeRules(world, (rules) => ({
    ...rules,
    social: { ...rules.social, spectatorsCanVote: true },
  }));
  await expect(submit(viewerCard)).toBeVisible(live);
  await expect(playersOnly.getByText(notVoter)).toBeVisible();
  await expect(submit(playersOnly)).toHaveCount(0);

  const otherTab = await viewer.context().newPage();
  await otherTab.goto(scope);
  const voteUrl = new URL(`${season}/polls/${poll.id}/vote`, viewer.url()).href;
  let releaseVotes: (() => void) | undefined;
  const bothSubmitted = new Promise<void>((resolve) => {
    releaseVotes = resolve;
  });
  let submitted = 0;
  // Both real clicks reach the server before a refreshed view can disable either tab's submit button.
  await viewer.context().route(voteUrl, async (route) => {
    if (++submitted === 2) releaseVotes?.();
    await bothSubmitted;
    await route.continue();
  });
  try {
    const otherCard = everyoneCard(otherTab);
    for (const card of [viewerCard, otherCard])
      await activate(card.getByText(hades, { exact: true }), phone);
    const [fromViewer, fromOther] = await Promise.all([
      viewer.waitForResponse(voteUrl),
      otherTab.waitForResponse(voteUrl),
      activate(submit(viewerCard), phone),
      activate(submit(otherCard), phone),
    ]);
    expect([fromViewer.status(), fromOther.status()].sort()).toEqual([200, 409]);
  } finally {
    releaseVotes?.();
    await viewer.context().unroute(voteUrl);
  }

  await world.adminApi.post(`${admin}/polls/${poll.id}/close`);
  for (const page of [first, second, viewer, otherTab]) {
    const card = everyoneCard(page);
    await expect(card.getByText('Голосование закрыто')).toBeVisible(live);
    await expect(card.getByText(`Итог: ${hades}`)).toBeVisible();
    await expect(card.getByRole('radio', { name: portal })).toHaveAccessibleDescription('1 голос');
    await expect(card.getByRole('radio', { name: hades })).toHaveAccessibleDescription('2 голоса');
  }
});

test('the season banner shows a global goal and follows real completions and early ending', async ({
  browser,
}, info) => {
  const world = await setUpSeason({ players: ['Проходящий', 'Наблюдатель'], start: false });
  const [player, watcher] = world.players;
  if (!player || !watcher) throw new Error('No players.');

  const name = 'Монетный дождь';
  const description = 'Всем по 2 монетки. Общая цель — пройти две игры.';
  const objectId = 'coin-rain';
  const season = `/api/seasons/${world.seasonId}`;
  const admin = `/api/admin/seasons/${world.seasonId}`;
  await setFeatures(world, { items: true, events: true, globalEvents: true });
  await world.adminApi.post(`/api/test/seasons/${world.seasonId}/content`, {
    objects: [
      JSON.stringify({
        id: objectId,
        kind: 'event',
        name,
        description,
        rarity: 'common',
        effect: {
          target: { selector: 'all' },
          actions: [{ type: 'changeResource', resource: 'coins', amount: 2 }],
        },
      }),
    ],
    wheels: [],
    comment: 'Монетный дождь для сквозной проверки',
  });
  await world.adminApi.post(`${admin}/status`, { to: 'active' });
  const runner = await apiOf(player);
  try {
    await rollAndStart(runner, world.seasonId);

    const page = await signIn(browser, watcher);
    await expect(page.getByRole('heading', { level: 1, name: world.seasonName })).toBeVisible();
    const banner = page.getByRole('region', { name, exact: true });
    await expect(banner).toHaveCount(0);

    await world.adminApi.post(`${admin}/global-events`, {
      objectId,
      durationHours: 168,
      goal: { metric: 'runs', target: 2 },
    });
    await expect(banner.getByText(description)).toBeVisible(live);
    const progress = banner.getByRole('progressbar', { name: `Общая цель: ${name}` });
    await expect(progress).toHaveAttribute('aria-valuetext', '0 из 2 прохождений');
    await expect(progress).toHaveAttribute('aria-valuenow', '0');

    await complete(runner, world.seasonId);
    await expect(progress).toHaveAttribute('aria-valuetext', '1 из 2 прохождений', live);
    await expect(progress).toHaveAttribute('aria-valuenow', '50');
    await designShot(page, `s5-22-global-goal-${info.project.name}`);

    const [event] = await world.adminApi.get<Schemas['GlobalEventView'][]>(
      `${season}/global-events`,
    );
    if (!event) throw new Error('No event after launching one.');
    await world.adminApi.post(`${admin}/global-events/${event.id}/end`);
    await expect(banner).toHaveCount(0, live);
  } finally {
    await runner.dispose();
  }
});
