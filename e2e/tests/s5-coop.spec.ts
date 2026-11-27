import { expect, test, type Page } from '@playwright/test';
import type { Schemas } from '../support/api.ts';
import { coopGameTitle, coopTag } from '../support/coopFixture.ts';
import {
  adminRefresh,
  apiOf,
  cellOf,
  commandsOf,
  completeHere,
  confirmButton,
  fillCompletionHere,
  leaderRow,
  live,
  openAdmin,
  points,
  rollAndStartHere,
  rollHere,
  seasonOf,
  setUpSeason,
  signIn,
} from '../support/world.ts';

/** One eligible game at the start and enough empty cells for either share of the roll. */
function coopMap(): Schemas['MapGraphView'] {
  const path = ['start', ...Array.from({ length: 11 }, (_, i) => `c${String(i + 1)}`), 'finish'];
  const cells: Schemas['CellView'][] = path.map((id, i) => ({
    id,
    type: id === 'start' || id === 'finish' ? id : 'empty',
    ...(id === 'start' ? { zone: 'coop-start' } : {}),
    x: i * 110,
    y: 200,
  }));
  const edges: Schemas['EdgeView'][] = cells.slice(0, -1).map((cell, i) => ({
    from: cell.id,
    to: cells[i + 1]?.id ?? 'finish',
    isDefaultForward: true,
    isPrimaryBackward: true,
  }));
  return {
    cells,
    edges,
    zones: [{ id: 'coop-start', name: 'Кооп на старте', rollFilter: { tags: [coopTag] } }],
  };
}

async function setupCoopSeason(partnerBoard = false, runConditions = false) {
  const world = await setUpSeason({ players: ['Лена', 'Петя'], start: false });
  const [roller, partner] = world.players;
  if (!roller || !partner) throw new Error('Two season players are required.');
  const season = `/api/seasons/${world.seasonId}`;
  const admin = `/api/admin/seasons/${world.seasonId}`;
  const rules = await world.adminApi.get<Schemas['RulesView']>(`${season}/rules`);
  const ruleset: Schemas['Ruleset'] = {
    ...rules.ruleset,
    features: {
      ...rules.ruleset.features,
      mapMode: 'graph',
      coop: true,
      partnerBoard,
      ...(runConditions ? { items: true, events: true } : {}),
    },
    map: { ...rules.ruleset.map, minZoneGames: 1 },
    reward: {
      ...rules.ruleset.reward,
      diceCount: { ...rules.ruleset.reward.diceCount, hoursPerDie: 3, min: 2, max: 2 },
      coop: { ...rules.ruleset.reward.coop, pointsShare: 0.5, coinsShare: 0.5 },
      // The two dice cap the hours counted for coins at six. An odd minimum exercises rounding.
      coins: { perHour: 1, min: 9 },
    },
    // Leave room for the shared clock to move in other scenarios. The board closes first.
    interactions: { ...rules.ruleset.interactions, inviteTtlHours: 24 * 23 },
    partnerBoard: { requestTtlHours: 24 * 11 },
  };
  await world.adminApi.put(`${admin}/rules`, { expectedVersion: rules.version, ruleset });
  await world.adminApi.post(`${admin}/map/publish`, { map: coopMap(), comment: 'Кооп-карта' });
  if (runConditions)
    await world.adminApi.post(`/api/test/seasons/${world.seasonId}/content`, {
      objects: [
        ['coop-hard-condition', 'Сложная кооп-игра', { difficultyAtLeast: 'hard' }],
        ['coop-easy-condition', 'Лёгкая будущая игра', { difficultyAtMost: 'easy' }],
      ].map(([id, name, runCondition]) =>
        JSON.stringify({
          id,
          name,
          kind: 'item',
          description: 'Условие следующего прохождения.',
          rarity: 'common',
          price: null,
          window: 'anytime',
          hostile: false,
          stackable: false,
          effect: {
            target: { selector: 'self' },
            actions: [{ type: 'modifyNextRoll', runCondition }],
          },
        }),
      ),
      wheels: [],
      comment: 'Разные условия роллившего и напарника',
    });
  await world.adminApi.post(`${admin}/status`, { to: 'active' });
  return { world, roller, partner, season };
}

