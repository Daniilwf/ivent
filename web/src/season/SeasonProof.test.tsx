import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { Schemas } from '../api/client';
import { ru } from '../i18n/ru';
import { SeasonScreen } from './SeasonScreen';

// C8, D-98: the proof of the last completed run is sent from the season screen (`me.lastCompleted.proof`,
// `ru.proof`), with links, a note and a witness chosen from the other players of the season (D-98 (5)).

vi.mock('../api/realtime', () => ({
  watchSeason: () => () => undefined,
}));

const proofRu = () => ru.proof;

const seasonId = '5ea50000-0000-0000-0000-000000000001';
const me = '10000000-0000-0000-0000-000000000001';
const petya = '10000000-0000-0000-0000-000000000002';
const masha = '10000000-0000-0000-0000-000000000003';
const runId = 'b1000000-0000-0000-0000-000000000010';
const link = 'https://imgur.com/a/credits';

function season(
  proof: Schemas['ProofView'] | null,
  status: Schemas['RunStatus'] = 'completed',
): Schemas['SeasonView'] {
  const lastCompleted: Schemas['CompletedRunView'] = {
    id: runId,
    game: { id: 'a1000000-0000-0000-0000-000000000001', title: 'Silent Hill', hours: 6 },
    difficulty: 'normal',
    dice: [
      { sides: 4, value: 3 },
      { sides: 4, value: 1 },
    ],
    challengeDice: [],
    total: 4,
    review: null,
    proof,
    status,
  };
  return {
    id: seasonId,
    cells: [
      { id: 'start', type: 'start' },
      { id: 'c1', type: 'empty' },
      { id: 'finish', type: 'finish' },
    ],
    players: [
      { id: me, name: 'Вася', cellId: 'c1', points: 4, phase: 'idle', finishOrder: null },
      { id: petya, name: 'Петя', cellId: 'start', points: 0, phase: 'idle', finishOrder: null },
      { id: masha, name: 'Маша', cellId: 'start', points: 0, phase: 'idle', finishOrder: null },
    ],
    // The server's leaderboard (D-100): Вася by points, Петя and Маша share place 2
    leaderboard: [
      { playerId: me, place: 1, points: 4, cellsToFinish: 1, isFirst: false, provisional: false },
      {
        playerId: petya,
        place: 2,
        points: 0,
        cellsToFinish: 2,
        isFirst: false,
        provisional: false,
      },
      {
        playerId: masha,
        place: 2,
        points: 0,
        cellsToFinish: 2,
        isFirst: false,
        provisional: false,
      },
    ],
    me: {
      playerId: me,
      phase: 'idle',
      offer: null,
      choice: null,
      activeRun: null,
      lastCompleted,
      nextReroll: null,
      manualEffects: [],
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

type Handler = (request: Request) => Response | Promise<Response>;

function serve(handler: Handler) {
  const fetch = vi.fn((request: Request) => Promise.resolve(handler(request)));
  vi.stubGlobal('fetch', fetch);
  return fetch;
}

const isSeasonGet = (r: Request) =>
  r.method === 'GET' && r.url.endsWith(`/api/seasons/${seasonId}`);
const isProofPost = (r: Request) =>
  r.method === 'POST' && r.url.endsWith(`/api/seasons/${seasonId}/runs/${runId}/proof`);

describe('Proof of the last completed run', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('offers the proof form under the last completed run when no proof was sent', async () => {
    serve((r) => (isSeasonGet(r) ? json(200, season(null)) : json(404, {})));
    render(<SeasonScreen seasonId={seasonId} onSignedOut={vi.fn()} />);

    const form = await screen.findByTestId('proof-form');
    expect(within(form).getAllByTestId('proof-link')).toHaveLength(1);
    expect(within(form).getByTestId('proof-note')).toBeInTheDocument();
    expect(within(form).getByTestId('proof-submit')).toHaveTextContent(proofRu().submit);
    expect(screen.queryByTestId('proof-status')).not.toBeInTheDocument();
  });

  it('sends the links and the note of the proof, then shows it is waiting for the admin', async () => {
    let current = season(null);
    const bodies: unknown[] = [];
    serve(async (r) => {
      if (isSeasonGet(r)) return json(200, current);
      if (isProofPost(r)) {
        bodies.push(await r.json());
        current = season({ status: 'pending', links: [link], note: 'Титры', comment: null });
        return json(200, { duplicate: false, events: [] });
      }
      return json(404, {});
    });
    render(<SeasonScreen seasonId={seasonId} onSignedOut={vi.fn()} />);

    await userEvent.type(await screen.findByTestId('proof-link'), link);
    await userEvent.type(screen.getByTestId('proof-note'), 'Титры');
    await userEvent.click(screen.getByTestId('proof-submit'));

    expect(await screen.findByTestId('proof-status')).toHaveTextContent(proofRu().status.pending);
    expect(bodies).toHaveLength(1);
    expect(bodies[0]).toMatchObject({ links: [link], note: 'Титры' });
    expect((bodies[0] as { commandId: string }).commandId).toMatch(/^[0-9a-f-]{36}$/);
  });

  it('adds link fields up to five and sends only the filled ones', async () => {
    const bodies: unknown[] = [];
    serve(async (r) => {
      if (isSeasonGet(r)) return json(200, season(null));
      if (isProofPost(r)) {
        bodies.push(await r.json());
        return json(200, { duplicate: false, events: [] });
      }
      return json(404, {});
    });
    render(<SeasonScreen seasonId={seasonId} onSignedOut={vi.fn()} />);
    await screen.findByTestId('proof-form');

    const add = screen.getByRole('button', { name: proofRu().addLink });
    for (let i = 0; i < 4; i++) await userEvent.click(add);

    const fields = screen.getAllByTestId('proof-link');
    expect(fields).toHaveLength(5);
    expect(screen.queryByRole('button', { name: proofRu().addLink })).toBeNull();

    const [first, , third] = fields;
    if (!first || !third) throw new Error('Five link fields are expected.');
    await userEvent.type(first, link);
    await userEvent.type(third, 'https://youtu.be/ending');
    await userEvent.click(screen.getByTestId('proof-submit'));

    await vi.waitFor(() => {
      expect(bodies).toHaveLength(1);
    });
    expect(bodies[0]).toMatchObject({ links: [link, 'https://youtu.be/ending'] });
  });

  it('refuses a link that is not http or https without sending anything', async () => {
    const fetch = serve((r) => (isSeasonGet(r) ? json(200, season(null)) : json(404, {})));
    render(<SeasonScreen seasonId={seasonId} onSignedOut={vi.fn()} />);

    await userEvent.type(await screen.findByTestId('proof-link'), 'javascript:alert(1)');
    await userEvent.click(screen.getByTestId('proof-submit'));

    expect(await screen.findByRole('alert')).toHaveTextContent(proofRu().linkInvalid);
    expect(fetch.mock.calls.filter(([r]) => isProofPost(r))).toHaveLength(0);
  });

  it('asks for a link when none was given', async () => {
    const fetch = serve((r) => (isSeasonGet(r) ? json(200, season(null)) : json(404, {})));
    render(<SeasonScreen seasonId={seasonId} onSignedOut={vi.fn()} />);

    await userEvent.click(await screen.findByTestId('proof-submit'));

    expect(await screen.findByRole('alert')).toHaveTextContent(proofRu().linkRequired);
    expect(fetch.mock.calls.filter(([r]) => isProofPost(r))).toHaveLength(0);
  });

  it('offers the other players of the season as witnesses, not me, after «без свидетеля»', async () => {
    serve((r) => (isSeasonGet(r) ? json(200, season(null)) : json(404, {})));
    render(<SeasonScreen seasonId={seasonId} onSignedOut={vi.fn()} />);

    const select = await screen.findByTestId('proof-witness');
    const options = within(select).getAllByRole('option');
    expect(options.map((o) => o.textContent)).toEqual([proofRu().noWitness, 'Петя', 'Маша']);
    expect(options.map((o) => (o as HTMLOptionElement).value)).toEqual(['', petya, masha]);
    expect(within(select).queryByRole('option', { name: 'Вася' })).toBeNull();
    expect(select).toHaveValue('');
    expect(screen.getByLabelText(proofRu().witness)).toBe(select);
  });

  it('sends a proof with only a witness as no links and the witness id', async () => {
    // SPEC: «либо прохождение видел другой участник» — a witness alone is a proof
    const bodies: unknown[] = [];
    serve(async (r) => {
      if (isSeasonGet(r)) return json(200, season(null));
      if (isProofPost(r)) {
        bodies.push(await r.json());
        return json(200, { duplicate: false, events: [] });
      }
      return json(404, {});
    });
    render(<SeasonScreen seasonId={seasonId} onSignedOut={vi.fn()} />);

    await userEvent.selectOptions(await screen.findByTestId('proof-witness'), petya);
    await userEvent.click(screen.getByTestId('proof-submit'));

    await vi.waitFor(() => {
      expect(bodies).toHaveLength(1);
    });
    expect(bodies[0]).toMatchObject({ links: [], witnessId: petya });
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
  });

  it('sends the links together with the witness when both are given', async () => {
    const bodies: unknown[] = [];
    serve(async (r) => {
      if (isSeasonGet(r)) return json(200, season(null));
      if (isProofPost(r)) {
        bodies.push(await r.json());
        return json(200, { duplicate: false, events: [] });
      }
      return json(404, {});
    });
    render(<SeasonScreen seasonId={seasonId} onSignedOut={vi.fn()} />);

    await userEvent.type(await screen.findByTestId('proof-link'), link);
    await userEvent.selectOptions(screen.getByTestId('proof-witness'), masha);
    await userEvent.click(screen.getByTestId('proof-submit'));

    await vi.waitFor(() => {
      expect(bodies).toHaveLength(1);
    });
    expect(bodies[0]).toMatchObject({ links: [link], witnessId: masha });
  });

  it('sends no witness when «без свидетеля» stays chosen', async () => {
    const bodies: Record<string, unknown>[] = [];
    serve(async (r) => {
      if (isSeasonGet(r)) return json(200, season(null));
      if (isProofPost(r)) {
        bodies.push((await r.json()) as Record<string, unknown>);
        return json(200, { duplicate: false, events: [] });
      }
      return json(404, {});
    });
    render(<SeasonScreen seasonId={seasonId} onSignedOut={vi.fn()} />);

    await userEvent.type(await screen.findByTestId('proof-link'), link);
    await userEvent.click(screen.getByTestId('proof-submit'));

    await vi.waitFor(() => {
      expect(bodies).toHaveLength(1);
    });
    expect(bodies[0]?.witnessId ?? null).toBeNull();
  });

  it('shows the reject instead of the dice for a rejected last run', async () => {
    serve((r) =>
      isSeasonGet(r)
        ? json(
            200,
            season(
              { status: 'rejected', links: [link], note: null, comment: 'На скрине другая игра' },
              'rejected',
            ),
          )
        : json(404, {}),
    );
    render(<SeasonScreen seasonId={seasonId} onSignedOut={vi.fn()} />);

    expect(await screen.findByTestId('last-dice')).toHaveTextContent(
      ru.turn.lastRejected('Silent Hill'),
    );
    expect(screen.getByTestId('last-dice')).not.toHaveTextContent(
      ru.turn.lastDice('Silent Hill', [3, 1], 4),
    );
    expect(screen.getByTestId('proof-status')).toHaveTextContent(proofRu().status.rejected);
  });

  it('keeps the form for a pending proof, so a new one can replace it', async () => {
    serve((r) =>
      isSeasonGet(r)
        ? json(200, season({ status: 'pending', links: [link], note: null, comment: null }))
        : json(404, {}),
    );
    render(<SeasonScreen seasonId={seasonId} onSignedOut={vi.fn()} />);

    expect(await screen.findByTestId('proof-status')).toHaveTextContent(proofRu().status.pending);
    expect(screen.getByTestId('proof-form')).toBeInTheDocument();
  });

  it('shows an approved proof without the form', async () => {
    serve((r) =>
      isSeasonGet(r)
        ? json(200, season({ status: 'approved', links: [link], note: null, comment: null }))
        : json(404, {}),
    );
    render(<SeasonScreen seasonId={seasonId} onSignedOut={vi.fn()} />);

    expect(await screen.findByTestId('proof-status')).toHaveTextContent(proofRu().status.approved);
    expect(screen.queryByTestId('proof-form')).not.toBeInTheDocument();
  });

  it('shows a rejected proof with the admin comment and without the form', async () => {
    serve((r) =>
      isSeasonGet(r)
        ? json(
            200,
            season({
              status: 'rejected',
              links: [link],
              note: null,
              comment: 'На скрине другая игра',
            }),
          )
        : json(404, {}),
    );
    render(<SeasonScreen seasonId={seasonId} onSignedOut={vi.fn()} />);

    const status = await screen.findByTestId('proof-status');
    expect(status).toHaveTextContent(proofRu().status.rejected);
    expect(status).toHaveTextContent(proofRu().reviewComment('На скрине другая игра'));
    expect(screen.queryByTestId('proof-form')).not.toBeInTheDocument();
  });

  it.each([
    'proof.invalidLink',
    'proof.empty',
    'proof.witnessInvalid',
    'proof.alreadyReviewed',
    'proof.difficultyAboveClaimed',
  ])('has a Russian text for the engine refusal %s and shows it', async (code) => {
    serve((r) =>
      isSeasonGet(r)
        ? json(200, season(null))
        : json(409, { title: 'rejected', status: 409, detail: null, code }),
    );
    render(<SeasonScreen seasonId={seasonId} onSignedOut={vi.fn()} />);

    await userEvent.type(await screen.findByTestId('proof-link'), link);
    await userEvent.click(screen.getByTestId('proof-submit'));

    const text = ru.rejection[code];
    expect(text).toBeTruthy();
    expect(await screen.findByRole('alert')).toHaveTextContent(text ?? '');
  });

  it('has no proof form while nothing is completed', async () => {
    const empty = season(null);
    serve((r) =>
      isSeasonGet(r)
        ? json(200, { ...empty, me: { ...empty.me, lastCompleted: null } })
        : json(404, {}),
    );
    render(<SeasonScreen seasonId={seasonId} onSignedOut={vi.fn()} />);
    await screen.findByTestId('roll');

    expect(screen.queryByTestId('proof-form')).not.toBeInTheDocument();
    expect(screen.queryByTestId('proof-status')).not.toBeInTheDocument();
  });
});
