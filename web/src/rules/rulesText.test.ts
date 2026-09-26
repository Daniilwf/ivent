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
      'Если проверки админом ждут 2 прохождения, новый ролл закрыт, пока их не станет меньше.',
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

    const soft = rulesPage({
      ...demoRuleset,
      roll: { ...demoRuleset.roll, minPlayMinutesBeforeDrop: 90 },
      drop: { ...demoRuleset.drop, affectsPosition: false, mandatoryEvent: 'none' },
    }).drop;
    expect(soft).toContain('Дропнуть можно не раньше чем через 90 мин игры — на совести.');
    expect(soft).toContain('Штраф: −2d4 очков.');
    expect(soft).not.toContain(t.drop.badEvent);
  });

  it('lists the finish bonuses from the second place, then the rest', () => {
    expect(rulesPage(demoRuleset).bonuses).toEqual([
      { place: '2-е место', points: '+10 очк.' },
      { place: '3-е место', points: '+8 очк.' },
      { place: '4-е место', points: '+6 очк.' },
      { place: '5-е место', points: '+4 очк.' },
      { place: '6-е и дальше', points: '+2 очк.' },
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
    expect(win).toContain('От старта до финиша — 60 клеток.');
    expect(win).toContain(
      'При равных очках выше тот, у кого больше пройденных игр, затем — раньше набраны итоговые очки.',
    );
    const at = rulesPage({
      ...demoRuleset,
      finish: { ...demoRuleset.finish, requireApprovalForFirst: false },
    });
    expect(at.win).not.toContain(t.win.firstApproval);
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
      'Монетки: 1 монетка за час игры, минимум — 3 монетки за прохождение.',
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

  it('rounds down or up when the rule says so', () => {
    expect(diceFor(8, { ...rule, rounding: 'floor' })).toBe(2);
    expect(diceFor(7, { ...rule, rounding: 'ceil' })).toBe(3);
  });
});

describe('the history in words', () => {
  it('names known fields, the finish bonus by place, and an unknown one by its path', () => {
    expect(fieldName('reward.diceCount.max')).toBe('Максимум кубиков');
    expect(fieldName('finish.bonusByOrder[0]')).toBe('Бонус за финиш: 2-е место');
    expect(fieldName('economy.shop.rollCost')).toBe('economy.shop.rollCost');
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
