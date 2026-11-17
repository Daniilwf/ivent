import { act, render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { Schemas } from '../api/client';
import { ru } from '../i18n/ru';
import { fakeServer, seasonId, type Handler } from '../test/fakeServer';
import { SeasonScreen } from './SeasonScreen';

let hubChange: (() => void) | null = null;
vi.mock('../api/realtime', () => ({
  watchSeason: (_seasonId: string, onChange: (updates: unknown[]) => void) => {
    hubChange = () => {
      onChange([]);
    };
    onChange([]);
    return () => {
      hubChange = null;
    };
  },
}));

const me = '10000000-0000-0000-0000-000000000001';
const petya = 'ab200000-0000-0000-0000-00000000000f';
const answerId = petya.replaceAll('-', '').toUpperCase();
const choiceId = 'ee100000-0000-0000-0000-000000000001';
const prompt = 'Кому подарить попутный ветер?';
const pendingChoice: Schemas['ChoiceView'] = {
  id: choiceId,
  kind: 'effect',
  prompt,
  objectId: 'tailwind-gift',
  name: 'Попутный ветер',
  options: [
    { id: answerId, game: null, label: 'Петя' },
    { id: 'Себе', game: null },
  ],
};

function season(): Schemas['SeasonView'] {
  return {
    id: seasonId,
    status: 'active',
    deadline: null,
    cells: [
      { id: 'start', type: 'start' },
      { id: 'finish', type: 'finish' },
    ],
    players: [
      {
        id: me,
        name: 'Вася',
        cellId: 'start',
        points: 0,
        phase: 'idle',
        finishOrder: null,
        avatar: null,
        token: 0,
      },
      {
        id: petya,
        name: 'Петя',
        cellId: 'start',
        points: 0,
        phase: 'idle',
        finishOrder: null,
        avatar: null,
        token: 1,
      },
    ],
    leaderboard: [
      { playerId: me, place: 1, points: 0, cellsToFinish: 1, isFirst: false, provisional: false },
      {
        playerId: petya,
        place: 2,
        points: 0,
        cellsToFinish: 1,
        isFirst: false,
        provisional: false,
      },
    ],
    me: {
      playerId: me,
      phase: 'idle',
      offer: null,
      choice: pendingChoice,
      activeRun: null,
      lastCompleted: null,
      nextReroll: null,
      manualEffects: [],
      dropHintMinutes: null,
      dropPenalty: null,
      techRerollOpen: false,
      challengesEnabled: false,
      eventsEnabled: true,
      roll: null,
      unchecked: null,
      finish: null,
    },
    lastSequence: 3,
    edges: [],
    zones: [],
    mapMode: 'linear',
    name: 'Тестовый сезон',
    economy: { items: false, shop: false, bets: false },
  };
}

function open(view: Schemas['SeasonView'] = season(), choose?: Handler) {
  const model = { view };
  const server = fakeServer({
    'GET /api/auth/me': {
      id: 'user',
      login: 'vasya',
      name: 'Вася',
      role: 'player',
      mustChangePassword: false,
      avatar: null,
    },
    [`GET /api/seasons/${seasonId}/feed`]: {
      entries: [],
      nextBefore: null,
      players: [],
      games: [],
      runs: [],
    },
    [`GET /api/seasons/${seasonId}`]: () => ({ body: model.view }),
    [`POST /api/seasons/${seasonId}/choose`]:
      choose ??
      (() => {
        if (!model.view.me) throw new Error('No turn in the event choice fixture.');
        model.view = {
          ...model.view,
          lastSequence: model.view.lastSequence + 1,
          me: { ...model.view.me, choice: null },
        };
        return { body: { duplicate: false, events: [] } };
      }),
  });
  const rendered = render(<SeasonScreen seasonId={seasonId} onSignedOut={vi.fn()} />);
  return { model, server, ...rendered };
}

const submit = () => screen.getByRole('button', { name: /ответить/i });

describe('the suspended event on the season screen', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('shows the server question and label, then sends only the exact selected option on explicit submission', async () => {
    const { server } = open();
    expect(await screen.findByText(prompt)).toBeVisible();
    expect(screen.getByText('Попутный ветер')).toBeVisible();
    expect(screen.queryByTestId('roll')).not.toBeInTheDocument();
    await userEvent.click(screen.getByRole('radio', { name: 'Петя' }));
    expect(server.sent('POST', '/choose')).toHaveLength(0);

    await userEvent.click(submit());

    await vi.waitFor(() => {
      expect(server.sent('POST', '/choose')).toHaveLength(1);
    });
    const sent = server.sent('POST', '/choose')[0]?.body as Schemas['ChooseRequest'];
    expect(sent.commandId).toMatch(/^[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}$/i);
    expect(sent).toEqual({
      commandId: sent.commandId,
      choiceId,
      optionId: answerId,
    });
    expect(await screen.findByTestId('roll')).toBeVisible();
    expect(screen.queryByText(prompt)).not.toBeInTheDocument();
  });

  it('does not submit twice while the answer is pending', async () => {
    let release: (() => void) | undefined;
    const { server } = open(season(), async () => {
      await new Promise<void>((resolve) => {
        release = resolve;
      });
      return { body: { duplicate: false, events: [] } };
    });
    await userEvent.click(await screen.findByRole('radio', { name: 'Петя' }));

    await userEvent.dblClick(submit());

    expect(server.sent('POST', '/choose')).toHaveLength(1);
    expect(submit()).toBeDisabled();
    for (const radio of screen.getAllByRole('radio')) expect(radio).toBeDisabled();
    await act(async () => {
      release?.();
      await Promise.resolve();
    });
    await vi.waitFor(() => expect(submit()).toBeEnabled());
  });

  it('keeps the question and selection after a rejection and allows an explicit retry', async () => {
    const { server } = open(season(), () => ({
      status: 409,
      body: { title: 'rejected', status: 409, code: 'turn.unknownOption' },
    }));
    await userEvent.click(await screen.findByRole('radio', { name: 'Петя' }));
    await userEvent.click(submit());

    expect(await screen.findByRole('alert')).toHaveTextContent(ru.rejection['turn.unknownOption']);
    expect(screen.getByText(prompt)).toBeVisible();
    expect(screen.getByRole('radio', { name: 'Петя' })).toBeChecked();
    expect(submit()).toBeEnabled();
    await userEvent.click(submit());
    await vi.waitFor(() => {
      expect(server.sent('POST', '/choose')).toHaveLength(2);
    });
  });

  it('does not clear an unsent selection when another player updates the season', async () => {
    const { model, server } = open();
    await userEvent.click(await screen.findByRole('radio', { name: 'Петя' }));
    await vi.waitFor(() => {
      expect(hubChange).not.toBeNull();
    });
    model.view = { ...structuredClone(model.view), lastSequence: 10 };

    act(() => {
      hubChange?.();
    });

    await vi.waitFor(() => {
      expect(server.sent('GET', `/seasons/${seasonId}`).length).toBeGreaterThan(1);
    });
    expect(screen.getByRole('radio', { name: 'Петя' })).toBeChecked();
    expect(server.sent('POST', '/choose')).toHaveLength(0);
  });

  it('renders the server-held question again after reopening the screen without auto-answering it', async () => {
    const first = open();
    await userEvent.click(await screen.findByRole('radio', { name: 'Петя' }));
    first.unmount();

    const second = open();

    expect(await screen.findByText(prompt)).toBeVisible();
    expect(submit()).toBeDisabled();
    expect(second.server.sent('POST', '/choose')).toHaveLength(0);
  });

  it.each(['closing', 'active'] as const)(
    'permits an existing answer in %s even after the deadline',
    async (status) => {
      const view = season();
      view.status = status;
      view.deadline = '2001-01-01T12:00:00Z';
      const { server } = open(view);
      await userEvent.click(await screen.findByRole('radio', { name: 'Петя' }));

      await userEvent.click(submit());

      await vi.waitFor(() => {
        expect(server.sent('POST', '/choose')).toHaveLength(1);
      });
    },
  );

  it.each(['finished', 'archived'] as const)(
    'keeps the question readable in a %s season without offering a write',
    async (status) => {
      const view = season();
      view.status = status;
      const { server } = open(view);

      expect(await screen.findByText(prompt)).toBeVisible();
      for (const radio of screen.getAllByRole('radio')) expect(radio).toBeDisabled();
      expect(submit()).toBeDisabled();
      await userEvent.click(submit());
      expect(server.sent('POST', '/choose')).toHaveLength(0);
    },
  );

  it('keeps the question readable after events are switched off but cannot answer it', async () => {
    const view = season();
    if (!view.me) throw new Error('No turn in the event choice fixture.');
    view.me.eventsEnabled = false;
    const { server } = open(view);

    expect(await screen.findByText(prompt)).toBeVisible();
    for (const radio of screen.getAllByRole('radio')) expect(radio).toBeDisabled();
    expect(submit()).toBeDisabled();
    await userEvent.click(submit());
    expect(server.sent('POST', '/choose')).toHaveLength(0);
  });

  it.each([true, false])(
    'marks the drawn card as waiting only when its object matches the pending question: %s',
    async (matches) => {
      const view = season();
      if (!view.me) throw new Error('No turn in the event choice fixture.');
      view.lastSequence = 7;
      view.me.lastEvent = {
        sequence: 7,
        deck: 'good',
        kind: 'good',
        objectId: matches ? 'tailwind-gift' : 'another-event',
        name: matches ? 'Попутный ветер' : 'Другой ивент',
        description: 'Текст последнего вытянутого ивента.',
        source: 'cell',
      };
      open(view);

      expect(await screen.findByText(prompt)).toBeVisible();
      if (matches) {
        expect(screen.getByText('Ждёт твоего ответа.')).toBeVisible();
        expect(screen.queryByText(ru.events.played)).not.toBeInTheDocument();
      } else {
        await userEvent.click(screen.getByText(ru.events.history('Другой ивент')));
        expect(screen.getByText(ru.events.played)).toBeVisible();
        expect(screen.queryByText('Ждёт твоего ответа.')).not.toBeInTheDocument();
      }
    },
  );
});
