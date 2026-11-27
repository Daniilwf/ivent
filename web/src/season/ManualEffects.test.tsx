import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { Schemas } from '../api/client';
import { ru } from '../i18n/ru';
import { SeasonScreen } from './SeasonScreen';
import { json } from '../test/fakeServer';

// Resolving manual effects on the season screen (C11a, E1, D-102): «Разыграно» sends at once.
// «Не подходит…» asks for a comment. The engine's refusal is shown in Russian.

vi.mock('../api/realtime', () => ({
  watchSeason: () => () => {},
}));

const seasonId = '5ea50000-0000-0000-0000-000000000001';
const me = '10000000-0000-0000-0000-000000000001';
const effectId = 'e1000000-0000-0000-0000-000000000001';

function season(
  status: Schemas['SeasonView']['status'] = 'active',
  effect: Schemas['ManualEffectView'] = {
    id: effectId,
    drawEvent: 'bad',
    source: 'drop',
    irl: false,
  },
): Schemas['SeasonView'] {
  return {
    id: seasonId,
    status,
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
    ],
    leaderboard: [
      { playerId: me, place: 1, points: 0, cellsToFinish: 1, isFirst: false, provisional: false },
    ],
    me: {
      playerId: me,
      phase: 'idle',
      offer: null,
      choice: null,
      activeRun: null,
      lastCompleted: null,
      nextReroll: null,
      manualEffects: [effect],
      dropHintMinutes: null,
      dropPenalty: null,
      techRerollOpen: false,
      challengesEnabled: false,
      roll: null,
      unchecked: null,
      finish: null,
    },
    lastSequence: 3,
    edges: [],
    zones: [],
    mapMode: 'linear',
    name: 'Тестовый сезон',
  };
}

const resolveUrl = `/api/seasons/${seasonId}/effects/${effectId}/resolve`;

/** Serves the season; records the bodies sent to the resolve endpoint and answers with `answer`. */
function serve(
  answer: () => Response = () => json(200, { duplicate: false, events: [] }),
  status: Schemas['SeasonView']['status'] = 'active',
  effect?: Schemas['ManualEffectView'],
) {
  const sent: unknown[] = [];
  vi.stubGlobal(
    'fetch',
    vi.fn(async (request: Request) => {
      if (request.method === 'GET') return json(200, season(status, effect));
      if (request.url.endsWith(resolveUrl)) {
        sent.push(await request.json());
        return answer();
      }
      return json(404, {});
    }),
  );
  return sent;
}