async function openInteractions(page: Page, seasonId: string) {
  const link = page.getByTestId('season-interactions');
  await expect(link).toHaveAttribute('href', `/seasons/${seasonId}/interactions`);
  await link.click();
  await expect(page.getByRole('heading', { name: 'Напарники и дуэли', level: 1 })).toBeVisible();
}

test('two players complete one coop run, receive their shares and lose both shares after proof rejection', async ({
  browser,
}) => {
  const { world, roller, partner } = await setupCoopSeason();
  const rollerPage = await signIn(browser, roller);
  const partnerPage = await signIn(browser, partner);
  const [rollerApi, partnerApi] = await Promise.all([apiOf(roller), apiOf(partner)]);

  try {
    expect(await rollAndStartHere(rollerPage)).toBe(coopGameTitle);
    const run = (await seasonOf(rollerApi, world.seasonId)).me?.activeRun;
    if (!run) throw new Error('The roller has no active run.');

    await openInteractions(rollerPage, world.seasonId);
    await openInteractions(partnerPage, world.seasonId);
    await rollerPage.getByLabel('Напарник', { exact: true }).selectOption(partner.playerId);
    await rollerPage.getByRole('button', { name: 'Пригласить в кооп', exact: true }).click();
    const incoming = partnerPage.getByRole('region', { name: 'Приглашения', exact: true });
    await expect(incoming.getByText('От кого: Лена', { exact: true })).toBeVisible(live);
    await incoming.getByRole('button', { name: 'Принять', exact: true }).click();
    await expect(rollerPage.getByText('Твой напарник — Петя.')).toBeVisible(live);

    await partnerPage.getByTestId('nav-season').click();
    await expect(partnerPage.getByTestId('active-run')).toContainText(coopGameTitle, live);
    await expect(partnerPage.getByTestId('active-run')).toContainText(
      'Завершение прохождения отмечает роллер.',
    );
    expect((await seasonOf(partnerApi, world.seasonId)).me).toMatchObject({
      activeRun: { id: run.id },
      isCoopPartner: true,
    });
    for (const action of ['roll', 'complete-open', 'complete-submit', 'drop', 'tech-reroll']) {
      await expect(partnerPage.getByTestId(action)).toHaveCount(0);
    }

    await rollerPage.getByTestId('nav-season').click();
    await completeHere(rollerPage);
    const result = (await seasonOf(rollerApi, world.seasonId)).me?.lastCompleted;
    if (!result) throw new Error('The shared run has no completion result.');
    expect(result.id).toBe(run.id);
    expect(result.dice).toHaveLength(2);
    expect(result.total).toBe(result.dice.reduce((sum, die) => sum + die.value, 0));
    const rollerPoints = Math.ceil(result.total / 2);
    const partnerPoints = result.total - rollerPoints;
    const events = (await commandsOf(world)).flatMap((command) => command.events);
    expect(events.filter((event) => event === 'run-completed')).toHaveLength(1);
    expect(events.filter((event) => event === 'coop-reward-split')).toHaveLength(1);

    await expect(leaderRow(rollerPage, roller.playerId)).toContainText(points(rollerPoints), live);
    await expect(leaderRow(partnerPage, partner.playerId)).toContainText(
      points(partnerPoints),
      live,
    );
    await expect
      .poll(() => cellOf(rollerPage, roller.name), live)
      .toBe(`cell-c${String(rollerPoints)}`);
    await expect
      .poll(() => cellOf(partnerPage, partner.name), live)
      .toBe(`cell-c${String(partnerPoints)}`);
    await expect(rollerPage.getByTestId('my-coins')).toHaveText('У тебя 5 монеток', live);
    await expect(partnerPage.getByTestId('my-coins')).toHaveText('У тебя 4 монетки', live);
    await expect(partnerPage.getByTestId('proof-form')).toHaveCount(0);

    const proof = rollerPage.getByTestId('proof-details');
    await proof.locator('summary').click();
    await proof.getByTestId('proof-link').fill('https://example.com/coop-credits.png');
    await proof.getByTestId('proof-submit').click();
    await expect(proof.getByTestId('proof-status')).toContainText('Пруф ждёт проверки админом.');

    const adminPage = await signIn(browser, world.admin);
    await openAdmin(adminPage, world, 'proofs');
    const card = adminPage.getByTestId(`proof-${run.id}`);
    await expect(card).toContainText(coopGameTitle, adminRefresh);
    await card.getByTestId('reject').click();
    await adminPage.getByTestId('reject-comment').fill('На видео не показано завершение');
    await confirmButton(adminPage, 'Отклонить прохождение').click();
    await expect(adminPage.getByTestId('proof-queue')).toContainText(`Отклонено: ${coopGameTitle}`);

    await expect(proof.getByTestId('proof-status')).toContainText('Пруф отклонён.', live);
    for (const [page, account] of [
      [rollerPage, roller],
      [partnerPage, partner],
    ] as const) {
      await expect(leaderRow(page, account.playerId)).toContainText(
        `${account.name}: 0 очков`,
        live,
      );
      await expect.poll(() => cellOf(page, account.name), live).toBe('cell-start');
      await expect(page.getByTestId('my-coins')).toHaveText('У тебя 0 монеток', live);
    }
    expect((await seasonOf(rollerApi, world.seasonId)).me?.lastCompleted?.status).toBe('rejected');
  } finally {
    await Promise.all([rollerApi.dispose(), partnerApi.dispose(), world.adminApi.dispose()]);
  }
});

