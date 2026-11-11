import { expect, test } from '@playwright/test';
import type { Schemas } from '../support/api.ts';
import { openAdmin, setUpSeason, signIn } from '../support/world.ts';

// 4.12 (D-490…D-498): in a season of its own with items on, the admin opens an effect in the content editor, breaks
// it (no trigger), saves, finds the server's error next to the trigger field, fixes it and saves a new version of the
// content. On a phone the editor only shows the content.

const curse = {
  id: 'curse',
  kind: 'effect',
  name: 'Проклятие',
  description: 'Минус очко за следующее прохождение.',
  effect: {
    target: { selector: 'self' },
    trigger: 'runCompleted',
    duration: { runs: 1 },
    actions: [{ type: 'changeResource', resource: 'points', amount: -1 }],
  },
};
const orange = {
  id: 'orange',
  kind: 'item',
  name: 'Апельсин',
  description: 'Сдвинься на клетку вперёд.',
  rarity: 'common',
  price: 10,
  window: 'anytime',
  effect: { target: { selector: 'self' }, actions: [{ type: 'move', steps: 1 }] },
};

test('the admin fixes an invalid effect guided by the error at its field and saves a new version', async ({
  browser,
}, testInfo) => {
  const world = await setUpSeason({ players: ['Кладовщик'], start: false });
  const admin = world.adminApi;
  const rules = await admin.get<Schemas['RulesView']>(`/api/seasons/${world.seasonId}/rules`);
  await admin.put(`/api/admin/seasons/${world.seasonId}/rules`, {
    expectedVersion: rules.version,
    ruleset: { ...rules.ruleset, features: { ...rules.ruleset.features, items: true } },
  });
  await admin.post(`/api/admin/seasons/${world.seasonId}/content/publish`, {
    expectedVersion: 0,
    pack: { objects: [orange, curse], wheels: [] },
    comment: 'Первые предметы',
  });

  const page = await signIn(browser, world.admin);
  await openAdmin(page, world, 'content');
  await expect(page.getByTestId('content-list')).toContainText('Версия контента 1');

  if (testInfo.project.name.startsWith('phone')) {
    // A phone looks at the content: the JSON read-only, no saving
    await expect(page.getByText('на телефоне контент можно только посмотреть')).toBeVisible();
    await page.getByTestId('content-open-orange').click();
    await expect(page.getByTestId('content-json')).toHaveAttribute('readonly', '');
    await expect(page.getByTestId('content-save')).toHaveCount(0);
    return;
  }

  await page.getByRole('radio', { name: /^Эффекты/u }).check({ force: true });
  await page.getByTestId('content-open-curse').click();
  const editor = page.getByTestId('content-editor');
  await expect(editor.getByRole('heading', { name: 'Редактор: Проклятие' })).toBeFocused();

  // A new description, and the trigger taken away by mistake: the schema lets it be empty, the server's rules do not
  await editor
    .getByTestId('content-field-description')
    .fill('Минус очко за одно следующее прохождение.');
  const trigger = editor.getByTestId('content-field-effect.trigger');
  await trigger.selectOption('');
  await editor.getByTestId('content-comment').fill('Проклятие на одно прохождение');
  await editor.getByTestId('content-save').click();

  await expect(editor.getByText('Сначала исправь ошибки.')).toBeVisible();
  await expect(trigger).toHaveAccessibleDescription('Этому объекту нужен триггер.');
  await expect(editor.getByTestId('content-problems')).toContainText('effect.trigger');

  // Guided by the error: the trigger back, and the save goes through as version 2
  await trigger.selectOption('runCompleted');
  await expect(editor.getByTestId('content-problems')).toHaveCount(0);
  await editor.getByTestId('content-save').click();

  await expect(page.getByText('Сохранено: версия контента 2.')).toBeVisible();
  await expect(page.getByTestId('content-list')).toContainText('Версия контента 2');
  await expect(page.getByTestId('content-history')).toContainText(
    '«Проклятие на одно прохождение»',
  );
  const content = await admin.get<Schemas['AdminContentView']>(
    `/api/admin/seasons/${world.seasonId}/content`,
  );
  expect(content.version).toBe(2);

  // No horizontal scroll with the editor open
  const overflow = await page.evaluate(
    () => document.documentElement.scrollWidth - document.documentElement.clientWidth,
  );
  expect(overflow).toBeLessThanOrEqual(0);
});
