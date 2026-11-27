import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { expect, test } from '@playwright/test';
import type { Schemas } from '../support/api.ts';
import {
  apiOf,
  complete,
  live,
  rollAndStart,
  rollHere,
  seasonOf,
  setUpSeason,
  signIn,
} from '../support/world.ts';

const designShot = (name: string) =>
  join(dirname(fileURLToPath(import.meta.url)), '..', '..', 'test-artifacts', 'design', name);

// The event asks through the ordinary turn UI, including live flag changes. A fixed one-entry deck makes the
// question deterministic without substituting a response from the browser or choosing a lucky random seed.
test('an event question keeps an unsent answer through a live pause and resumes once', async ({
  browser,
}, info) => {
  const world = await setUpSeason({ players: ['Выбирающий', 'Наблюдатель'], start: false });
  const player = world.players[0];
  if (!player) throw new Error('No player');
  const admin = world.adminApi;
  const path = `/api/seasons/${world.seasonId}`;
  const adminPath = `/api/admin/seasons/${world.seasonId}`;
  const rules = await admin.get<Schemas['RulesView']>(`${path}/rules`);
  await admin.put(`${adminPath}/rules`, {
    expectedVersion: rules.version,
    ruleset: {
      ...rules.ruleset,
      features: { ...rules.ruleset.features, mapMode: 'graph', items: true, events: true },
      events: { ...rules.ruleset.events, defaultDeck: 'bad' },
    },
  });
  await admin.post(`${adminPath}/map/publish`, {
    map: {
      cells: [
        { id: 'start', type: 'start', x: 0, y: 0 },
        { id: 'c1', type: 'empty', x: 110, y: 0 },
        { id: 'finish', type: 'finish', x: 220, y: 0 },
      ],
      edges: [
        { from: 'start', to: 'c1', isDefaultForward: true, isPrimaryBackward: true },
        { from: 'c1', to: 'finish', isDefaultForward: true, isPrimaryBackward: true },
      ],
      zones: [],
    },
    comment: 'Карта вопроса',
  });
  await admin.post(`/api/test/seasons/${world.seasonId}/content`, {
    objects: [
      JSON.stringify({
        id: 'coin-or-step',
        kind: 'event',
        name: 'Монетки или шаг',
        description: 'Выбери: 3 монетки или шаг вперёд.',
        rarity: 'common',
        price: null,
        hostile: false,
        effect: {
          target: { selector: 'self' },
          outcomes: {
            choice: { prompt: 'Что берёшь?', options: { list: ['монетки', 'шаг'] } },
            cases: [
              {
                option: 'монетки',
                actions: [{ type: 'changeResource', resource: 'coins', amount: 3 }],
              },
              { option: 'шаг', actions: [{ type: 'move', steps: 1 }] },
            ],
          },
        },
      }),
    ],
    wheels: [
      JSON.stringify({
        id: 'bad',
        name: 'Плохие ивенты',
        deck: 'bad',
        entries: [{ objectId: 'coin-or-step', weight: 1 }],
      }),
    ],
    comment: 'Вопрос ивента',
  });
  await admin.post(`${adminPath}/status`, { to: 'active' });
  const owner = await apiOf(player);
  let page;
  try {
    await rollAndStart(owner, world.seasonId);
    page = await signIn(browser, player);
    await page.goto('/');
    await expect(page.getByTestId('active-run')).toBeVisible();
    const runId = (await seasonOf(owner, world.seasonId)).me?.activeRun?.id;
    if (!runId) throw new Error('No active run');
    await owner.post(`${path}/drop`, { runId });
    const question = page.getByTestId('event-choice');
    await expect(question.getByText('Что берёшь?')).toBeVisible(live);
    await expect(page.getByTestId('roll')).toHaveCount(0);
    const answer = question.getByRole('button', { name: 'Ответить' });
    await expect(answer).toBeDisabled();
    await question.getByText('монетки', { exact: true }).click();
    await expect(question.getByRole('radio', { name: 'монетки', exact: true })).toBeChecked();
    await expect(answer).toBeEnabled();
    await page.evaluate(() => document.fonts.ready);
    await page.screenshot({
      path: designShot(`s5-21-question-${info.project.name}.png`),
      fullPage: true,
    });
    expect(
      await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth),
    ).toBe(true);

    const setEvents = async (enabled: boolean) => {
      const current = await admin.get<Schemas['RulesView']>(`${path}/rules`);
      await admin.put(`${adminPath}/rules`, {
        expectedVersion: current.version,
        ruleset: { ...current.ruleset, features: { ...current.ruleset.features, events: enabled } },
      });
    };
    await setEvents(false);
    await expect(answer).toBeDisabled(live);
    await expect(question.getByRole('radio', { name: 'монетки', exact: true })).toBeChecked();
    await expect(
      page.getByText('Ивенты временно выключены. Ответить можно будет после включения.'),
    ).toBeVisible();
    await page.screenshot({
      path: designShot(`s5-21-paused-${info.project.name}.png`),
      fullPage: true,
    });
    await setEvents(true);
    await expect(answer).toBeEnabled(live);
    const before = (await seasonOf(owner, world.seasonId)).me?.coins;
    if (before == null) throw new Error('No balance');
    await answer.click();
    await expect(question).toHaveCount(0, live);
    await expect(page.getByTestId('roll')).toBeEnabled();
    const after = await seasonOf(owner, world.seasonId);
    expect(after.me?.coins).toBe(before + 3);
    expect(after.me?.choice).toBeNull();
    await page.reload();
    await expect(page.getByTestId('event-choice')).toHaveCount(0);
    expect((await seasonOf(owner, world.seasonId)).me?.coins).toBe(before + 3);
    await page.goto(`/seasons/${world.seasonId}/feed`);
    await expect(
      page.getByTestId(/^feed-item-/).filter({ hasText: 'ответ ивента принят' }),
    ).toHaveCount(1, live);
  } finally {
    if (page && !page.isClosed()) await page.context().close();
    await owner.dispose();
    await admin.dispose();
  }
});