for (const { difficulty, label, met } of [
  { difficulty: 'hard', label: 'Сложная', met: true },
  { difficulty: 'normal', label: 'Нормальная', met: false },
] as const)
  test(`a coop uses the roller's nonempty conditions when the shared run ${met ? 'meets' : 'misses'} them`, async ({
    browser,
  }) => {
    const { world, roller, partner, season } = await setupCoopSeason(false, true);
    const [rollerApi, partnerApi] = await Promise.all([apiOf(roller), apiOf(partner)]);
    const rollerPage = await signIn(browser, roller);
    const partnerPage = await signIn(browser, partner);

    try {
      for (const [account, api, objectId] of [
        [roller, rollerApi, 'coop-hard-condition'],
        [partner, partnerApi, 'coop-easy-condition'],
      ] as const) {
        await world.adminApi.post(
          `/api/admin/seasons/${world.seasonId}/players/${account.playerId}/inventory`,
          { giveObjectId: objectId, comment: 'Условие для проверки кооп-снапшота' },
        );
        const item = (await api.get<Schemas['EconomyView']>(`${season}/economy`)).items?.find(
          (candidate) => candidate.objectId === objectId,
        );
        if (!item) throw new Error(`The condition item ${objectId} was not granted.`);
        await api.post(`${season}/items/${item.instanceId}/use`);
      }

      expect((await seasonOf(rollerApi, world.seasonId)).me?.waitingConditions).toEqual([
        expect.objectContaining({ objectId: 'coop-hard-condition', difficultyAtLeast: 'hard' }),
      ]);
      expect((await seasonOf(partnerApi, world.seasonId)).me?.waitingConditions).toEqual([
        expect.objectContaining({ objectId: 'coop-easy-condition', difficultyAtMost: 'easy' }),
      ]);
      await expect(rollerPage.getByTestId('roll')).toBeEnabled(live);
      await rollHere(rollerPage);
      const offer = rollerPage.getByTestId('offer');
      await expect(offer).toBeVisible();
      await expect(offer).toContainText(coopGameTitle);
      await offer.getByText('Сложная', { exact: true }).click();
      await expect(offer.getByRole('radio', { name: 'Сложная', exact: true })).toBeChecked();
      await rollerPage.getByTestId('start').click();
      await expect(rollerPage.getByTestId('active-run')).toBeVisible(live);
      const run = (await seasonOf(rollerApi, world.seasonId)).me?.activeRun;
      if (!run) throw new Error('The roller has no active run.');
      expect(run.declaredDifficulty).toBe('hard');
      expect(run.conditions).toEqual([
        expect.objectContaining({ objectId: 'coop-hard-condition', difficultyAtLeast: 'hard' }),
      ]);

      await openInteractions(rollerPage, world.seasonId);
      await openInteractions(partnerPage, world.seasonId);
      await rollerPage.getByLabel('Напарник', { exact: true }).selectOption(partner.playerId);
      await rollerPage.getByRole('button', { name: 'Пригласить в кооп', exact: true }).click();
      await partnerPage
        .getByRole('region', { name: 'Приглашения', exact: true })
        .getByRole('button', { name: 'Принять', exact: true })
        .click();

      // Leaving Interactions cancels outstanding requests. Confirm that the invitation joined the run first.
      await expect
        .poll(async () => (await seasonOf(partnerApi, world.seasonId)).me?.activeRun?.id, live)
        .toBe(run.id);

      await partnerPage.getByTestId('nav-season').click();
      const partnerRun = partnerPage.getByTestId('active-run');
      await expect(partnerRun.getByText('Не ниже: Сложная', { exact: true })).toBeVisible(live);
      const joined = await seasonOf(partnerApi, world.seasonId);
      expect(joined.me?.activeRun).toMatchObject({ id: run.id, conditions: run.conditions });
      expect(joined.me?.waitingConditions).toEqual([
        expect.objectContaining({ objectId: 'coop-easy-condition', difficultyAtMost: 'easy' }),
      ]);

      await rollerPage.getByTestId('nav-season').click();
      await expect(
        rollerPage.getByTestId('active-run').getByText('Не ниже: Сложная', { exact: true }),
      ).toBeVisible(live);
      await fillCompletionHere(rollerPage, label);
      await rollerPage.getByTestId('complete-submit').click();
      await expect
        .poll(async () => (await seasonOf(rollerApi, world.seasonId)).me?.lastCompleted?.id, live)
        .toBe(run.id);

      const [rollerAfter, partnerAfter] = await Promise.all([
        seasonOf(rollerApi, world.seasonId),
        seasonOf(partnerApi, world.seasonId),
      ]);
      const result = rollerAfter.me?.lastCompleted;
      if (!result) throw new Error('The common completion is missing.');
      expect(result).toMatchObject({
        id: run.id,
        difficulty,
        conditions: run.conditions,
        conditionCheck: { met, waived: false },
      });
      await rollerApi.post(`${season}/runs/${run.id}/proof`, {
        links: ['https://example.com/coop-conditions.png'],
      });
      const sharedProofs = (
        await world.adminApi.get<Schemas['ProofQueueItemView'][]>(
          `/api/admin/seasons/${world.seasonId}/proofs`,
        )
      ).filter((candidate) => candidate.runId === run.id);
      expect(sharedProofs).toHaveLength(1);
      expect(sharedProofs[0]).toMatchObject({
        runId: run.id,
        playerId: roller.playerId,
        conditions: run.conditions,
        conditionCheck: { met, waived: false },
      });
      expect(result.dice).toHaveLength(met ? 2 : 0);
      expect(result.total).toBe(result.dice.reduce((sum, die) => sum + die.value, 0));
      if (met) expect(result.total).toBeGreaterThan(0);
      const rollerPoints = Math.ceil(result.total / 2);
      expect(rollerAfter.me?.coins).toBe(5);
      expect(partnerAfter.me?.coins).toBe(4);
      expect(
        rollerAfter.players.find((candidate) => candidate.id === roller.playerId)?.points,
      ).toBe(rollerPoints);
      expect(
        partnerAfter.players.find((candidate) => candidate.id === partner.playerId)?.points,
      ).toBe(result.total - rollerPoints);
      expect(rollerAfter.me?.waitingConditions ?? []).toEqual([]);
      expect(partnerAfter.me?.waitingConditions).toEqual([
        expect.objectContaining({ objectId: 'coop-easy-condition', difficultyAtMost: 'easy' }),
      ]);
      await expect(leaderRow(rollerPage, roller.playerId)).toContainText(
        points(rollerPoints),
        live,
      );
      await expect(leaderRow(partnerPage, partner.playerId)).toContainText(
        points(result.total - rollerPoints),
        live,
      );
      const events = (await commandsOf(world)).flatMap((command) => command.events);
      expect(events.filter((event) => event === 'run-completed')).toHaveLength(1);
      expect(events.filter((event) => event === 'run-condition-checked')).toHaveLength(1);
      expect(events.filter((event) => event === 'coop-reward-split')).toHaveLength(1);
    } finally {
      await Promise.all([
        rollerPage.context().close(),
        partnerPage.context().close(),
        rollerApi.dispose(),
        partnerApi.dispose(),
        world.adminApi.dispose(),
      ]);
    }
  });

