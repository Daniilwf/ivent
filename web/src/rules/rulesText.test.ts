import { ru } from '../i18n/ru';
import { demoRuleset } from './demoRules';
import { diceFor, fieldName, rulesPage, valueText } from './rulesText';

// H7: the rules page's sentences come from the ruleset in force, so its numbers are real (SPEC «Правила на сайте»)

const t = ru.rules;

describe('the rules page from the ruleset', () => {
  it('says how the dice are counted, with the limits and an example by the same rule', () => {
    const page = rulesPage(demoRuleset);
    expect(page.reward).toEqual([
      'Один кубик за каждые 3 ч по HowLongToBeat, с округлением до ближайшего.',
      'Не меньше 1 и не больше 10 кубиков.',
      'Например, игра на 12 ч на нормальной сложности — 4d4.',
    ]);
  });

  it('lists the die of every difficulty, the top one with its event', () => {
    expect(rulesPage(demoRuleset).dice.map((row) => [row.label, row.die])).toEqual([
      ['Лёгкая', 'd2'],
      ['Нормальная или сложностей нет', 'd4'],
      ['Сложная', 'd6'],
      ['Выше сложной', 'd6 и хороший ивент'],
    ]);
  });

  it('gives the rerolls: free ones and the price after them, in coins or a bad event', () => {
    expect(rulesPage(demoRuleset).roll).toContain(t.roll.free(1));
    expect(rulesPage(demoRuleset).roll).toContain('Дальше реролл стоит 5 монеток.');

    const badEvent = rulesPage({
      ...demoRuleset,
      roll: { ...demoRuleset.roll, freeRerollsPerRoll: 0, rerollCost: { kind: 'badEvent' } },
    });
    expect(badEvent.roll).toContain(t.roll.noFree);
    expect(badEvent.roll).toContain(t.roll.costBadEvent);
  });

  it('names the limit of runs waiting for the check (D-134) only when the season has one', () => {
    expect(rulesPage(demoRuleset).roll).toContain(
      'Если проверки админом ждут 2 прохождения или больше, новый ролл закрыт, пока их не станет меньше.',
    );
    const unlimited = rulesPage({
      ...demoRuleset,
      season: { ...demoRuleset.season, maxUncheckedRuns: null },
    });
    expect(unlimited.roll.join(' ')).not.toContain('ждут');
  });

  it('says the choice size only when there is a choice', () => {
    expect(rulesPage(demoRuleset).roll.join(' ')).not.toContain('выбираешь');
    const choice = rulesPage({ ...demoRuleset, roll: { ...demoRuleset.roll, choiceCount: 3 } });
    expect(choice.roll).toContain('После ролла выбираешь одну из 3 выпавших игр.');
  });

  it('gives the drop penalty with what it takes and the bad event, and the tech reroll window', () => {
    const drop = rulesPage(demoRuleset).drop;
    expect(drop).toContain('Дропнуть можно не раньше чем через 1 ч игры — на совести.');
    expect(drop).toContain('Штраф: −2d4 очков и клеток.');
    expect(drop).toContain(t.drop.badEvent);
    expect(drop.some((line) => line.includes('Доступен 48 ч после ролла'))).toBe(true);
    // D-205: the default adds a die per drop in a row; D-206: no wish reroll while the list is empty
    expect(drop).toContain(t.drop.consecutive(1, 4));
    expect(drop.join(' ')).not.toContain('Реролл по желанию');

    const soft = rulesPage({
      ...demoRuleset,
      roll: { ...demoRuleset.roll, minPlayMinutesBeforeDrop: 90 },
      drop: { ...demoRuleset.drop, affectsPosition: false, mandatoryEvent: 'none' },
    }).drop;
    expect(soft).toContain('Дропнуть можно не раньше чем через 90 мин игры — на совести.');
    expect(soft).toContain('Штраф: −2d4 очков.');
    expect(soft).not.toContain(t.drop.badEvent);

    const flat = rulesPage({
      ...demoRuleset,
      drop: { ...demoRuleset.drop, consecutiveExtraDice: null },
    }).drop;
    expect(flat.join(' ')).not.toContain('подряд');
    const wish = rulesPage({
      ...demoRuleset,
      roll: { ...demoRuleset.roll, wishRerollTags: ['RPG', 'VN'] },
    }).drop;
    expect(wish).toContain(t.drop.wish('RPG, VN', 48));
  });

  it('lists the finish bonuses from the second place, then the rest', () => {
    expect(rulesPage(demoRuleset).bonuses).toEqual([
      { place: '2-й на финише', points: '+10 очков' },
      { place: '3-й на финише', points: '+8 очков' },
      { place: '4-й на финише', points: '+6 очков' },
      { place: '5-й на финише', points: '+4 очка' },
      { place: '6-й на финише и дальше', points: '+2 очка' },
    ]);
    const none = rulesPage({
      ...demoRuleset,
      finish: { ...demoRuleset.finish, bonusByOrder: [], bonusAfterList: 0 },
    });
    expect(none.bonuses).toEqual([]);
    expect(none.finish).toContain(t.finish.noBonus);
  });

  it('mentions the approval of the first place and the tiebreakers in their order', () => {
    const win = rulesPage(demoRuleset).win;
    expect(win).toContain(t.win.firstApproval);
    expect(win).toContain('От старта до финиша — 70 клеток.');
    expect(win).toContain(
      'При равных очках выше тот, у кого больше пройденных игр, затем — раньше набраны итоговые очки.',
    );
    // The first freezes when his proofs are checked (D-99), or at once when the rules need no check
    expect(win).toContain(t.win.firstFrozenApproved);
    expect(win).not.toContain(t.win.firstFrozen);
    const at = rulesPage({
      ...demoRuleset,
      finish: { ...demoRuleset.finish, requireApprovalForFirst: false },
    });
    expect(at.win).not.toContain(t.win.firstApproval);
    expect(at.win).toContain(t.win.firstFrozen);
  });

  it('tells the challenge bonus only when challenges are on', () => {
    expect(rulesPage(demoRuleset).rewardAfter.join(' ')).not.toContain('челлендж');
    const on = rulesPage({
      ...demoRuleset,
      features: { ...demoRuleset.features, challenges: true },
    });
    expect(on.rewardAfter).toContain(t.reward.challenge(1));
  });

  it('gives the coins for a run', () => {
    expect(rulesPage(demoRuleset).rewardAfter).toContain(
      'Монетки: 1 монетка за час по HowLongToBeat (считается не больше 30 ч), минимум — 3 монетки за прохождение.',
    );
  });
});

