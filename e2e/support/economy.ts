import { expect, type Page } from '@playwright/test';
import { live } from './world.ts';

/** Brings a season's economy panel into view on either layout. */
export async function openEconomy(page: Page, name: 'inventory' | 'shop' | 'bets') {
  const panel = page.getByTestId(name);
  const opener = page.getByTestId(`open-${name}`);
  await expect(opener.or(panel).first()).toBeVisible(live);
  if (!(await panel.isVisible())) await opener.click();
  await expect(panel).toBeVisible(live);
  return panel;
}
