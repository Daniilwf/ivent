import { act, render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { ru } from '../i18n/ru';
import { Menu } from '../ui/Menu';
import {
  currentTheme,
  resetThemeForTests,
  setThemeChoice,
  startTheme,
  themeChoices,
  themeStorageKey,
  useTheme,
  type ThemeChoice,
} from './theme';

// D-940: the theme — «Светлая», «Тёмная», «Как в системе» (the default), kept on this device, following the system.

/** A system whose colour scheme the test sets and changes */
function fakeSystem(dark: boolean) {
  const listeners = new Set<() => void>();
  const query = {
    get matches() {
      return dark;
    },
    addEventListener: (_: string, listener: () => void) => listeners.add(listener),
    removeEventListener: (_: string, listener: () => void) => listeners.delete(listener),
  };
  vi.stubGlobal(
    'matchMedia',
    vi.fn(() => query),
  );
  return {
    set(next: boolean) {
      dark = next;
      act(() => {
        for (const listener of listeners) listener();
      });
    },
  };
}

function ThemeMenu() {
  const theme = useTheme();
  return (
    <>
      <output data-testid="shown">{`${theme.choice} ${theme.theme}`}</output>
      <Menu
        trigger={<button type="button">menu</button>}
        items={[
          {
            kind: 'choice',
            label: ru.shell.theme,
            value: theme.choice,
            options: themeChoices.map((value) => ({
              value,
              label: ru.shell.themes[value],
              testId: `theme-${value}`,
            })),
            onChange: (value) => {
              setThemeChoice(value as ThemeChoice);
            },
          },
        ]}
      />
    </>
  );
}

const html = document.documentElement;

describe('the theme', () => {
  beforeEach(() => {
    localStorage.clear();
    delete html.dataset['theme'];
    resetThemeForTests();
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('follows the system by default and stores nothing', () => {
    fakeSystem(true);
    startTheme();
    expect(currentTheme()).toBe('dark');
    expect(html.dataset['theme']).toBeUndefined();
    expect(localStorage.getItem(themeStorageKey)).toBeNull();
  });

  it.each([
    ['light', false, 'light'],
    ['light', true, 'light'],
    ['dark', false, 'dark'],
    ['dark', true, 'dark'],
    ['system', false, 'light'],
    ['system', true, 'dark'],
  ] as const)('«%s» on a system that is dark: %s shows %s', (choice, dark, shown) => {
    fakeSystem(dark);
    startTheme();
    setThemeChoice(choice);
    expect(currentTheme()).toBe(shown);
    expect(html.dataset['theme']).toBe(choice === 'system' ? undefined : choice);
  });

  it('keeps the choice on this device: a new visit starts with it', () => {
    fakeSystem(false);
    startTheme();
    setThemeChoice('dark');
    expect(localStorage.getItem(themeStorageKey)).toBe('dark');

    delete html.dataset['theme'];
    resetThemeForTests();
    startTheme();
    expect(html.dataset['theme']).toBe('dark');
    expect(currentTheme()).toBe('dark');

    setThemeChoice('system');
    expect(localStorage.getItem(themeStorageKey)).toBeNull();
  });

  it('ignores a stored value it does not know and falls back to the system', () => {
    fakeSystem(false);
    localStorage.setItem(themeStorageKey, 'sepia');
    resetThemeForTests();
    startTheme();
    expect(html.dataset['theme']).toBeUndefined();
    expect(currentTheme()).toBe('light');
  });

  it('keeps the browser scheme and bar colour in sync with the page', () => {
    const system = fakeSystem(false);
    const scheme = document.createElement('meta');
    scheme.name = 'color-scheme';
    scheme.content = 'light dark';
    const bar = document.createElement('meta');
    bar.name = 'theme-color';
    document.head.append(scheme, bar);

    try {
      html.style.setProperty('--color-page', '#eef2f7');
      startTheme();
      expect(scheme.content).toBe('light dark');
      expect(bar.content).toBe('#eef2f7');

      html.style.setProperty('--color-page', '#1a1c22');
      system.set(true);
      expect(scheme.content).toBe('light dark');
      expect(bar.content).toBe('#1a1c22');

      html.style.setProperty('--color-page', '#eef2f7');
      setThemeChoice('light');
      expect(scheme.content).toBe('light');
      expect(bar.content).toBe('#eef2f7');

      html.style.setProperty('--color-page', '#1a1c22');
      setThemeChoice('dark');
      expect(scheme.content).toBe('dark');
      expect(bar.content).toBe('#1a1c22');

      setThemeChoice('system');
      expect(scheme.content).toBe('light dark');
      expect(bar.content).toBe('#1a1c22');
    } finally {
      scheme.remove();
      bar.remove();
      html.style.removeProperty('--color-page');
    }
  });

  it('follows a change of the system while «Как в системе» is chosen, and not after another choice', () => {
    const system = fakeSystem(false);
    startTheme();
    render(<ThemeMenu />);
    expect(screen.getByTestId('shown')).toHaveTextContent('system light');

    system.set(true);
    expect(screen.getByTestId('shown')).toHaveTextContent('system dark');

    act(() => {
      setThemeChoice('light');
    });
    system.set(true);
    expect(screen.getByTestId('shown')).toHaveTextContent('light light');
  });

  it('offers the three choices in the menu, marks the current one and keeps the menu open', async () => {
    fakeSystem(false);
    startTheme();
    const person = userEvent.setup();
    render(<ThemeMenu />);
    await person.click(screen.getByRole('button', { name: 'menu' }));

    expect(screen.getByText(ru.shell.theme)).toBeInTheDocument();
    const radios = screen.getAllByRole('menuitemradio');
    expect(radios.map((r) => r.textContent)).toEqual([
      ru.shell.themes.light,
      ru.shell.themes.dark,
      ru.shell.themes.system,
    ]);
    expect(screen.getByTestId('theme-system')).toHaveAttribute('aria-checked', 'true');

    await person.click(screen.getByTestId('theme-dark'));
    expect(screen.getByTestId('theme-dark')).toHaveAttribute('aria-checked', 'true');
    expect(screen.getByTestId('theme-system')).toHaveAttribute('aria-checked', 'false');
    expect(html.dataset['theme']).toBe('dark');
    expect(localStorage.getItem(themeStorageKey)).toBe('dark');
    expect(screen.getByTestId('shown')).toHaveTextContent('dark dark');
  });
});
