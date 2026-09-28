import { act, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { ru } from '../i18n/ru';
import { demoRules } from './demoRules';
import { RulesScreen } from './RulesScreen';
import { fakeServer, json } from '../test/fakeServer';

// H7: the rules page from the season's current ruleset and the history of its changes (SPEC «Правила на сайте»)

type Update = { seasonId: string; fromSequence: number; toSequence: number; types: string[] };
let notify: ((updates: Update[]) => void) | null = null;
vi.mock('../api/realtime', () => ({
  watchSeason: (_seasonId: string, onChange: (updates: Update[]) => void) => {
    notify = onChange;
    // The hub's first answer is the join
    onChange([]);
    return () => {
      notify = null;
    };
  },
}));

/** A command of the season with these events came in */
function seasonChange(...types: string[]) {
  notify?.([{ seasonId, fromSequence: 1, toSequence: 1, types }]);
}

const seasonId = '5ea50000-0000-0000-0000-000000000001';
const rulesPath = `/api/seasons/${seasonId}/rules`;
const t = ru.rules;

function serve(answer: () => Response | Promise<Response>) {
  return fakeServer({ [`GET ${rulesPath}`]: answer }).fetch;
}

function renderRules(id: string | null = seasonId) {
  const onSignedOut = vi.fn();
  render(<RulesScreen seasonId={id} onSignedOut={onSignedOut} />);
  return { onSignedOut };
}

function section(name: string) {
  return screen.getByRole('region', { name });
}

describe('the rules page', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('shows a skeleton, then every section with the version', async () => {
    serve(() => json(200, demoRules));
    renderRules();

    expect(screen.getByTestId('rules-loading')).toHaveAttribute('aria-busy', 'true');
    expect(await screen.findByText(t.version(3))).toBeInTheDocument();
    for (const name of Object.values(t.sections)) {
      expect(section(name)).toBeInTheDocument();
    }
    // The contents lead to each section
    const contents = screen.getByRole('navigation', { name: t.contents });
    expect(within(contents).getByRole('link', { name: t.sections.drop })).toHaveAttribute(
      'href',
      '#rules-drop',
    );
  });

  it('gives the real numbers: dice by difficulty, drop penalty, bonuses, the unchecked limit', async () => {
    serve(() => json(200, demoRules));
    renderRules();
    await screen.findByText(t.version(3));

    const dice = within(screen.getByTestId('rules-dice'));
    expect(dice.getByRole('row', { name: 'Лёгкая d2' })).toBeInTheDocument();
    expect(dice.getByRole('row', { name: 'Выше сложной d6 и хороший ивент' })).toBeInTheDocument();
    expect(section(t.sections.drop)).toHaveTextContent('Штраф: −2d4 очков и клеток.');
    expect(
      within(screen.getByTestId('rules-bonuses')).getAllByRole('listitem')[0],
    ).toHaveTextContent('2-е место+10 очк.');
    expect(section(t.sections.roll)).toHaveTextContent('ждут 2 прохождения');
  });

  it('shows the deadline in Moscow time, or that there is none yet', async () => {
    let rules = demoRules;
    serve(() => json(200, rules));
    renderRules();

    // The year shows only when it is not this one
    expect(await screen.findByTestId('rules-deadline')).toHaveTextContent(
      /^Дедлайн: 20 декабря( 2026 г\.)?, 23:59 МСК\.$/,
    );

    rules = { ...demoRules, deadline: null };
    act(() => {
      seasonChange('ruleset-changed');
    });
    await waitFor(() => {
      expect(screen.getByTestId('rules-deadline')).toHaveTextContent(t.deadline.none);
    });
  });

  it('lists the changes newest first with the author, the date and «было/стало» in words', async () => {
    serve(() => json(200, demoRules));
    renderRules();
    await screen.findByText(t.version(3));

    const history = within(screen.getByTestId('rules-history'));
    const versions = history.getAllByRole('heading', { level: 3 });
    const year = String.raw`( 2026 г\.)?`;
    expect(versions.map((h) => h.textContent)).toEqual([
      expect.stringMatching(new RegExp(`^Версия 3Поменял Админ, 14 октября${year}, 21:30 МСК$`)),
      expect.stringMatching(new RegExp(`^Версия 2Поменял Админ, 5 октября${year}, 12:00 МСК$`)),
      expect.stringMatching(
        new RegExp(`^Версия 1Сезон начался с этими правилами, 1 октября${year}, 15:00 МСК$`),
      ),
    ]);
    const bonus = history.getByText('Бонус за финиш: 2-е место').closest('li');
    expect(bonus).toHaveTextContent('Было: 12');
    expect(bonus).toHaveTextContent('Стало: 10');
    const limit = history.getByText('Лимит прохождений на проверке').closest('li');
    expect(limit).toHaveTextContent('Было: нет');
    expect(limit).toHaveTextContent('Стало: 2');
  });

  it('keeps a version saved without changes in the list, marked so', async () => {
    serve(() =>
      json(200, {
        ...demoRules,
        version: 4,
        history: [
          {
            version: 4,
            at: '2026-10-20T10:00:00Z',
            authorId: 'a',
            authorName: 'Админ',
            changes: [],
          },
          ...demoRules.history,
        ],
      }),
    );
    renderRules();
    await screen.findByText(t.version(4));

    const history = within(screen.getByTestId('rules-history'));
    const latest = history.getAllByRole('listitem')[0];
    expect(latest).toHaveTextContent('Версия 4');
    expect(latest).toHaveTextContent(t.history.unchanged);
  });

  it('shows the latest answer when an older one comes late', async () => {
    const answers: ((r: Response) => void)[] = [];
    serve(() => new Promise<Response>((resolve) => answers.push(resolve)));
    renderRules();
    await waitFor(() => {
      expect(answers).toHaveLength(1);
    });
    act(() => {
      seasonChange('ruleset-changed');
    });
    await waitFor(() => {
      expect(answers).toHaveLength(2);
    });

    // The newer answer (version 4) comes first, the first request's (version 3) after it
    await act(async () => {
      answers[1]?.(json(200, { ...demoRules, version: 4 }));
      await Promise.resolve();
    });
    expect(await screen.findByText(t.version(4))).toBeInTheDocument();
    await act(async () => {
      answers[0]?.(json(200, demoRules));
      await new Promise((resolve) => setTimeout(resolve, 20));
    });
    expect(screen.getByText(t.version(4))).toBeInTheDocument();
  });

  it('says the rules have not changed when there is only the start', async () => {
    serve(() => json(200, { ...demoRules, version: 1, history: demoRules.history.slice(2) }));
    renderRules();

    expect(await screen.findByText(t.history.empty)).toBeInTheDocument();
  });

  it('follows a change of the rules without a reload', async () => {
    let rules = demoRules;
    serve(() => json(200, rules));
    renderRules();
    await screen.findByText(t.version(3));

    rules = {
      ...demoRules,
      version: 4,
      ruleset: {
        ...demoRules.ruleset,
        drop: { ...demoRules.ruleset.drop, penaltyDice: { count: 3, sides: 6 } },
      },
    };
    act(() => {
      seasonChange('ruleset-changed');
    });

    expect(await screen.findByText(t.version(4))).toBeInTheDocument();
    expect(section(t.sections.drop)).toHaveTextContent('Штраф: −3d6 очков и клеток.');
  });

  it('reads the rules again only when they, the deadline or an undo change them', async () => {
    const fetched = serve(() => json(200, demoRules));
    renderRules();
    await screen.findByText(t.version(3));
    const before = fetched.mock.calls.length;

    act(() => {
      seasonChange('game-rolled', 'player-moved');
    });
    await new Promise((resolve) => setTimeout(resolve, 20));
    expect(fetched.mock.calls.length).toBe(before);

    act(() => {
      seasonChange('season-deadline-set');
    });
    await waitFor(() => {
      expect(fetched.mock.calls.length).toBe(before + 1);
    });
  });

  it('shows the error with a retry', async () => {
    let fail = true;
    serve(() => (fail ? json(500, {}) : json(200, demoRules)));
    renderRules();

    expect(await screen.findByRole('heading', { name: t.loadErrorTitle })).toBeInTheDocument();
    fail = false;
    await userEvent.click(screen.getByRole('button', { name: ru.ui.retry }));

    expect(await screen.findByText(t.version(3))).toBeInTheDocument();
  });

  it('without a season says the rules come with it and asks nothing', () => {
    const fetch = serve(() => json(200, demoRules));
    renderRules(null);

    expect(screen.getByRole('heading', { name: t.noSeasonTitle })).toBeInTheDocument();
    expect(fetch).not.toHaveBeenCalled();
  });

  it('a session that ended leads to the sign-in', async () => {
    serve(() => json(401, {}));
    const { onSignedOut } = renderRules();

    await waitFor(() => {
      expect(onSignedOut).toHaveBeenCalled();
    });
  });
});
