import { shown } from './screenshot';

// D-121: the bug report's screenshot never draws a password field or anything marked private.

describe('screenshot filter', () => {
  afterEach(() => {
    document.body.innerHTML = '';
  });

  it('leaves out password fields and anything private', () => {
    document.body.innerHTML = `
      <form data-private><input name="login" /><span>Вход</span></form>
      <input type="password" name="pw" />
      <input type="text" name="title" />
      <p>Лидерборд</p>`;

    const [login, pw, title] = Array.from(document.querySelectorAll('input'));
    const label = document.querySelector('span');
    const board = document.querySelector('p');

    expect(shown(login as Node)).toBe(false);
    expect(shown(label as Node)).toBe(false);
    expect(shown(pw as Node)).toBe(false);
    expect(shown(title as Node)).toBe(true);
    expect(shown(board as Node)).toBe(true);
    expect(shown(document.createTextNode('текст'))).toBe(true);
  });
});
