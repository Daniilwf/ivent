import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { Schemas } from '../api/client';
import { answer, fakeServer, json, seasonId } from '../test/fakeServer';
import { PlayerSuggestions, type PlayerSuggestionsProps } from './PlayerSuggestions';

type Update = { seasonId: string; fromSequence: number; toSequence: number; types: string[] };
const listeners = new Set<(updates: Update[]) => void>();
vi.mock('../api/realtime', () => ({
  watchSeason: (_id: string, notify: (updates: Update[]) => void) => {
    listeners.add(notify);
    notify([]);
    return () => listeners.delete(notify);
  },
}));

const prefix = `/api/seasons/${seasonId}`;
const props: PlayerSuggestionsProps = {
  seasonId,
  role: 'player',
  participating: true,
  seasonStatus: 'active',
  onSignedOut: vi.fn(),
};
const rules = (enabled = true) => ({
  version: 1,
  ruleset: { features: { suggestions: enabled }, suggestions: { perDay: 5 } },
  history: [],
});
function idea(overrides: Partial<Schemas['SuggestionView']> = {}): Schemas['SuggestionView'] {
  return {
    id: '10000000-0000-0000-0000-000000000001',
    playerId: '20000000-0000-0000-0000-000000000001',
    kind: 'event',
    text: 'Вечер коротких игр',
    game: null,
    status: 'pending',
    comment: null,
    submittedAt: '2026-11-20T12:00:00Z',
    ...overrides,
  };
}
async function openForm() {
  await userEvent.click(await screen.findByText('Предложить игру, ивент или предмет'));
}
function update(types: string[]) {
  act(() => {
    for (const notify of listeners) notify([{ seasonId, fromSequence: 1, toSequence: 2, types }]);
  });
}

