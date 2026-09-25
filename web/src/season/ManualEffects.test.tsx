import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { Schemas } from '../api/client';
import { ru } from '../i18n/ru';
import { SeasonScreen } from './SeasonScreen';

// Resolving manual effects on the season screen (C11a, E1, D-102): «Применено» sends at once, with the comment if one is
// typed; «Не применимо» needs a comment first; the engine's refusal is shown in Russian.

vi.mock('../api/realtime', () => ({
  watchSeason: () => () => {},
}));

const seasonId = '5ea50000-0000-0000-0000-000000000001';
const me = '10000000-0000-0000-0000-000000000001';
const effectId = 'e1000000-0000-0000-0000-000000000001';

function season(status: Schemas['SeasonView']['status'] = 'active'): Schemas['SeasonView'] {
  return {
    id: seasonId,
    status,
    deadline: null,
    cells: [
      { id: 'start', type: 'start' },
      { id: 'finish', type: 'finish' },
    ],
    players: [
      { id: me, name: 'Вася', cellId: 'start', points: 0, phase: 'idle', finishOrder: null },
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
      manualEffects: [{ id: effectId, drawEvent: 'bad', source: 'drop' }],
      dropHintMinutes: null,
      dropPenalty: null,
      techRerollOpen: false,
      challengesEnabled: false,
      finish: null,
    },
    lastSequence: 3,
  };
}

function json(status: number, body: unknown) {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': status >= 400 ? 'application/problem+json' : 'application/json' },
  });
}

const resolveUrl = `/api/seasons/${seasonId}/effects/${effectId}/resolve`;

/** Serves the season; records the bodies sent to the resolve endpoint and answers with `answer`. */
function serve(
  answer: () => Response = () => json(200, { duplicate: false, events: [] }),
  status: Schemas['SeasonView']['status'] = 'active',
) {
  const sent: unknown[] = [];
  vi.stubGlobal(
    'fetch',
    vi.fn(async (request: Request) => {
      if (request.method === 'GET') return json(200, season(status));
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

  it('sends the typed comment with «применено»', async () => {
    const sent = serve();
    render(<SeasonScreen seasonId={seasonId} onSignedOut={vi.fn()} />);

    await userEvent.type(
      await screen.findByTestId(`manual-effect-comment-${effectId}`),
      '  разыграли  ',
    );
    await userEvent.click(screen.getByTestId(`manual-effect-applied-${effectId}`));

    await vi.waitFor(() => {
      expect(sent).toHaveLength(1);
    });
    expect(sent[0]).toMatchObject({ outcome: 'applied', comment: 'разыграли' });
  });

  it('offers «не применимо» only with a comment', async () => {
    const sent = serve();
    render(<SeasonScreen seasonId={seasonId} onSignedOut={vi.fn()} />);

    const notApplicable = await screen.findByTestId(`manual-effect-not-applicable-${effectId}`);
    expect(notApplicable).toBeDisabled();
    await userEvent.type(screen.getByTestId(`manual-effect-comment-${effectId}`), '   ');
    expect(notApplicable).toBeDisabled();

    await userEvent.type(screen.getByTestId(`manual-effect-comment-${effectId}`), 'Колода пуста');
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

  it('labels the comment field and both buttons', async () => {
    serve();
    render(<SeasonScreen seasonId={seasonId} onSignedOut={vi.fn()} />);

    expect(await screen.findByLabelText(ru.effects.comment)).toBeInTheDocument();
    expect(screen.getByRole('button', { name: ru.effects.applied })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: ru.effects.notApplicable })).toBeInTheDocument();
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

    const button = await screen.findByTestId(`manual-effect-not-applicable-${effectId}`);
    expect(button).toHaveAccessibleDescription(ru.effects.commentNeeded);
    expect(screen.getByText(ru.effects.commentNeeded)).toBeVisible();
  });
});
