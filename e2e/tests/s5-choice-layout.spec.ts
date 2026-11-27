import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { expect, test } from '@playwright/test';

const shot = (name: string) =>
  join(dirname(fileURLToPath(import.meta.url)), '..', '..', 'test-artifacts', 'design', name);

test('long event questions fit the viewport and keep their answer controls', async ({
  page,
}, info) => {
  await page.goto('/styleguide#event-choice');
  const choice = page.getByTestId('event-choice').first();
  await expect(choice).toBeVisible();
  await page.evaluate(() => document.fonts.ready);
  const prompt = 'Какомуиздрузейпередатьследующееоченьсложноеиспытание'.repeat(5);
  const legend = choice.locator('legend');
  // Only content changes: the question may contain a long word, just as a category or an imported name can.
  await legend.evaluate((element, text) => {
    element.textContent = text;
  }, prompt);
  await expect(legend).toHaveText(prompt);
  await expect(choice.getByRole('radio')).toHaveCount(2);
  await expect(choice.getByRole('button', { name: 'Ответить' })).toBeVisible();
  await choice.scrollIntoViewIfNeeded();
  await page.screenshot({ path: shot(`s5-21-layout-long-${info.project.name}.png`) });
  const geometry = await page.evaluate(() => ({
    pageWidth: document.documentElement.scrollWidth,
    viewportWidth: innerWidth,
  }));
  const viewport = page.viewportSize();
  if (!viewport) throw new Error('The layout scenario needs a fixed viewport.');
  expect(
    geometry.pageWidth,
    'The question must wrap without widening the page',
  ).toBeLessThanOrEqual(viewport.width);
});

test('a selected event answer keeps a visible check in the label color at its intended size', async ({
  page,
}, info) => {
  await page.goto('/styleguide#event-choice');
  const choice = page.getByTestId('event-choice').first();
  await expect(choice).toBeVisible();
  await page.evaluate(() => document.fonts.ready);
  const option = choice.locator('label').first();
  await option.click();
  await expect(option.getByRole('radio')).toBeChecked();
  const check = option.locator('svg');
  await expect(check).toBeVisible();
  const appearance = await check.evaluate((element) => ({
    color: getComputedStyle(element).color,
    labelColor: element.parentElement ? getComputedStyle(element.parentElement).color : null,
    width: element.getBoundingClientRect().width,
    intendedWidth: Number(element.getAttribute('width')),
  }));
  await page.screenshot({ path: shot(`s5-21-layout-selected-${info.project.name}.png`) });
  expect
    .soft(appearance.color, 'The check must remain visible on the selected fill')
    .toBe(appearance.labelColor);
  expect(appearance.width, 'The check must not shrink under a long label').toBeGreaterThanOrEqual(
    appearance.intendedWidth,
  );
});

test('event history responds to Space while an unfocused map editor is mounted elsewhere', async ({
  page,
}) => {
  await page.goto('/styleguide#event-history');
  const history = page
    .locator('details')
    .filter({ hasText: 'Последний ивент: «Попутный ветер»' })
    .first();
  const summary = history.locator('summary').first();
  await expect(summary).toBeVisible();
  await expect(history).not.toHaveAttribute('open');
  await summary.focus();
  await summary.press('Space');
  await expect(history).toHaveAttribute('open');
  await summary.press('Enter');
  await expect(history).not.toHaveAttribute('open');
  await summary.press('Space');
  await expect(history).toHaveAttribute('open');
});
