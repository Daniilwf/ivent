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
  totalHours: 12.75,
  bestRating: 8,
  worstRating: 4,
  attacksMade: 0,
  attacksReceived: 1,
  bestRoll: { runId: 'best', sum: 2, minimum: 1, maximum: 2, luck: 1 },
  worstRoll: { runId: 'worst', sum: 4, minimum: 2, maximum: 12, luck: 0.2 },
  place: null,
  points: null,
  playerToken: null,
  provisional: false,
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
    expect(details.getByText(t.hours)).toBeInTheDocument();
    expect(details.getByText('12,75')).toBeInTheDocument();
    expect(details.getByText('Выпало 2 из 2 возможных')).toBeInTheDocument();
    expect(details.getByText('Выпало 4 из 12 возможных')).toBeInTheDocument();

    await userEvent.click(screen.getByRole('button', { name: t.download }));
    expect(downloadRecap).toHaveBeenCalledWith({
      season: statistics.seasonName,
      player: user.name,
      standing: null,
      playerToken: null,
      completed: { label: t.completed, value: '4' },
      hours: { label: t.hours, value: '12,75', unit: 'часа' },
      ratings: [
        { label: t.bestRating, value: '8', unit: t.ratingUnit },
        { label: t.worstRating, value: '4', unit: t.ratingUnit },
      ],
      attacks: [
        { label: t.attacksMade, value: '0' },
        { label: t.attacksReceived, value: '1' },
      ],
      rolls: [
        { label: t.bestRoll, value: '2', detail: 'Выпало 2 из 2 возможных' },
        { label: t.worstRoll, value: '4', detail: 'Выпало 4 из 12 возможных' },
      ],
    });
  });

  it('explains the roll and exports the same standing and correctly inflected hours', async () => {
    fakeServer({
      [path]: {
        ...statistics,
        totalHours: 98,
        place: 7,
        points: 432,
        playerToken: 5,
        bestRoll: { runId: 'best', sum: 15, minimum: 3, maximum: 18, luck: 0.8 },
        worstRoll: { runId: 'worst', sum: 5, minimum: 3, maximum: 10, luck: 2 / 7 },
      },
    });
    vi.mocked(downloadRecap).mockResolvedValue(undefined);
    render(<SeasonRecap seasonId={seasonId} user={user} onSignedOut={vi.fn()} />);

    const article = await screen.findByRole('article', { name: user.name });
    const card = within(article);
    const label = card.getByText(t.bestRoll);
    const explanation = card.getByText('Выпало 15 из 18 возможных');
    expect(
      label.compareDocumentPosition(explanation) & Node.DOCUMENT_POSITION_FOLLOWING,
    ).toBeTruthy();
    expect(article).toHaveTextContent('98 часов');
    expect(article).toHaveTextContent(/(?:7(?:[-‑–]е)?\s*место|место\s*7)/iu);
    expect(article).toHaveTextContent(/(?:432\s*очк|очки\s*432)/iu);
    expect(article).not.toHaveTextContent(/28[,.]57\s*%/u);

    await userEvent.click(screen.getByRole('button', { name: t.download }));

    // Check the public presentation data without coupling to how the shared card is laid out internally.
    const exported = JSON.stringify(vi.mocked(downloadRecap).mock.lastCall?.[0]);
    expect(exported).toMatch(/\b432\b/u);
    expect(exported).toMatch(/\b7\b/u);
    expect(exported).toContain('98');
  });

  it.each([
    { roll: null, empty: true },
    { roll: undefined, empty: false },
  ])(
    'shows the empty roll message only when the server measures dice ($roll)',
    async ({ roll, empty }) => {
      fakeServer({ [path]: { ...statistics, bestRoll: roll, worstRoll: roll } });
      render(<SeasonRecap seasonId={seasonId} user={user} onSignedOut={vi.fn()} />);

      const card = within(await screen.findByRole('article', { name: user.name }));
      expect(card.queryAllByText(t.noRolls)).toHaveLength(empty ? 1 : 0);
      expect(card.queryAllByRole('heading', { name: t.rolls })).toHaveLength(empty ? 1 : 0);
    },
  );

  it('refreshes the reported rolls when dice are rerolled', async () => {
    let bestRoll = statistics.bestRoll;
    fakeServer({ [path]: () => json(200, { ...statistics, bestRoll }) });
    render(<SeasonRecap seasonId={seasonId} user={user} onSignedOut={vi.fn()} />);
    expect(await screen.findByText('Выпало 2 из 2 возможных')).toBeInTheDocument();

    bestRoll = { runId: 'best', sum: 3, minimum: 1, maximum: 5, luck: 0.5 };
    act(() =>
      notify?.([{ seasonId, fromSequence: 1, toSequence: 1, types: ['run-dice-rerolled'] }]),
    );
    expect(await screen.findByText('Выпало 3 из 5 возможных')).toBeInTheDocument();
    expect(screen.queryByText('Выпало 2 из 2 возможных')).toBeNull();
  });

  it('marks the first place as provisional until the freeze reaches the open recap', async () => {
    let provisional = true;
    fakeServer({
      [path]: () => json(200, { ...statistics, place: 1, points: 4, playerToken: 0, provisional }),
    });
    vi.mocked(downloadRecap).mockResolvedValue(undefined);
    render(<SeasonRecap seasonId={seasonId} user={user} onSignedOut={vi.fn()} />);

    const temporary = '1-е место (предварительно)';
    expect(await screen.findByText(temporary)).toBeVisible();
    await userEvent.click(screen.getByRole('button', { name: t.download }));
    expect(downloadRecap).toHaveBeenLastCalledWith(
      expect.objectContaining({ standing: { place: temporary, points: '4 очка' } }),
    );

    provisional = false;
    act(() =>
      notify?.([
        { seasonId, fromSequence: 1, toSequence: 2, types: ['proof-approved', 'player-frozen'] },
      ]),
    );
    expect(await screen.findByText('1-е место')).toBeVisible();
    expect(screen.queryByText(temporary)).toBeNull();
  });

  it.each([
    { totalHours: undefined, value: '—', unit: undefined },
    { totalHours: 0, value: '0', unit: 'часов' },
    { totalHours: 1, value: '1', unit: 'час' },
    { totalHours: 2, value: '2', unit: 'часа' },
    { totalHours: 21.5, value: '21,5', unit: 'часа' },
  ])(
    'shows and exports "$value $unit" for hours $totalHours',
    async ({ totalHours, value, unit }) => {
      fakeServer({ [path]: { ...statistics, totalHours } });
      vi.mocked(downloadRecap).mockResolvedValue(undefined);
      render(<SeasonRecap seasonId={seasonId} user={user} onSignedOut={vi.fn()} />);

      const card = within(await screen.findByRole('article', { name: user.name }));
      expect(card.getByText(t.hours).nextElementSibling).toHaveTextContent(
        unit ? `${value} ${unit}` : value,
      );

      await userEvent.click(screen.getByRole('button', { name: t.download }));
      expect(downloadRecap).toHaveBeenCalledWith(
        expect.objectContaining({
          hours: unit ? { label: t.hours, value, unit } : { label: t.hours, value },
        }),
      );
    },
  );

  it('reloads the displayed hours when a completion correction arrives', async () => {
    let totalHours = 0;
    fakeServer({ [path]: () => json(200, { ...statistics, totalHours }) });
    render(<SeasonRecap seasonId={seasonId} user={user} onSignedOut={vi.fn()} />);

    const card = within(await screen.findByRole('article', { name: user.name }));
    expect(card.getByText(t.hours).nextElementSibling).toHaveTextContent('0 часов');

    totalHours = 14.125;
    act(() =>
      notify?.([{ seasonId, fromSequence: 1, toSequence: 1, types: ['run-hours-corrected'] }]),
    );
    expect(await card.findByText('14,125')).toBeInTheDocument();
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
