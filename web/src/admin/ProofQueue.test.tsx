import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { Schemas } from '../api/client';
import { ru } from '../i18n/ru';
import { AdminScreen } from './AdminScreen';
import { adminUser, answer, fakeServer, seasonId } from './fakeServer';

// H8: the proof queue (SPEC «Очередь пруфов», D-98, D-134): finishes on top as the server orders them, the players
// whose roll the unchecked limit closed are marked; approve, approve without a screenshot (a comment then), a lower
// difficulty by the proof, and reject with its consequences and a comment.

vi.mock('../api/realtime', () => ({ watchSeason: () => () => {} }));

const t = ru.admin.proofs;

function item(overrides: Partial<Schemas['ProofQueueItemView']>): Schemas['ProofQueueItemView'] {
  return {
    runId: 'r1',
    playerId: 'p1',
    playerName: 'Вася',
    gameTitle: 'Hollow Knight',
    completedAt: '2026-09-20T09:00:00+00:00',
    reachedFinish: false,
    status: 'pending',
    links: ['https://imgur.com/a/credits'],
    note: 'Титры в конце',
    witnessName: null,
    difficulty: 'hard',
    hours: 27,
    diceTotal: 11,
    decidesFinish: false,
    files: [],
    rollClosed: false,
    ...overrides,
  };
}

const finishing = item({
  runId: 'r0',
  playerId: 'p2',
  playerName: 'Сова',
  gameTitle: 'Celeste',
  decidesFinish: true,
  reachedFinish: true,
});

function open(queue: Schemas['ProofQueueItemView'][], routes: Record<string, unknown> = {}) {
  const server = fakeServer({ 'GET /api/admin/seasons/*/proofs': queue, ...routes });
  render(<AdminScreen path="/admin" currentSeasonId={seasonId} user={adminUser} />);
  return server;
}

const ok = { duplicate: false, events: [] };