test('deleting a joined coop partner preserves the shared run and its reward without restoring account access', async ({
  browser,
}) => {
  const { world, roller, partner, season } = await setupCoopSeason();
  const [rollerApi, partnerApi] = await Promise.all([apiOf(roller), apiOf(partner)]);
  const rollerPage = await signIn(browser, roller);
  const partnerPage = await signIn(browser, partner);

  try {
    expect(await rollAndStartHere(rollerPage)).toBe(coopGameTitle);
    const run = (await seasonOf(rollerApi, world.seasonId)).me?.activeRun;
    if (!run) throw new Error('The roller has no active run.');
    await openInteractions(rollerPage, world.seasonId);
    await openInteractions(partnerPage, world.seasonId);
    await rollerPage.getByLabel('Напарник', { exact: true }).selectOption(partner.playerId);
    await rollerPage.getByRole('button', { name: 'Пригласить в кооп', exact: true }).click();
    await partnerPage
      .getByRole('region', { name: 'Приглашения', exact: true })
      .getByRole('button', { name: 'Принять', exact: true })
      .click();
    await expect(rollerPage.getByText('Твой напарник — Петя.')).toBeVisible(live);
    const expectedCoop = { runId: run.id, rollerId: roller.playerId, partnerId: partner.playerId };
    expect(await rollerApi.get<Schemas['CoopView']>(`${season}/coop`)).toMatchObject(expectedCoop);

    const deletion = await world.adminApi.post<Schemas['AccountActionResponse']>(
      `/api/admin/accounts/${partner.id}/delete`,
    );
    expect(deletion.account).toMatchObject({ id: partner.id, isDeleted: true });
    expect((await partnerApi.send('GET', season)).status()).toBe(401);
    expect(await rollerApi.get<Schemas['CoopView']>(`${season}/coop`)).toMatchObject(expectedCoop);
    const afterDeletion = await seasonOf(rollerApi, world.seasonId);
    expect(afterDeletion.me?.activeRun?.id).toBe(run.id);
    expect(
      afterDeletion.players.find((candidate) => candidate.id === partner.playerId),
    ).toMatchObject({
      points: 0,
    });
    const playersAfterDeletion = await world.adminApi.get<Schemas['AdminPlayerView'][]>(
      `/api/admin/seasons/${world.seasonId}/players`,
    );
    expect(
      playersAfterDeletion.find((candidate) => candidate.id === partner.playerId),
    ).toMatchObject({
      isInactive: false,
    });

    await rollerPage.getByTestId('nav-season').click();
    await expect(rollerPage.getByTestId('active-run')).toContainText(coopGameTitle, live);
    await completeHere(rollerPage);
    const afterCompletion = await seasonOf(rollerApi, world.seasonId);
    const result = afterCompletion.me?.lastCompleted;
    if (!result) throw new Error('Deleting the partner prevented the roller from completing.');
    expect(result.id).toBe(run.id);
    expect(result.dice).toHaveLength(2);
    const rollerPoints = Math.ceil(result.total / 2);
    const partnerPoints = result.total - rollerPoints;
    expect(afterCompletion.me).toMatchObject({ coins: 5, activeRun: null });
    expect(
      afterCompletion.players.find((candidate) => candidate.id === roller.playerId)?.points,
    ).toBe(rollerPoints);
    expect(
      afterCompletion.players.find((candidate) => candidate.id === partner.playerId),
    ).toMatchObject({ points: partnerPoints });
    const adminPlayers = await world.adminApi.get<Schemas['AdminPlayerView'][]>(
      `/api/admin/seasons/${world.seasonId}/players`,
    );
    expect(adminPlayers.find((candidate) => candidate.id === partner.playerId)).toMatchObject({
      coins: 4,
      isInactive: false,
    });
    expect(await rollerApi.get<Schemas['CoopView'] | null>(`${season}/coop`)).toBeNull();
    const events = (await commandsOf(world)).flatMap((command) => command.events);
    expect(events.filter((event) => event === 'coop-left')).toHaveLength(0);
    expect(events.filter((event) => event === 'run-completed')).toHaveLength(1);
    expect(events.filter((event) => event === 'coop-reward-split')).toHaveLength(1);
    expect((await partnerApi.send('GET', season)).status()).toBe(401);
  } finally {
    await Promise.all([
      rollerPage.context().close(),
      partnerPage.context().close(),
      rollerApi.dispose(),
      partnerApi.dispose(),
      world.adminApi.dispose(),
    ]);
  }
});

