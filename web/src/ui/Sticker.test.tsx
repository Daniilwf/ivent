import { render } from '@testing-library/react';
import { initial } from './initial';
import { Sticker } from './Sticker';

// K1A: the stand-in of an avatar is the first character of the name as the reader sees it, an emoji whole
// (DESIGN: emoji and Cyrillic in names), never half of a surrogate pair.

describe('the first character of a name', () => {
  it.each([
    ['Вася', 'В'],
    ['ёж', 'ё'],
    ['🔥Вася', '🔥'],
    ['👨‍👩‍👧‍👦🔥💀', '👨‍👩‍👧‍👦'],
    ['❤️ Маша', '❤️'],
    ['  Петя', 'П'],
    ['', ''],
  ])('of %s is %s', (name, expected) => {
    expect(initial(name)).toBe(expected);
  });

  it('is what the sticker shows', () => {
    const { container } = render(<Sticker player={{ name: '🔥Вася', token: 1, avatar: null }} />);
    expect(container.textContent).toBe('🔥');
  });
});
