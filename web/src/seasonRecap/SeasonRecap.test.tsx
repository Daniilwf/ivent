import { act, render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { Schemas } from '../api/client';
import type { SeasonUpdate } from '../api/realtime';
import { ru } from '../i18n/ru';
import { fakeServer, json, seasonId } from '../test/fakeServer';
import { SeasonRecap } from './SeasonRecap';
import { downloadRecap } from './recapPng';

vi.mock('./recapPng', () => ({ downloadRecap: vi.fn() }));

let notify: ((updates: SeasonUpdate[]) => void) | undefined;
vi.mock('../api/realtime', () => ({
  watchSeason: (_seasonId: string, onChange: (updates: SeasonUpdate[]) => void) => {
    notify = onChange;
    onChange([]);
    return () => {
      notify = undefined;
    };
  },
}));

const t = ru.seasonRecap;
const user = { id: 'u1', name: 'Барсук' };
const path = `/api/seasons/${seasonId}/my-statistics`;
const statistics: Schemas['SeasonStatisticsView'] = {
  seasonId,
  seasonName: 'Осень',
  completedRuns: 4,
  bestRating: 8,
  worstRating: 4,
  attacksMade: 0,
  attacksReceived: 1,
};

describe('SeasonRecap', () => {
  it('shows one shared set of values in the preview and the export', async () => {
    fakeServer({ [path]: statistics });
    vi.mocked(downloadRecap).mockResolvedValue(undefined);
    render(<SeasonRecap seasonId={seasonId} user={user} onSignedOut={vi.fn()} />);

    const card = await screen.findByRole('article', { name: user.name });
    const details = within(card);
    expect(details.getByText(statistics.seasonName)).toBeInTheDocument();
    expect(details.getAllByText(t.ratingUnit)).toHaveLength(2);
    expect(details.getByText('0')).toBeInTheDocument();

    await userEvent.click(screen.getByRole('button', { name: t.download }));
    expect(downloadRecap).toHaveBeenCalledWith({
      season: statistics.seasonName,
      player: user.name,
      completed: { label: t.completed, value: '4' },
      ratings: [
        { label: t.bestRating, value: '8', unit: t.ratingUnit },
        { label: t.worstRating, value: '4', unit: t.ratingUnit },
      ],
      attacks: [
        { label: t.attacksMade, value: '0' },
        { label: t.attacksReceived, value: '1' },
      ],
    });
  });

  it('retries a load error, shows missing ratings once, and keeps the card after an export failure', async () => {
    let requests = 0;
    fakeServer({
      [path]: () =>
        ++requests === 1
          ? json(500)
          : json(200, { ...statistics, completedRuns: 0, bestRating: null, worstRating: null }),
    });
    vi.mocked(downloadRecap).mockRejectedValueOnce(new Error('Canvas is unsupported'));
    render(<SeasonRecap seasonId={seasonId} user={user} onSignedOut={vi.fn()} />);

    expect(await screen.findByRole('heading', { name: t.loadError })).toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: ru.ui.retry }));
    expect(await screen.findByText(t.noRatings)).toBeInTheDocument();
    expect(screen.getAllByText(t.noRatings)).toHaveLength(1);

    await userEvent.click(screen.getByRole('button', { name: t.download }));
    expect(await screen.findByRole('alert')).toHaveTextContent(t.downloadError);
    expect(screen.getByRole('article', { name: user.name })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: t.download })).not.toHaveAttribute('aria-disabled');
  });

  it('removes the card and download when the open season disables statistics', async () => {
    let enabled = true;
    fakeServer({ [path]: () => (enabled ? json(200, statistics) : json(404)) });
    render(<SeasonRecap seasonId={seasonId} user={user} onSignedOut={vi.fn()} />);
    await screen.findByRole('article', { name: user.name });

    enabled = false;
    act(() => notify?.([{ seasonId, fromSequence: 1, toSequence: 1, types: ['ruleset-changed'] }]));

    expect(await screen.findByRole('heading', { name: t.unavailable })).toBeInTheDocument();
    expect(screen.queryByRole('article')).toBeNull();
    expect(screen.queryByRole('button', { name: t.download })).toBeNull();
  });

  it('offers the profile and no download for an unavailable recap', async () => {
    fakeServer({ [path]: () => json(404) });
    render(<SeasonRecap seasonId={seasonId} user={user} onSignedOut={vi.fn()} />);
    expect(await screen.findByRole('heading', { name: t.unavailable })).toBeInTheDocument();
    expect(screen.getByRole('link', { name: t.toProfile })).toHaveAttribute('href', '/users/u1');
    expect(screen.queryByRole('button', { name: t.download })).toBeNull();
  });
});