describe('the player suggestion box', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
    vi.clearAllMocks();
  });

  it.each(['spectator', 'admin'] as const)('does not read private suggestions for %s', (role) => {
    const server = fakeServer({});
    render(<PlayerSuggestions {...props} role={role} />);
    expect(server.calls).toHaveLength(0);
    expect(screen.queryByText('Предложка')).not.toBeInTheDocument();
  });

  it('is hidden when suggestions are disabled and never requests the private list', async () => {
    const server = fakeServer({ [`GET ${prefix}/rules`]: rules(false) });
    render(<PlayerSuggestions {...props} />);
    await waitFor(() => {
      expect(server.sent('GET', '/rules')).toHaveLength(1);
    });
    await waitFor(() => expect(screen.queryByTestId('loading')).not.toBeInTheDocument());
    expect(screen.queryByText('Предложка')).not.toBeInTheDocument();
    expect(server.sent('GET', '/suggestions')).toHaveLength(0);
  });

  it('does not read the box for a player who is not in the selected season', () => {
    const server = fakeServer({});
    render(<PlayerSuggestions {...props} participating={false} />);
    expect(server.calls).toHaveLength(0);
  });

  it('submits an event, clears the form only after acceptance and shows its own pending item', async () => {
    let submitted = false;
    const server = fakeServer({
      [`GET ${prefix}/rules`]: rules(),
      [`GET ${prefix}/suggestions`]: () => ({ body: submitted ? [idea()] : [] }),
      [`POST ${prefix}/suggestions`]: () => {
        submitted = true;
        return { body: { duplicate: false, events: [] } };
      },
    });
    render(<PlayerSuggestions {...props} />);
    await openForm();
    await userEvent.selectOptions(
      screen.getByRole('combobox', { name: 'Что предлагаешь' }),
      'event',
    );
    await userEvent.type(
      screen.getByRole('textbox', { name: 'Описание предложения' }),
      'Вечер коротких игр',
    );
    await userEvent.click(screen.getByRole('button', { name: 'Отправить предложение' }));
    expect(await screen.findByText('Предложение отправлено')).toBeVisible();
    expect(await screen.findByText('Вечер коротких игр')).toBeVisible();
    expect(screen.getByText('На рассмотрении')).toBeVisible();
    expect(server.sent('POST', '/suggestions')[0]?.body).toMatchObject({
      kind: 'event',
      text: 'Вечер коротких игр',
      game: null,
    });
    expect(screen.getByRole('textbox', { name: 'Описание предложения' })).toHaveValue('');
  });

  it('sends the generated game-card shape rather than a text-only game proposal', async () => {
    const server = fakeServer({
      [`GET ${prefix}/rules`]: rules(),
      [`GET ${prefix}/suggestions`]: [],
      [`POST ${prefix}/suggestions`]: { duplicate: false, events: [] },
    });
    render(<PlayerSuggestions {...props} />);
    await openForm();
    await userEvent.type(screen.getByRole('textbox', { name: 'Название игры' }), 'Portal 2');
    await userEvent.type(
      screen.getByRole('textbox', { name: 'Категории через запятую' }),
      'Головоломка, Кооп',
    );
    expect(screen.getByLabelText('Длина игры, часы')).not.toBeVisible();
    await userEvent.click(screen.getByText('Дополнить карточку игры'));
    fireEvent.change(screen.getByRole('spinbutton', { name: 'Длина игры, часы' }), {
      target: { value: '8.5' },
    });
    await userEvent.click(screen.getByRole('checkbox', { name: 'Кооперативная игра' }));
    await userEvent.click(screen.getByRole('button', { name: 'Отправить предложение' }));
    await waitFor(() => {
      expect(server.sent('POST', '/suggestions')).toHaveLength(1);
    });
    expect(server.sent('POST', '/suggestions')[0]?.body).toMatchObject({
      kind: 'game',
      game: {
        title: 'Portal 2',
        tags: ['Головоломка', 'Кооп'],
        hours: 8.5,
        isCoop: true,
        coverFileId: null,
      },
    });
  });

  it('preserves the typed item and shows the engine refusal after a failed submission', async () => {
    fakeServer({
      [`GET ${prefix}/rules`]: rules(),
      [`GET ${prefix}/suggestions`]: [],
      [`POST ${prefix}/suggestions`]: answer(409, { code: 'suggestion.dailyLimit' }),
    });
    render(<PlayerSuggestions {...props} />);
    await openForm();
    await userEvent.selectOptions(
      screen.getByRole('combobox', { name: 'Что предлагаешь' }),
      'item',
    );
    const text = screen.getByRole('textbox', { name: 'Описание предложения' });
    await userEvent.type(text, 'Предмет для возврата к развилке');
    await userEvent.click(screen.getByRole('button', { name: 'Отправить предложение' }));
    expect(await screen.findByRole('alert')).toHaveTextContent(
      'На сегодня лимит предложений исчерпан',
    );
    expect(text).toHaveValue('Предмет для возврата к развилке');
  });

  it('keeps a pending draft visible and sends only once until the server accepts it', async () => {
    let accept: ((response: Response) => void) | undefined;
    const server = fakeServer({
      [`GET ${prefix}/rules`]: rules(),
      [`GET ${prefix}/suggestions`]: [],
      [`POST ${prefix}/suggestions`]: () =>
        new Promise<Response>((resolve) => {
          accept = resolve;
        }),
    });
    render(<PlayerSuggestions {...props} />);
    await openForm();
    const title = screen.getByRole('textbox', { name: 'Название игры' });
    await userEvent.type(title, 'Игра до ответа сервера');
    const submit = screen.getByRole('button', { name: 'Отправить предложение' });
    await userEvent.dblClick(submit);
    expect(server.sent('POST', '/suggestions')).toHaveLength(1);
    expect(title).toHaveValue('Игра до ответа сервера');
    expect(title).toBeDisabled();
    act(() => accept?.(json(200, { duplicate: false, events: [] })));
    expect(await screen.findByText('Предложение отправлено')).toBeVisible();
    expect(title).toHaveValue('');
  });

  it('retries a lost answer with the same command and preserves all game materials', async () => {
    let lost = true;
    const server = fakeServer({
      [`GET ${prefix}/rules`]: rules(),
      [`GET ${prefix}/suggestions`]: [],
      [`POST ${prefix}/suggestions`]: () => {
        if (lost) throw new TypeError('Connection lost');
        return json(200, { duplicate: true, events: [] });
      },
    });
    render(<PlayerSuggestions {...props} />);
    await openForm();
    await userEvent.type(
      screen.getByRole('textbox', { name: 'Название игры' }),
      'Повтор без дубля',
    );
    await userEvent.click(screen.getByRole('button', { name: 'Отправить предложение' }));
    await screen.findByText(
      'Не удалось отправить предложение. Твой черновик сохранён — попробуй ещё раз.',
    );
    expect(screen.getByRole('textbox', { name: 'Название игры' })).toHaveValue('Повтор без дубля');
    lost = false;
    await userEvent.click(screen.getByRole('button', { name: 'Отправить предложение' }));
    await screen.findByText('Предложение отправлено');
    const [first, repeated] = server.sent('POST', '/suggestions');
    expect(repeated?.body).toEqual(first?.body);
  });

  it('keeps keyboard focus on an enabled form control after successful submission clears the required fields', async () => {
    let accept: ((response: Response) => void) | undefined;
    const server = fakeServer({
      [`GET ${prefix}/rules`]: rules(),
      [`GET ${prefix}/suggestions`]: [],
      [`POST ${prefix}/suggestions`]: () =>
        new Promise<Response>((resolve) => {
          accept = resolve;
        }),
    });
    render(<PlayerSuggestions {...props} />);
    await openForm();
    await userEvent.type(
      screen.getByRole('textbox', { name: 'Название игры' }),
      'Клавиатурная отправка',
    );
    const submit = screen.getByRole('button', { name: 'Отправить предложение' });
    submit.focus();
    await userEvent.keyboard('{Enter}');
    await waitFor(() => {
      expect(server.sent('POST', '/suggestions')).toHaveLength(1);
    });
    act(() => accept?.(json(200, { duplicate: false, events: [] })));
    await screen.findByText('Предложение отправлено');
    expect(screen.getByRole('combobox', { name: 'Что предлагаешь' })).toHaveFocus();
    expect(submit).toBeDisabled();
  });

  it('does not take focus back when the user leaves a pending submission', async () => {
    let accept: ((response: Response) => void) | undefined;
    const server = fakeServer({
      [`GET ${prefix}/rules`]: rules(),
      [`GET ${prefix}/suggestions`]: [],
      [`POST ${prefix}/suggestions`]: () =>
        new Promise<Response>((resolve) => {
          accept = resolve;
        }),
    });
    render(
      <>
        <PlayerSuggestions {...props} />
        <button>Вернуться к карте</button>
      </>,
    );
    await openForm();
    await userEvent.type(
      screen.getByRole('textbox', { name: 'Название игры' }),
      'Не перехватывать фокус',
    );
    screen.getByRole('button', { name: 'Отправить предложение' }).focus();
    await userEvent.keyboard('{Enter}');
    await waitFor(() => {
      expect(server.sent('POST', '/suggestions')).toHaveLength(1);
    });
    const other = screen.getByRole('button', { name: 'Вернуться к карте' });
    other.focus();
    act(() => accept?.(json(200, { duplicate: false, events: [] })));
    await screen.findByText('Предложение отправлено');
    expect(other).toHaveFocus();
  });

  it('keeps keyboard focus on the retry action after the server refuses submission', async () => {
    let refuse: ((response: Response) => void) | undefined;
    const server = fakeServer({
      [`GET ${prefix}/rules`]: rules(),
      [`GET ${prefix}/suggestions`]: [],
      [`POST ${prefix}/suggestions`]: () =>
        new Promise<Response>((resolve) => {
          refuse = resolve;
        }),
    });
    render(<PlayerSuggestions {...props} />);
    await openForm();
    await userEvent.type(
      screen.getByRole('textbox', { name: 'Название игры' }),
      'Повтор с клавиатуры',
    );
    const submit = screen.getByRole('button', { name: 'Отправить предложение' });
    submit.focus();
    await userEvent.keyboard('{Enter}');
    await waitFor(() => {
      expect(server.sent('POST', '/suggestions')).toHaveLength(1);
    });
    act(() => refuse?.(json(409, { code: 'suggestion.dailyLimit' })));
    await screen.findByRole('alert');
    expect(submit).toBeEnabled();
    expect(submit).toHaveFocus();
  });

  it('hides the mechanic if it is disabled between reading the rules and submission', async () => {
    let enabled = true;
    fakeServer({
      [`GET ${prefix}/rules`]: () => ({ body: rules(enabled) }),
      [`GET ${prefix}/suggestions`]: [],
      [`POST ${prefix}/suggestions`]: () => {
        enabled = false;
        return json(409, { code: 'feature.disabled' });
      },
    });
    render(<PlayerSuggestions {...props} />);
    await openForm();
    await userEvent.type(screen.getByRole('textbox', { name: 'Название игры' }), 'Флаг изменился');
    await userEvent.click(screen.getByRole('button', { name: 'Отправить предложение' }));
    await waitFor(() => expect(screen.queryByTestId('player-suggestions')).not.toBeInTheDocument());
  });

  it('uses a new command when the player edits a proposal after a lost answer', async () => {
    let lost = true;
    const server = fakeServer({
      [`GET ${prefix}/rules`]: rules(),
      [`GET ${prefix}/suggestions`]: [],
      [`POST ${prefix}/suggestions`]: () => {
        if (lost) throw new TypeError('Connection lost');
        return json(200, { duplicate: false, events: [] });
      },
    });
    render(<PlayerSuggestions {...props} />);
    await openForm();
    const title = screen.getByRole('textbox', { name: 'Название игры' });
    await userEvent.type(title, 'Первый вариант');
    await userEvent.click(screen.getByRole('button', { name: 'Отправить предложение' }));
    await screen.findByText(
      'Не удалось отправить предложение. Твой черновик сохранён — попробуй ещё раз.',
    );
    await userEvent.clear(title);
    await userEvent.type(title, 'Исправленный вариант');
    lost = false;
    await userEvent.click(screen.getByRole('button', { name: 'Отправить предложение' }));
    await screen.findByText('Предложение отправлено');
    const [first, changed] = server.sent('POST', '/suggestions');
    const firstBody = first?.body as Schemas['SubmitSuggestionRequest'];
    const changedBody = changed?.body as Schemas['SubmitSuggestionRequest'];
    expect(changedBody.commandId).not.toEqual(firstBody.commandId);
    expect(changedBody.game?.title).toBe('Исправленный вариант');
  });

  it('shows a private moderation result and never offers submission in an archived season', async () => {
    fakeServer({
      [`GET ${prefix}/rules`]: rules(),
      [`GET ${prefix}/suggestions`]: [
        idea({ status: 'rejected', comment: 'Уточни условия и награду.' }),
      ],
    });
    render(<PlayerSuggestions {...props} seasonStatus="archived" />);
    expect(await screen.findByText('Уточни условия и награду.')).toBeVisible();
    expect(screen.getByText('Ответ админа')).toBeVisible();
    expect(screen.getByText('Отклонено')).toBeVisible();
    expect(screen.queryByRole('button', { name: 'Отправить предложение' })).not.toBeInTheDocument();
  });

  it('loads moderation live without rereading rules for unrelated changes or losing a draft', async () => {
    let reviewed = false;
    const server = fakeServer({
      [`GET ${prefix}/rules`]: rules(),
      [`GET ${prefix}/suggestions`]: () => ({
        body: [idea({ status: reviewed ? 'approved' : 'pending' })],
      }),
    });
    render(<PlayerSuggestions {...props} />);
    await openForm();
    await userEvent.type(screen.getByRole('textbox', { name: 'Название игры' }), 'Будущая игра');
    await screen.findByText('На рассмотрении');
    update(['run-completed']);
    reviewed = true;
    update(['suggestion-reviewed']);
    expect(await screen.findByText('Одобрено')).toBeVisible();
    expect(screen.getByRole('textbox', { name: 'Название игры' })).toHaveValue('Будущая игра');
    expect(server.sent('GET', '/rules')).toHaveLength(1);
  });

  it('restores its list and unsent draft after a disabled read and a newer enabled rules version', async () => {
    let rulesVersion = 1;
    let available = false;
    let refuse: ((response: Response) => void) | undefined;
    const server = fakeServer({
      [`GET ${prefix}/rules`]: () => ({ body: { ...rules(), version: rulesVersion } }),
      [`GET ${prefix}/suggestions`]: () =>
        available
          ? json(200, [idea()])
          : new Promise<Response>((resolve) => {
              refuse = resolve;
            }),
    });
    render(<PlayerSuggestions {...props} />);
    await openForm();
    await userEvent.type(
      screen.getByRole('textbox', { name: 'Название игры' }),
      'Неотправленная игра',
    );
    act(() => refuse?.(json(409, { code: 'feature.disabled' })));
    await waitFor(() => expect(screen.queryByTestId('player-suggestions')).not.toBeInTheDocument());
    // Both flag changes happened on the server before this tab could render the disabled version.
    rulesVersion = 3;
    available = true;
    update(['ruleset-changed']);
    expect(await screen.findByText('Вечер коротких игр')).toBeVisible();
    await openForm();
    expect(screen.getByRole('textbox', { name: 'Название игры' })).toHaveValue(
      'Неотправленная игра',
    );
    expect(server.sent('GET', '/suggestions')).toHaveLength(2);
    expect(server.sent('GET', '/rules')).toHaveLength(2);
  });

  it('retries an initial read failure and keeps the game form independent of the list load', async () => {
    let failed = true;
    fakeServer({
      [`GET ${prefix}/rules`]: rules(),
      [`GET ${prefix}/suggestions`]: () => (failed ? json(500) : json(200, [idea()])),
    });
    render(<PlayerSuggestions {...props} />);
    await openForm();
    await userEvent.type(
      screen.getByRole('textbox', { name: 'Название игры' }),
      'Сохранённый черновик',
    );
    await screen.findByText('Твои предложения не загрузились');
    failed = false;
    await userEvent.click(screen.getByRole('button', { name: 'Попробовать ещё раз' }));
    expect(await screen.findByText('Вечер коротких игр')).toBeVisible();
    expect(screen.getByRole('textbox', { name: 'Название игры' })).toHaveValue(
      'Сохранённый черновик',
    );
  });
});