test('both participants see the earlier board deadline and its close expires the response', async ({
  browser,
}) => {
  const { world, roller, partner, season } = await setupCoopSeason(true);
  const rollerPage = await signIn(browser, roller);
  const partnerPage = await signIn(browser, partner);
  try {
    expect(await rollAndStartHere(rollerPage)).toBe(coopGameTitle);
    await openInteractions(rollerPage, world.seasonId);
    await openInteractions(partnerPage, world.seasonId);
    await rollerPage.getByRole('button', { name: 'Опубликовать запрос', exact: true }).click();
    const response = partnerPage.getByRole('button', { name: 'Откликнуться', exact: true });
    await expect(response).toBeVisible(live);

    const requests = await world.adminApi.get<Schemas['PartnerRequestView'][]>(
      `${season}/partner-requests`,
    );
    expect(requests).toHaveLength(1);
    const [request] = requests;
    if (!request) throw new Error('The partner request is missing.');
    expect(request).toMatchObject({ playerId: roller.playerId, gameTitle: coopGameTitle });

    await response.click();
    const incoming = rollerPage.getByRole('region', { name: 'Приглашения', exact: true });
    const outgoing = partnerPage.getByRole('region', { name: 'Приглашения', exact: true });
    await expect(incoming.getByRole('button', { name: 'Принять', exact: true })).toBeVisible(live);
    await expect(incoming.locator('time')).toHaveAttribute('datetime', request.expiresAt, live);
    await expect(outgoing.locator('time')).toHaveAttribute('datetime', request.expiresAt, live);

    await rollerPage.getByRole('button', { name: 'Закрыть запрос', exact: true }).click();
    for (const invitations of [incoming, outgoing]) {
      await invitations.getByText('История приглашений', { exact: true }).click();
      await expect(invitations.getByText('Истекло', { exact: true })).toBeVisible(live);
    }
    await expect(incoming.getByRole('button', { name: 'Принять', exact: true })).toHaveCount(0);
  } finally {
    await world.adminApi.dispose();
  }
});
