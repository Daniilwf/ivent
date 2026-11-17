import { act, render, screen, within } from '@testing-library/react';
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

const playerId = '10000000-0000-0000-0000-000000000001';
const effectId = 'e5000000-0000-0000-0000-000000000001';
const manualId = 'e5000000-0000-0000-0000-000000000002';
const runId = 'b1000000-0000-0000-0000-000000000001';
const gameId = 'a1000000-0000-0000-0000-000000000001';
const fileId = '50000000-0000-0000-0000-000000000001';
const videoLink = 'https://example.com/watch?v=karaoke';
const draftLink = 'https://example.com/my-unsent-video';
const description = 'Спой песню на выбор остальных и скинь видео в галерею.';
const seasonPath = '/api/seasons/' + seasonId;
const proofPath = seasonPath + '/effects/' + effectId + '/irl-proof';

function file(): Schemas['FileLinkView'] {
  return {
    id: fileId,
    url: '/api/files/' + fileId,
    thumbnailUrl: '/api/files/' + fileId + '/thumbnail',
  };
}

function proof(overrides: Partial<Schemas['IrlProofView']> = {}): Schemas['IrlProofView'] {
  return {
    status: 'pending',
    files: [file()],
    link: videoLink,
    comment: null,
    submittedAt: '2026-11-17T16:00:00Z',
    ...overrides,
  };
}

function effect(overrides: Partial<Schemas['ManualEffectView']> = {}): Schemas['ManualEffectView'] {
  return {
    id: effectId,
    drawEvent: 'bad',
    source: 'drop',
    objectId: 'karaoke',
    irl: true,
    name: 'Караоке',
    description,
    irlProof: null,
    ...overrides,
  };
}

function activeRun(): Schemas['RunView'] {
  return {
    id: runId,
    game: { id: gameId, title: 'Silent Hill', hours: 12 },
    startedAt: '2026-11-17T12:00:00Z',
    declaredDifficulty: 'normal',
    conditions: [],
  };
}

function season(turn: Partial<Schemas['MyTurnView']> = {}): Schemas['SeasonView'] {
  return {
    id: seasonId,
    name: 'Сезон IRL',
    status: 'active',
    deadline: null,
    cells: [
      { id: 'start', type: 'start' },
      { id: 'finish', type: 'finish' },
    ],
    players: [
      {
        id: playerId,
        name: 'Вася',
        cellId: 'start',
        points: 0,
        phase: turn.phase ?? 'idle',
        finishOrder: null,
        avatar: null,
        token: 0,
      },
    ],
    leaderboard: [
      {
        playerId,
        place: 1,
        points: 0,
        cellsToFinish: 1,
        isFirst: false,
        provisional: false,
      },
    ],
    me: {
      playerId,
      phase: 'idle',
      offer: null,
      choice: null,
      activeRun: null,
      lastCompleted: null,
      nextReroll: null,
      manualEffects: [effect()],
      dropHintMinutes: null,
      dropPenalty: null,
      techRerollOpen: false,
      challengesEnabled: false,
      eventsEnabled: true,
      irlProofMaxFiles: 0,
      roll: null,
      unchecked: null,
      finish: null,
      coins: 10,
      checks: [],
      waitingEvents: [],
      waitingConditions: [],
      ...turn,
    },
    lastSequence: 3,
    edges: [],
    zones: [],
    mapMode: 'linear',
    economy: { items: false, shop: false, bets: false },
  };
}

type Model = { view: Schemas['SeasonView'] };

function updateTurn(model: Model, turn: Partial<Schemas['MyTurnView']>) {
  if (!model.view.me) throw new Error('The IRL fixture needs my turn.');
  const me = { ...model.view.me, ...turn };
  model.view = {
    ...model.view,
    me,
    lastSequence: model.view.lastSequence + 1,
    players: model.view.players.map((player) =>
      player.id === me.playerId ? { ...player, phase: me.phase } : player,
    ),
  };
}