describe('the dice count', () => {
  const rule = demoRuleset.reward.diceCount;

  it('rounds to the nearest, half up, and keeps to the limits', () => {
    expect(diceFor(4.5, rule)).toBe(2);
    expect(diceFor(4.4, rule)).toBe(1);
    expect(diceFor(1, rule)).toBe(1);
    expect(diceFor(100, rule)).toBe(10);
  });

  it("counts a fractional hours per die like the engine's decimal, not a binary fraction", () => {
    // 12 / 2.4 is 5 in decimal and 5.000000000000001 in a double: ceil must stay 5
    expect(diceFor(12, { ...rule, hoursPerDie: 2.4, rounding: 'ceil' })).toBe(5);
    expect(diceFor(7.2, { ...rule, hoursPerDie: 2.4, rounding: 'floor' })).toBe(3);
  });

  it('rounds down or up when the rule says so', () => {
    expect(diceFor(8, { ...rule, rounding: 'floor' })).toBe(2);
    expect(diceFor(7, { ...rule, rounding: 'ceil' })).toBe(3);
  });
});

describe('the economy on the rules page (S4-1)', () => {
  const on = (features: Partial<typeof demoRuleset.features>) =>
    rulesPage({ ...demoRuleset, features: { ...demoRuleset.features, ...features } });

  it('is not there while the economy mechanics are off', () => {
    expect(rulesPage(demoRuleset).economy).toEqual([]);
  });

  it('says the inventory limit and that a debt closes buying, with items on', () => {
    const page = on({ items: true });
    expect(page.economy).toContain(t.economy.inventory(5));
    expect(page.economy).toContain(t.economy.debt);
    expect(page.economy.join(' ')).not.toMatch(/магазин|Ставк/u);
  });

  it('gives the shop with its real numbers', () => {
    const page = on({ items: true, shop: true });
    expect(page.economy).toContain(
      'Ролл магазина стоит 5 монеток и даёт 3 личных лота на 15 мин; цена лота — по редкости.',
    );
    expect(page.economy).toContain(
      'Каждый следующий ролл дороже на 5 монеток; цена снова 5 монеток после завершения или дропа прохождения.',
    );
  });

  it('gives the bets with the window, the terms, the limits and the coefficients', () => {
    const page = on({ bets: true });
    expect(page.economy).toContain(
      'Ставка — на текущее прохождение другого игрока, в первые 24 ч после его ролла; срок — 1, 3 или 7 дн.',
    );
    expect(page.economy).toContain('Не больше 10 монеток за ставку и не больше 3 открытых ставок.');
    expect(page.economy).toContain(
      'Если успеет, вернётся ставка × коэффициент по часам игры на день срока: до 1 ч — ×1,2, до 3 ч — ×2, больше — ×3.',
    );
    expect(page.economy).toContain(t.economy.betLost);
  });
});

describe('the history in words', () => {
  it('names known fields, the finish bonus by place, and an unknown one by its path', () => {
    expect(fieldName('reward.diceCount.max')).toBe('Максимум кубиков');
    expect(fieldName('finish.bonusByOrder[0]')).toBe('Бонус за финиш: 2-й на финише');
    expect(fieldName('effects.maxChainDepth')).toBe('effects.maxChainDepth');
  });

  it('names the mechanics switched on and the numbers of the economy (S4-1)', () => {
    expect(fieldName('features.shop')).toBe('Магазин');
    expect(fieldName('features.bets')).toBe('Ставки');
    expect(fieldName('features.items')).toBe('Предметы');
    expect(fieldName('economy.shop.rollCost')).toBe('Цена ролла магазина, монеток');
    expect(fieldName('bets.maxStake')).toBe('Наибольшая ставка, монеток');
    expect(fieldName('bets.payoutByHoursPerDay[1].multiplier')).toBe(
      'Коэффициент ставки, ступень 2',
    );
  });

  it('gives values as numbers, yes and no, known words, and «нет» for none', () => {
    expect(valueText('12')).toBe('12');
    expect(valueText('0.5')).toBe('0,5');
    expect(valueText('true')).toBe('да');
    expect(valueText('false')).toBe('нет');
    expect(valueText('null')).toBe('нет');
    expect(valueText(null)).toBe('нет');
    expect(valueText('"badEvent"')).toBe('плохой ивент');
    expect(valueText('"other"')).toBe('other');
    expect(valueText('[1,2]')).toBe('[1,2]');
    expect(valueText('not json')).toBe('not json');
  });
});