describe('Manual effects', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('offers no resolution of an IRL effect: only the admin resolves it (D-820)', async () => {
    serve(undefined, 'active', {
      id: effectId,
      drawEvent: 'bad',
      source: 'event',
      objectId: 'karaoke',
      irl: true,
      irlProof: null,
    });
    render(<SeasonScreen seasonId={seasonId} onSignedOut={vi.fn()} />);

    expect(await screen.findByTestId(`manual-effect-${effectId}`)).toBeInTheDocument();
    expect(screen.queryByTestId(`manual-effect-applied-${effectId}`)).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Не подходит…' })).not.toBeInTheDocument();
    expect(
      screen.queryByTestId(`manual-effect-not-applicable-${effectId}`),
    ).not.toBeInTheDocument();
  });

  it('applies an effect without a comment', async () => {
    const sent = serve();
    render(<SeasonScreen seasonId={seasonId} onSignedOut={vi.fn()} />);

    await userEvent.click(await screen.findByTestId(`manual-effect-applied-${effectId}`));

    await vi.waitFor(() => {
      expect(sent).toHaveLength(1);
    });
    expect(sent[0]).toMatchObject({ outcome: 'applied', comment: null });
    expect((sent[0] as { commandId: string }).commandId).toMatch(/^[0-9a-f-]{36}$/);
  });

  it('applies directly after the player abandons an unsuitable reason', async () => {
    const sent = serve();
    render(<SeasonScreen seasonId={seasonId} onSignedOut={vi.fn()} />);

    await userEvent.click(await screen.findByRole('button', { name: 'Не подходит…' }));
    await userEvent.type(screen.getByTestId(`manual-effect-comment-${effectId}`), 'Колода пуста');
    await userEvent.keyboard('{Escape}');
    expect(sent).toHaveLength(0);
    await userEvent.click(screen.getByTestId(`manual-effect-applied-${effectId}`));

    await vi.waitFor(() => {
      expect(sent).toHaveLength(1);
    });
    expect(sent[0]).toMatchObject({ outcome: 'applied', comment: null });
  });

  it('submits «не применимо» from the dialog with a trimmed comment', async () => {
    const sent = serve();
    render(<SeasonScreen seasonId={seasonId} onSignedOut={vi.fn()} />);

    await userEvent.click(await screen.findByRole('button', { name: 'Не подходит…' }));
    const dialog = within(screen.getByRole('dialog', { name: 'Ивент не подходит' }));
    const notApplicable = dialog.getByTestId(`manual-effect-not-applicable-${effectId}`);
    expect(notApplicable).toBeDisabled();
    const comment = dialog.getByTestId(`manual-effect-comment-${effectId}`);
    await userEvent.type(comment, '   ');
    expect(notApplicable).toBeDisabled();

    await userEvent.type(comment, 'Колода пуста  ');
    expect(notApplicable).toBeEnabled();
    await userEvent.click(notApplicable);

    await vi.waitFor(() => {
      expect(sent).toHaveLength(1);
    });
    expect(sent[0]).toMatchObject({ outcome: 'notApplicable', comment: 'Колода пуста' });
  });

  it('shows the refusal in Russian', async () => {
    serve(() => json(409, { title: 'rejected', status: 409, code: 'effect.notPending' }));
    render(<SeasonScreen seasonId={seasonId} onSignedOut={vi.fn()} />);

    await userEvent.click(await screen.findByTestId(`manual-effect-applied-${effectId}`));

    expect(await screen.findByRole('alert')).toHaveTextContent(ru.rejection['effect.notPending']);
  });

  it('labels the task buttons and the comment field in the dialog', async () => {
    serve();
    render(<SeasonScreen seasonId={seasonId} onSignedOut={vi.fn()} />);

    expect(await screen.findByRole('button', { name: 'Разыграно' })).toBeInTheDocument();
    expect(screen.queryByLabelText(ru.effects.comment)).not.toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: 'Не подходит…' }));

    const dialog = within(screen.getByRole('dialog', { name: 'Ивент не подходит' }));
    expect(dialog.getByLabelText(ru.effects.comment)).toBeInTheDocument();
    expect(dialog.getByRole('button', { name: ru.effects.notApplicable })).toBeInTheDocument();
  });

  it.each(['finished', 'archived'] as const)(
    'shows the effect without the buttons once the season is %s',
    async (status) => {
      serve(undefined, status);
      render(<SeasonScreen seasonId={seasonId} onSignedOut={vi.fn()} />);

      expect(await screen.findByTestId(`manual-effect-${effectId}`)).toHaveTextContent(
        ru.effects.drawEvent('bad', 'drop'),
      );
      expect(screen.queryByTestId(`manual-effect-applied-${effectId}`)).toBeNull();
      expect(screen.queryByTestId(`manual-effect-not-applicable-${effectId}`)).toBeNull();
      expect(screen.queryByRole('button', { name: 'Не подходит…' })).toBeNull();
    },
  );

  it('resolves while the season is closing', async () => {
    serve(undefined, 'closing');
    render(<SeasonScreen seasonId={seasonId} onSignedOut={vi.fn()} />);

    expect(await screen.findByTestId(`manual-effect-applied-${effectId}`)).toBeEnabled();
  });

  it('says in plain text that «не применимо» needs a comment', async () => {
    serve();
    render(<SeasonScreen seasonId={seasonId} onSignedOut={vi.fn()} />);

    await userEvent.click(await screen.findByRole('button', { name: 'Не подходит…' }));
    expect(screen.getByRole('textbox', { name: ru.effects.comment })).toHaveAccessibleDescription(
      ru.effects.commentNeeded,
    );
    expect(screen.getByText(ru.effects.commentNeeded)).toBeVisible();
  });
});
