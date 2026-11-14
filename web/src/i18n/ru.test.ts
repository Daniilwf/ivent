import { describe, expect, it } from 'vitest';
import { coins, ru } from './ru';

// K1S-4: the completion's result said «+4 очков» and «до 1 клеток» for any total
describe('the completion result in Russian', () => {
  const d = ru.moments.dice;

  it('puts the points in the right plural', () => {
    expect(d.after(1, 'Фишка на клетке 2')).toBe('+1 очко. Фишка на клетке 2');
    expect(d.after(4, 'Фишка на клетке 5')).toBe('+4 очка. Фишка на клетке 5');
    expect(d.after(5, 'Фишка на клетке 6')).toBe('+5 очков. Фишка на клетке 6');
    expect(d.after(12, '')).toMatch(/^\+12 очков/);
    expect(d.after(22, '')).toMatch(/^\+22 очка/);
    expect(d.afterStay(2)).toBe('+2 очка, фишка стоит на месте');
  });

  it('says «до 1 клетки» and «до 4 клеток» in the dice summary', () => {
    expect(d.result([1], [], 1)).toBe('1. Итого +1 очко и до 1 клетки вперёд');
    expect(d.result([2, 2], [], 4)).toBe('2 + 2. Итого +4 очка и до 4 клеток вперёд');
    expect(d.result([3, 3], [], 21)).toContain('+21 очко и до 21 клетки вперёд');
    expect(d.resultStay([1, 1], [], 2)).toBe('1 + 1. Итого +2 очка, фишка стоит на месте');
  });
});

// Review of K1S: the texts name things by the glossary and the sections by their own names
describe('the season texts follow the glossary', () => {
  it('sends the admin to the section that exists, speaking to them as «ты»', () => {
    const text = ru.rejection['season.effectsPending'];
    expect(text).toContain(`«${ru.admin.sections.effects}»`);
    expect(text).not.toMatch(/разыграйте/);
  });

  it('calls the bonus cell «бонус очков»', () => {
    expect(ru.feed.facts.cellBonus(3)).toBe('+3 очка — бонус очков');
  });

  it('does not list only a part of what a rejection takes', () => {
    for (const line of [
      ru.turn.lastRejected('Silent Hill'),
      ru.turn.checkRejected('Silent Hill'),
    ]) {
      expect(line).toBe('Прохождение отклонено (Silent Hill): снято всё, что дал ход.');
    }
  });
});

// Design review of fix/stage12-close («Чего избегать», «Тексты»): no «·» between parts of a line — a comma or a second
// line; word forms through plural(), never an abbreviation like «очк.» or «кл.»
describe('the texts of the interface', () => {
  const sources = import.meta.glob<string>(
    ['../**/*.{ts,tsx}', '!../**/*.test.{ts,tsx}', '!../api/schema.ts'],
    {
      query: '?raw',
      import: 'default',
      eager: true,
    },
  );

  it('are read from the source', () => {
    expect(Object.keys(sources)).toContain('./ru.ts');
  });

  it.each(Object.keys(sources))('%s has no «·» separator and no cut word forms', (path) => {
    const text = sources[path] ?? '';
    expect(text).not.toContain('·');
    expect(text).not.toMatch(/\d\}?\s(очк|кл)\./);
  });
});

// S4D-6: «Поставить 1 монетка», «Ролл магазина за 1 монетка», «От 1 до 21 монетка» — the word for coins follows its case
describe('coins in their case', () => {
  const cases: [number, string, string, string][] = [
    [1, '1 монетка', '1 монетку', '1 монетки'],
    [2, '2 монетки', '2 монетки', '2 монеток'],
    [5, '5 монеток', '5 монеток', '5 монеток'],
    [11, '11 монеток', '11 монеток', '11 монеток'],
    [21, '21 монетка', '21 монетку', '21 монетки'],
  ];

  it.each(cases)('declines %i', (n, nominative, accusative, genitive) => {
    expect(coins(n)).toBe(nominative);
    expect(coins(n, 'nominative')).toBe(nominative);
    expect(coins(n, 'accusative')).toBe(accusative);
    expect(coins(n, 'genitive')).toBe(genitive);
  });

  it('is said in its case at the call sites', () => {
    expect(ru.bets.confirm(1)).toBe('Поставить 1 монетку');
    expect(ru.bets.stakeHint(21)).toBe('От 1 до 21 монетки');
    expect(ru.shop.roll(1)).toBe('Ролл магазина за 1 монетку');
    expect(ru.shop.rollAgain(21)).toBe('Новый ролл за 21 монетку');
    expect(ru.shop.buyNamed('Птица', 1)).toBe('Купить «Птица» за 1 монетку');
    expect(ru.turn.rerollConfirm('coins', 1)).toBe('Реролл стоит 1 монетку. Потратить?');
    expect(ru.rules.roll.costCoins(1)).toBe('Дальше реролл стоит 1 монетку.');
  });
});