test('waiting events and conditions follow the roll and show one completion result', async ({
  browser,
}, info) => {
  const world = await setUpSeason({ players: ['Испытатель'], start: false });
  const player = world.players[0];
  if (!player) throw new Error('No player');
  const admin = world.adminApi;
  const path = `/api/seasons/${world.seasonId}`;
  const adminPath = `/api/admin/seasons/${world.seasonId}`;
  const rules = await admin.get<Schemas['RulesView']>(`${path}/rules`);
  await admin.put(`${adminPath}/rules`, {
    expectedVersion: rules.version,
    ruleset: {
      ...rules.ruleset,
      features: { ...rules.ruleset.features, items: true, events: true },
      events: { ...rules.ruleset.events, defaultDeck: 'bad' },
    },
  });
  await admin.post(`/api/test/seasons/${world.seasonId}/content`, {
    objects: [
      JSON.stringify({
        id: 'trial',
        kind: 'event',
        name: 'Даже не вспотел',
        description:
          'Следующую игру нужно пройти на сложной сложности или выше. В следующем броске за прохождение на один кубик больше.',
        rarity: 'common',
        price: null,
        hostile: false,
        effect: {
          target: { selector: 'self' },
          actions: [
            { type: 'modifyNextRoll', runCondition: { difficultyAtLeast: 'hard' } },
            { type: 'modifyDice', when: 'next', stage: 'count', value: 1 },
          ],
        },
      }),
    ],
    wheels: [
      JSON.stringify({
        id: 'bad',
        name: 'Плохие ивенты',
        deck: 'bad',
        entries: [{ objectId: 'trial', weight: 1 }],
      }),
    ],
    comment: 'Ивент с условием',
  });
  await admin.post(`${adminPath}/status`, { to: 'active' });
  const owner = await apiOf(player);
  const page = await signIn(browser, player);
  try {
    await rollAndStart(owner, world.seasonId);
    await owner.post(`${path}/drop`);
    const turn = page.getByTestId('turn');
    await turn.getByText('Ждут своего часа', { exact: true }).click();
    await expect(turn.getByRole('heading', { name: 'Накопленные ивенты' })).toBeVisible(live);
    await expect(turn.getByRole('heading', { name: 'Условия следующей игры' })).toBeVisible();
    await expect(turn.getByText('Не ниже: Сложная', { exact: true })).toBeVisible();
    await expect(turn.getByText('Ждёт броска', { exact: true })).toBeVisible();
    await expect(turn.getByText('Ждёт ролла игры', { exact: true })).toHaveCount(0);
    const sourceText = turn.locator(
      'details:has(> summary:has-text("Текст ивента: «Даже не вспотел»"))',
    );
    await expect(sourceText).not.toHaveAttribute('open');
    await sourceText.locator('summary').focus();
    await sourceText.locator('summary').press('Space');
    await expect(sourceText).toHaveAttribute('open');
    await expect(sourceText).toContainText(
      'Следующую игру нужно пройти на сложной сложности или выше.',
    );
    await sourceText.locator('summary').press('Enter');
    await expect(sourceText).not.toHaveAttribute('open');
    await page.evaluate(() => document.fonts.ready);
    await page.screenshot({
      path: designShot(`s5-21-waiting-${info.project.name}.png`),
      fullPage: true,
    });
    await rollHere(page);
    await expect(page.getByTestId('offer').or(page.getByTestId('choice'))).toBeVisible(live);
    const rolled = await seasonOf(owner, world.seasonId);
    if (rolled.me?.choice?.kind === 'game') {
      const option = rolled.me.choice.options.find((candidate) => candidate.game);
      if (!option) throw new Error('No offered game');
      await owner.post(`${path}/choose`, { choiceId: rolled.me.choice.id, optionId: option.id });
    }
    const offer = page.getByTestId('offer');
    await expect(offer.getByRole('heading', { name: 'Условия прохождения' })).toBeVisible(live);
    await expect(offer.getByText('Не ниже: Сложная', { exact: true })).toBeVisible();
    await page.screenshot({
      path: designShot(`s5-21-offer-conditions-${info.project.name}.png`),
      fullPage: true,
    });
    await page.getByTestId('start').click();
    const active = page.getByTestId('active-run');
    await expect(active.getByRole('heading', { name: 'Условия прохождения' })).toBeVisible(live);
    await expect(active.getByText('Не ниже: Сложная', { exact: true })).toBeVisible();
    await page.screenshot({
      path: designShot(`s5-21-conditions-${info.project.name}.png`),
      fullPage: true,
    });
    await complete(owner, world.seasonId, 'normal');
    await expect(turn.getByText('Нарушено', { exact: true })).toHaveCount(1, live);
    await expect(turn.getByRole('heading', { name: 'Условия следующей игры' })).toHaveCount(0);
    const result = (await seasonOf(owner, world.seasonId)).me?.lastCompleted;
    expect(result?.conditionCheck).toEqual({ met: false, waived: false });
    expect(result?.dice).toEqual([]);
    await expect(turn.getByText('Ждёт броска', { exact: true })).toBeVisible();
    await expect(sourceText).not.toHaveAttribute('open');
    await page.screenshot({
      path: designShot(`s5-21-unmet-${info.project.name}.png`),
      fullPage: true,
    });
    expect(
      await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth),
    ).toBe(true);
    await page.goto(`/seasons/${world.seasonId}/feed`);
    await expect(
      page.getByTestId(/^feed-item-/).filter({ hasText: 'Условие прохождения не выполнено' }),
    ).toHaveCount(1, live);
  } finally {
    if (!page.isClosed()) await page.context().close();
    await owner.dispose();
    await admin.dispose();
  }
});