function open(view = season(), post?: Handler) {
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
    ['GET ' + seasonPath + '/feed']: {
      entries: [],
      nextBefore: null,
      players: [],
      games: [],
      runs: [],
    },
    ['GET ' + seasonPath]: () => ({ body: model.view }),
    ['POST ' + proofPath]:
      post ??
      ((call) => {
        const body = call.body as Schemas['IrlProofRequest'];
        const before = model.view.me;
        if (!before) throw new Error('The IRL fixture needs my turn.');
        updateTurn(model, {
          manualEffects: before.manualEffects.map((task) =>
            task.id === effectId
              ? {
                  ...task,
                  irlProof: proof({
                    link: body.link ?? null,
                    files: (body.files ?? []).map((id) => ({
                      id,
                      url: '/api/files/' + id,
                      thumbnailUrl: '/api/files/' + id + '/thumbnail',
                    })),
                  }),
                }
              : task,
          ),
        });
        return { body: { duplicate: false, events: [] } };
      }),
  });
  render(<SeasonScreen seasonId={seasonId} role="player" onSignedOut={vi.fn()} />);
  return { model, server };
}

async function publish(
  model: Model,
  server: ReturnType<typeof fakeServer>,
  turn: Partial<Schemas['MyTurnView']>,
) {
  await vi.waitFor(() => {
    expect(hubChange).not.toBeNull();
  });
  const reads = server.sent('GET', seasonPath).length;
  updateTurn(model, turn);
  act(() => {
    hubChange?.();
  });
  await vi.waitFor(() => {
    expect(server.sent('GET', seasonPath).length).toBeGreaterThan(reads);
  });
}

const linkField = () => screen.getByRole('textbox', { name: /ссылка на видео/i });
const submit = () => screen.getByRole('button', { name: 'Отправить пруф' });

