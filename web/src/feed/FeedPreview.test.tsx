import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { Schemas } from '../api/client';
import { ru } from '../i18n/ru';
import { FeedPreview } from './FeedPreview';

// H5: the latest of the feed beside the map on a desktop (DESIGN.md «Главная»): five lines, read again when the season
// moves on, and the way to the whole feed.

const seasonId = '99999999-9999-9999-9999-999999999999';
const vasya = '11111111-1111-1111-1111-111111111111';

function entries(count: number): Schemas['FeedEntryView'][] {
  return Array.from({ length: count }, (_, i) => ({
    sequence: 100 - i,
    commandId: `c${i}`,
    occurredAt: new Date().toISOString(),
    type: 'run-started',
    data: { runId: 'r', playerId: vasya, gameId: 'g' },
    author: null,
    undone: false,
  }));
}

function respond(status: number, body?: unknown) {
  return new Response(body === undefined ? null : JSON.stringify(body), {
    status,
    headers: { 'Content-Type': 'application/json' },
  });
}

function serve(answer: () => Response) {
  const calls = vi.fn(() => Promise.resolve(answer()));
  vi.stubGlobal('fetch', calls);
  return calls;
}

const page = (count: number) => ({
  entries: entries(count),
  nextBefore: null,
  players: [{ id: vasya, userId: 'u', name: 'Вася', avatar: null, hasProfile: true, token: 0 }],
  games: [{ id: 'g', title: 'Celeste', hasPage: true }],
  runs: [],
});

describe('FeedPreview', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('shows the five newest lines and leads to the whole feed', async () => {
    serve(() => respond(200, page(8)));

    render(<FeedPreview seasonId={seasonId} version={1} />);

    expect(screen.getByTestId('feed-loading')).toBeInTheDocument();
    expect(await screen.findByTestId('feed-item-c0')).toBeInTheDocument();
    expect(screen.getAllByTestId(/^feed-item-/)).toHaveLength(5);
    expect(screen.getByRole('heading', { level: 3, name: ru.feed.today })).toBeInTheDocument();
    expect(screen.getByRole('link', { name: ru.feed.all })).toHaveAttribute('href', '/feed');
  });

  it('reads the feed again when the season moves on', async () => {
    let count = 1;
    const calls = serve(() => respond(200, page(count)));
    const { rerender } = render(<FeedPreview seasonId={seasonId} version={1} />);
    await screen.findByTestId('feed-item-c0');

    count = 2;
    rerender(<FeedPreview seasonId={seasonId} version={2} />);

    expect(await screen.findByTestId('feed-item-c1')).toBeInTheDocument();
    expect(calls).toHaveBeenCalledTimes(2);
  });

  it('says the feed is empty', async () => {
    serve(() => respond(200, page(0)));

    render(<FeedPreview seasonId={seasonId} version={1} />);

    expect(await screen.findByText(ru.feed.emptyTitle)).toBeInTheDocument();
  });

  it('says it did not load and tries again', async () => {
    let fail = true;
    serve(() => (fail ? respond(500) : respond(200, page(1))));
    render(<FeedPreview seasonId={seasonId} version={1} />);

    expect(await screen.findByRole('alert')).toHaveTextContent(ru.feed.errorTitle);
    fail = false;
    await userEvent.click(screen.getByRole('button', { name: ru.ui.retry }));

    await waitFor(() => {
      expect(screen.getByTestId('feed-item-c0')).toBeInTheDocument();
    });
  });
});