describe('The proof queue', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('shows the runs in the server order, the finish on top and marked', async () => {
    open([finishing, item({})]);

    const cards = await screen.findAllByRole('article');
    expect(cards.map((c) => within(c).getByRole('heading').textContent)).toEqual([
      'Celeste',
      'Hollow Knight',
    ]);
    expect(within(screen.getByTestId('proof-r0')).getByText(t.decidesFinish)).toBeInTheDocument();
    expect(within(screen.getByTestId('proof-r1')).queryByText(t.decidesFinish)).toBeNull();
    expect(screen.getByTestId('proof-count')).toHaveTextContent(t.count(2));
  });

  it('marks the players whose roll the unchecked limit closed and names them on top', async () => {
    open([item({ rollClosed: true }), item({ runId: 'r2', rollClosed: true }), finishing]);

    const marked = await screen.findAllByTestId('roll-closed');
    expect(marked).toHaveLength(2);
    expect(marked[0]).toHaveTextContent(t.rollClosed);
    expect(within(screen.getByTestId('proof-r0')).queryByTestId('roll-closed')).toBeNull();
    expect(screen.getByText(t.rollClosedSummary('Вася'))).toBeInTheDocument();
  });

  it('says nothing about the limit when no roll is closed', async () => {
    open([item({})]);

    await screen.findByTestId('proof-r1');
    expect(screen.queryByTestId('roll-closed')).toBeNull();
    expect(screen.queryByText(/Ролл закрыт лимитом непроверенных/)).toBeNull();
  });

  it('shows the proof: links, screenshots, note and witness, and the claimed numbers', async () => {
    open([
      item({
        witnessName: 'Петя',
        files: [{ id: 'f1', url: '/api/files/f1', thumbnailUrl: '/api/files/f1/thumbnail' }],
      }),
    ]);

    const card = await screen.findByTestId('proof-r1');
    expect(within(card).getByRole('link', { name: /imgur\.com/ })).toHaveAttribute(
      'href',
      'https://imgur.com/a/credits',
    );
    expect(within(card).getByRole('img', { name: ru.proof.shotAlt(1) })).toHaveAttribute(
      'src',
      '/api/files/f1/thumbnail',
    );
    expect(within(card).getByText(t.note('Титры в конце'))).toBeInTheDocument();
    expect(within(card).getByText(t.witness('Петя'))).toBeInTheDocument();
    expect(card).toHaveTextContent(t.claimed(ru.difficulty.hard));
    expect(card).toHaveTextContent(t.dice(11));
  });

  it('approves a sent proof and refreshes the queue', async () => {
    let queue = [item({})];
    const server = open([], {
      'GET /api/admin/seasons/*/proofs': () => ({ body: queue }),
      'POST /api/admin/seasons/*/runs/*/approve': () => {
        queue = [];
        return { body: ok };
      },
    });

    await userEvent.click(await screen.findByTestId('approve'));

    const [sent] = server.sent('POST', '/runs/r1/approve');
    expect(sent?.body).toMatchObject({ difficulty: null, comment: null });
    expect(await screen.findByText(t.approved('Hollow Knight'))).toBeInTheDocument();
    expect(await screen.findByText(t.emptyTitle)).toBeInTheDocument();
  });

  it('approves at a lower difficulty by the proof, offering only lower ones', async () => {
    const server = open([item({})], { 'POST /api/admin/seasons/*/runs/*/approve': ok });

    const select = await screen.findByTestId('approve-difficulty');
    expect(
      within(select)
        .getAllByRole('option')
        .map((o) => o.getAttribute('value')),
    ).toEqual(['', 'easy', 'normal']);
    await userEvent.selectOptions(select, 'normal');
    await userEvent.click(screen.getByTestId('approve'));

    await waitFor(() => {
      expect(server.sent('POST', '/approve')[0]?.body).toMatchObject({ difficulty: 'normal' });
    });
  });

  it('asks for a comment to approve a run without a proof', async () => {
    const server = open([item({ status: null, links: [], note: null })], {
      'POST /api/admin/seasons/*/runs/*/approve': ok,
    });

    const approve = await screen.findByTestId('approve');
    expect(approve).toHaveTextContent(t.approveWithoutShot);
    await userEvent.click(approve);

    expect(await screen.findByText(ru.admin.commentRequired)).toBeInTheDocument();
    expect(server.sent('POST', '/approve')).toHaveLength(0);

    await userEvent.type(screen.getByTestId('approve-comment'), 'Видел на стриме');
    await userEvent.click(approve);
    await waitFor(() => {
      expect(server.sent('POST', '/approve')[0]?.body).toMatchObject({
        comment: 'Видел на стриме',
      });
    });
  });

  it('rejects only after a confirmation with the consequences and a comment', async () => {
    const server = open([item({})], { 'POST /api/admin/seasons/*/runs/*/reject': ok });

    await userEvent.click(await screen.findByTestId('reject'));
    const dialog = await screen.findByRole('alertdialog');
    for (const line of t.rejectConsequences(11, false))
      expect(within(dialog).getByText(line)).toBeInTheDocument();
    expect(within(dialog).queryByText(/финиш и место/)).toBeNull();

    await userEvent.click(within(dialog).getByRole('button', { name: t.rejectConfirm }));
    expect(await within(dialog).findByText(ru.admin.commentRequired)).toBeInTheDocument();
    expect(server.sent('POST', '/reject')).toHaveLength(0);

    await userEvent.type(within(dialog).getByTestId('reject-comment'), 'На скрине другая игра');
    await userEvent.click(within(dialog).getByRole('button', { name: t.rejectConfirm }));

    await waitFor(() => {
      expect(server.sent('POST', '/reject')[0]?.body).toMatchObject({
        comment: 'На скрине другая игра',
      });
    });
    expect(await screen.findByText(t.rejected('Hollow Knight'))).toBeInTheDocument();
  });

  it('warns that rejecting a run that decides a finish may take the finish away', async () => {
    open([finishing]);

    await userEvent.click(await screen.findByTestId('reject'));
    const dialog = await screen.findByRole('alertdialog');

    for (const line of t.rejectConsequences(11, true))
      expect(within(dialog).getByText(line)).toBeInTheDocument();
    expect(within(dialog).getByText(/финиш и место снимутся/)).toBeInTheDocument();
  });

  it('keeps a refused reject in its dialog', async () => {
    open([item({})], {
      'POST /api/admin/seasons/*/runs/*/reject': answer(409, {
        title: 'Rejected',
        status: 409,
        code: 'proof.alreadyReviewed',
      }),
    });

    await userEvent.click(await screen.findByTestId('reject'));
    const dialog = await screen.findByRole('alertdialog');
    await userEvent.type(within(dialog).getByTestId('reject-comment'), 'Другая игра');
    await userEvent.click(within(dialog).getByRole('button', { name: t.rejectConfirm }));

    expect(
      await within(dialog).findByText(ru.rejection['proof.alreadyReviewed']),
    ).toBeInTheDocument();
  });

  it('sends a retry after a lost answer with the same command id, and a new action with its own', async () => {
    let lost = true;
    const server = open([item({}), item({ runId: 'r2' })], {
      'POST /api/admin/seasons/*/runs/*/approve': () => {
        if (lost) {
          lost = false;
          throw new TypeError('Failed to fetch');
        }
        return { body: ok };
      },
    });

    const card = await screen.findByTestId('proof-r1');
    await userEvent.click(within(card).getByTestId('approve'));
    expect(await within(card).findByText(ru.admin.failed)).toBeInTheDocument();
    await userEvent.click(within(card).getByTestId('approve'));
    await waitFor(() => {
      expect(server.sent('POST', '/r1/approve')).toHaveLength(2);
    });
    await userEvent.click(within(screen.getByTestId('proof-r2')).getByTestId('approve'));
    await waitFor(() => {
      expect(server.sent('POST', '/r2/approve')).toHaveLength(1);
    });

    const id = (call: { body: unknown } | undefined) =>
      (call?.body as { commandId?: string } | undefined)?.commandId;
    const [first, again] = server.sent('POST', '/r1/approve');
    expect(id(again)).toBe(id(first));
    expect(id(server.sent('POST', '/r2/approve')[0])).not.toBe(id(first));
  });

  it('cancelling the reject sends nothing', async () => {
    const server = open([item({})]);

    await userEvent.click(await screen.findByTestId('reject'));
    await userEvent.click(await screen.findByRole('button', { name: ru.ui.cancel }));

    expect(screen.queryByRole('alertdialog')).toBeNull();
    expect(server.sent('POST', '/reject')).toHaveLength(0);
  });

  it('shows the engine’s refusal at the card', async () => {
    open([item({})], {
      'POST /api/admin/seasons/*/runs/*/approve': answer(409, {
        title: 'Rejected',
        status: 409,
        code: 'proof.alreadyReviewed',
      }),
    });

    await userEvent.click(await screen.findByTestId('approve'));

    expect(
      await within(screen.getByTestId('proof-r1')).findByText(
        ru.rejection['proof.alreadyReviewed'],
      ),
    ).toBeInTheDocument();
  });

  it('makes only the first card’s approval the main action', async () => {
    open([finishing, item({})]);

    const buttons = await screen.findAllByTestId('approve');
    expect(buttons.map((b) => b.dataset.variant)).toEqual(['main', 'quiet']);
  });

  it('says there is nothing to check when the queue is empty', async () => {
    open([]);

    expect(await screen.findByText(t.emptyTitle)).toBeInTheDocument();
    expect(screen.queryByTestId('proof-count')).toBeNull();
  });

  it('shows the error with a retry that loads again', async () => {
    let fail = true;
    const server = open([], {
      'GET /api/admin/seasons/*/proofs': () => (fail ? { status: 500 } : { body: [item({})] }),
    });

    await screen.findByText(ru.admin.loadErrorTitle);
    fail = false;
    await userEvent.click(screen.getByRole('button', { name: ru.ui.retry }));

    expect(await screen.findByTestId('proof-r1')).toBeInTheDocument();
    expect(server.sent('GET', '/proofs')).toHaveLength(2);
  });
});