describe('IRL proofs on the season screen', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('does not label an object-backed IRL task with its legacy draw placeholder or another drawn event kind', async () => {
    open(
      season({
        manualEffects: [effect({ drawEvent: 'good' })],
        lastEvent: {
          sequence: 2,
          deck: 'bad',
          kind: 'bad',
          objectId: 'unrelated-draw',
          name: 'Другой плохой ивент',
          description: 'Это описание другого вытянутого ивента.',
          source: 'cell',
        },
      }),
    );

    const row = await screen.findByTestId('manual-effect-' + effectId);
    expect(within(row).getByText('Караоке')).toBeVisible();
    expect(within(row).getByText(description)).toBeVisible();
    expect(within(row).queryByText(ru.effects.drawEvent('good', 'drop'))).not.toBeInTheDocument();
    expect(within(row).queryByText(ru.effects.drawEvent('bad', 'drop'))).not.toBeInTheDocument();
    expect(within(row).queryByText(ru.events.kinds.good, { exact: true })).not.toBeInTheDocument();
    expect(within(row).queryByText(ru.events.kinds.bad, { exact: true })).not.toBeInTheDocument();
  });

  it('shows the own task and zero-file limit, then sends one exact command to that effect', async () => {
    const { server } = open();
    expect(await screen.findByText('Караоке')).toBeVisible();
    expect(screen.getByText(description)).toBeVisible();
    expect(screen.queryByTestId('irl-proof-file')).not.toBeInTheDocument();
    await userEvent.type(linkField(), '  ' + videoLink + '  ');
    expect(server.sent('POST', proofPath)).toHaveLength(0);

    await userEvent.click(submit());

    await screen.findByText('Пруф на проверке');
    const calls = server.sent('POST', proofPath);
    expect(calls).toHaveLength(1);
    const body = calls[0]?.body as Schemas['IrlProofRequest'];
    expect(body.commandId).toMatch(/^[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}$/i);
    expect(body).toEqual({ commandId: body.commandId, files: [], link: videoLink });
    expect(screen.queryByRole('textbox', { name: /ссылка на видео/i })).not.toBeInTheDocument();
  });

  it.each([null, undefined])(
    'uses link-only UI when the old view omits its file limit: %s',
    async (limit) => {
      const turn: Partial<Schemas['MyTurnView']> =
        limit === undefined ? {} : { irlProofMaxFiles: limit };
      const view = season(turn);
      if (!view.me) throw new Error('The fixture needs my turn.');
      if (limit === undefined) delete view.me.irlProofMaxFiles;
      open(view);

      expect(await screen.findByText('Караоке')).toBeVisible();
      expect(linkField()).toBeVisible();
      expect(screen.queryByTestId('irl-proof-file')).not.toBeInTheDocument();
    },
  );

  it.each(['active', 'closing'] as const)(
    'accepts a proof in %s after the deadline instead of gating it with rolls',
    async (status) => {
      const view = season();
      view.status = status;
      view.deadline = '2001-01-01T12:00:00Z';
      const { server } = open(view);
      await screen.findByText('Караоке');
      await userEvent.type(linkField(), videoLink);

      await userEvent.click(submit());

      await vi.waitFor(() => {
        expect(server.sent('POST', proofPath)).toHaveLength(1);
      });
      expect(await screen.findByText('Пруф на проверке')).toBeVisible();
    },
  );

  it.each(['finished', 'archived', 'draft'] as const)(
    'keeps an IRL task readable without an upload or proof action in %s',
    async (status) => {
      const view = season({ irlProofMaxFiles: 5 });
      view.status = status;
      const { server } = open(view);

      expect(await screen.findByText('Караоке')).toBeVisible();
      expect(screen.getByText(description)).toBeVisible();
      expect(screen.queryByTestId('irl-proof-file')).not.toBeInTheDocument();
      expect(screen.queryByRole('button', { name: 'Отправить пруф' })).not.toBeInTheDocument();
      expect(server.sent('POST', proofPath)).toHaveLength(0);
    },
  );

  it('keeps an event-switched-off task readable without enabling uploads or submit', async () => {
    const { server } = open(season({ eventsEnabled: false, irlProofMaxFiles: 5 }));

    expect(await screen.findByText('Караоке')).toBeVisible();
    expect(screen.getByText(description)).toBeVisible();
    expect(screen.queryByTestId('irl-proof-file')).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Отправить пруф' })).not.toBeInTheDocument();
    expect(server.sent('POST', proofPath)).toHaveLength(0);
  });

  it('keeps the draft after a command refusal and allows an explicit retry', async () => {
    const { server } = open(season(), () => ({
      status: 409,
      body: { title: 'rejected', status: 409, code: 'irlProof.pending' },
    }));
    await screen.findByText('Караоке');
    await userEvent.type(linkField(), draftLink);

    await userEvent.click(submit());

    expect(await screen.findByRole('alert')).toHaveTextContent(ru.rejection['irlProof.pending']);
    expect(linkField()).toHaveValue(draftLink);
    expect(submit()).toBeEnabled();
    await userEvent.click(submit());
    await vi.waitFor(() => {
      expect(server.sent('POST', proofPath)).toHaveLength(2);
    });
    expect(linkField()).toHaveValue(draftLink);
  });

  it('keeps the draft after a lost answer and does not submit twice while a request is in flight', async () => {
    let release: (() => void) | undefined;
    let first = true;
    const { server } = open(season(), async () => {
      if (first) {
        first = false;
        await new Promise<void>((resolve) => {
          release = resolve;
        });
        throw new Error('connection lost');
      }
      return { body: { duplicate: false, events: [] } };
    });
    await screen.findByText('Караоке');
    await userEvent.type(linkField(), draftLink);

    await userEvent.dblClick(submit());

    expect(server.sent('POST', proofPath)).toHaveLength(1);
    expect(submit()).toBeDisabled();
    await act(async () => {
      release?.();
      await Promise.resolve();
    });
    expect(await screen.findByRole('alert')).toHaveTextContent(ru.app.loadError);
    expect(linkField()).toHaveValue(draftLink);
    await userEvent.click(submit());
    await vi.waitFor(() => {
      expect(server.sent('POST', proofPath)).toHaveLength(2);
    });
  });

  it('retains the focused unsent IRL draft when a hub update changes idle to a playing turn', async () => {
    const { model, server } = open();
    await screen.findByText('Караоке');
    await userEvent.type(linkField(), draftLink);
    expect(linkField()).toHaveFocus();

    await publish(model, server, { phase: 'playing', activeRun: activeRun() });

    await screen.findByTestId('active-run');
    expect(linkField()).toHaveValue(draftLink);
    expect(linkField()).toHaveFocus();
    expect(server.sent('POST', proofPath)).toHaveLength(0);
  });

  it('retains the IRL draft when a non-IRL task moves the H2 manual-work slot', async () => {
    const { model, server } = open();
    await screen.findByText('Караоке');
    await userEvent.type(linkField(), draftLink);

    await publish(model, server, {
      manualEffects: [
        effect(),
        {
          id: manualId,
          drawEvent: 'bad',
          source: 'drop',
          objectId: 'toast',
          irl: false,
          name: 'Тост',
          description: 'Скажи тост в чате.',
          irlProof: null,
        },
      ],
    });

    const manual = await screen.findByTestId('manual-effect-' + manualId);
    expect(manual).toBeVisible();
    const roll = screen.getByTestId('roll');
    expect(manual.compareDocumentPosition(roll) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
    expect(linkField()).toHaveValue(draftLink);
    expect(linkField()).toHaveFocus();
    expect(server.sent('POST', proofPath)).toHaveLength(0);
  });

  it('uses updated file limits without dropping the same task draft', async () => {
    const { model, server } = open(season({ irlProofMaxFiles: 5 }));
    await screen.findByTestId('irl-proof-file');
    await userEvent.type(linkField(), draftLink);

    await publish(model, server, { irlProofMaxFiles: 0 });

    await vi.waitFor(() => expect(screen.queryByTestId('irl-proof-file')).not.toBeInTheDocument());
    expect(linkField()).toHaveValue(draftLink);
    expect(submit()).toBeEnabled();
    expect(server.sent('POST', proofPath)).toHaveLength(0);
  });

  it('shows a hub-submitted proof read-only and prefills untouched fields after its rejection', async () => {
    const { model, server } = open(season({ irlProofMaxFiles: 5 }));
    await screen.findByText('Караоке');
    expect(linkField()).toHaveValue('');

    await publish(model, server, { manualEffects: [effect({ irlProof: proof() })] });

    expect(await screen.findByText('Пруф на проверке')).toBeVisible();
    expect(screen.queryByRole('textbox', { name: /ссылка на видео/i })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Отправить пруф' })).not.toBeInTheDocument();
    await publish(model, server, {
      manualEffects: [
        effect({ irlProof: proof({ status: 'rejected', comment: 'Не слышно песню' }) }),
      ],
    });

    expect(await screen.findByText('Пруф отклонён')).toBeVisible();
    expect(screen.getByText('Не слышно песню')).toBeVisible();
    expect(linkField()).toHaveValue(videoLink);
    expect(screen.getByRole('img')).toHaveAttribute('src', file().thumbnailUrl);
    await userEvent.clear(linkField());
    await userEvent.type(linkField(), draftLink);
    await userEvent.click(submit());
    await vi.waitFor(() => {
      expect(server.sent('POST', proofPath)).toHaveLength(1);
    });
    const body = server.sent('POST', proofPath)[0]?.body as Schemas['IrlProofRequest'];
    expect(body.files).toEqual([fileId]);
    expect(body.link).toBe(draftLink);
  });

  it('does not overwrite a dirty local draft with a different hub proof and its rejection', async () => {
    const { model, server } = open(season({ irlProofMaxFiles: 5 }));
    await screen.findByText('Караоке');
    await userEvent.type(linkField(), draftLink);

    await publish(model, server, { manualEffects: [effect({ irlProof: proof() })] });
    await screen.findByText('Пруф на проверке');
    await publish(model, server, {
      manualEffects: [
        effect({ irlProof: proof({ status: 'rejected', comment: 'Нужно новое видео' }) }),
      ],
    });

    await screen.findByText('Пруф отклонён');
    expect(linkField()).toHaveValue(draftLink);
    expect(server.sent('POST', proofPath)).toHaveLength(0);
  });

  it('uses server proof states and removes the task after approval instead of inventing a local approved card', async () => {
    const { model, server } = open(season(), () => ({ body: { duplicate: false, events: [] } }));
    await screen.findByText('Караоке');
    await userEvent.type(linkField(), videoLink);
    await userEvent.click(submit());
    await vi.waitFor(() => {
      expect(server.sent('POST', proofPath)).toHaveLength(1);
    });
    await vi.waitFor(() => expect(submit()).toBeEnabled());
    expect(screen.queryByText('Пруф на проверке')).not.toBeInTheDocument();
    expect(linkField()).toHaveValue(videoLink);

    await publish(model, server, { manualEffects: [effect({ irlProof: proof({ files: [] }) })] });
    await screen.findByText('Пруф на проверке');
    await publish(model, server, { manualEffects: [] });

    await vi.waitFor(() => expect(screen.queryByText('Караоке')).not.toBeInTheDocument());
    expect(screen.queryByRole('textbox', { name: /ссылка на видео/i })).not.toBeInTheDocument();
    expect(screen.queryByText('Пруф одобрен')).not.toBeInTheDocument();
    expect(server.sent('POST', proofPath)).toHaveLength(1);
  });
});
