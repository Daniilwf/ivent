import { act, render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { Schemas } from '../api/client';
import { usePoolVersion, useSeasonVersion } from '../app/useSeasonVersion';
import { ru } from '../i18n/ru';
import { fakeServer, json, seasonId } from '../test/fakeServer';
import { NominationResults } from './NominationResults';

vi.mock('../app/useSeasonVersion', () => ({ usePoolVersion: vi.fn(), useSeasonVersion: vi.fn() }));

const caption = 'Короткая дорога привела к финишу';
const galleryLink = 'Открыть галерею';
const current: Schemas['NominationResultView'] = {
  automatic: [
    {
      nomination: 'mostRuns',
      value: 1,
      winners: [{ playerId: 'player', name: 'Лена', profileUserId: 'account' }],
    },
  ],
  voted: [
    {
      nomination: 'funniestMoment',
      pollId: 'poll',
      hasWinner: true,
      winner: {
        id: 'moment',
        label: caption,
        detail: null,
        href: '/seasons/season/gallery',
        thumbnailUrl: '/api/thumbnail',
      },
    },
  ],
};
const hidden: Schemas['NominationResultView'] = {
  ...current,
  voted: [{ nomination: 'funniestMoment', pollId: 'poll', hasWinner: true, winner: null }],
};

describe('NominationResults', () => {
  beforeEach(() => {
    vi.mocked(useSeasonVersion).mockReturnValue(0);
    vi.mocked(usePoolVersion).mockReturnValue(0);
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('hides stale voted material immediately and leaves an automatic winner focused', async () => {
    let resolve!: (response: Response) => void;
    const pending = new Promise<Response>((done) => {
      resolve = done;
    });
    let requests = 0;
    fakeServer({
      'GET /api/seasons/*/nominations': () => (++requests === 1 ? json(200, current) : pending),
    });
    const screenElement = <NominationResults seasonId={seasonId} onSignedOut={vi.fn()} />;
    const { container, rerender } = render(screenElement);
    const winner = await screen.findByRole('link', { name: 'Лена' });
    expect(container.querySelector('img')).toHaveAttribute('src', '/api/thumbnail');
    winner.focus();

    vi.mocked(useSeasonVersion).mockReturnValue(1);
    rerender(<NominationResults seasonId={seasonId} onSignedOut={vi.fn()} />);

    expect(winner).toHaveFocus();
    expect(screen.getByRole('heading', { name: 'Самый смешной момент' })).toBeVisible();
    expect(screen.queryByText(caption)).not.toBeInTheDocument();
    expect(screen.queryByRole('link', { name: galleryLink })).not.toBeInTheDocument();
    expect(container.querySelector('img')).toBeNull();

    await act(async () => {
      resolve(json(200, hidden));
      await pending;
    });
    expect(await screen.findByText('Материалы победителя скрыты.')).toBeVisible();
    expect(winner).toHaveFocus();
  });

  it('moves focus from a removed link to its nomination and retries without restoring stale material', async () => {
    let requests = 0;
    fakeServer({
      'GET /api/seasons/*/nominations': () => {
        switch (++requests) {
          case 1:
            return json(200, current);
          case 2:
            return json(500);
          default:
            return json(200, hidden);
        }
      },
    });
    const onSignedOut = vi.fn();
    const { container, rerender } = render(
      <NominationResults seasonId={seasonId} onSignedOut={onSignedOut} />,
    );
    const link = await screen.findByRole('link', { name: galleryLink });
    expect(screen.getByText(caption)).toBeVisible();
    const heading = screen.getByRole('heading', { name: 'Самый смешной момент' });
    link.focus();

    vi.mocked(usePoolVersion).mockReturnValue(1);
    rerender(<NominationResults seasonId={seasonId} onSignedOut={onSignedOut} />);

    expect(heading).toHaveFocus();
    expect(screen.queryByRole('link', { name: galleryLink })).not.toBeInTheDocument();
    expect(container.querySelector('img')).toBeNull();
    await userEvent.click(await screen.findByRole('button', { name: ru.ui.retry }));
    expect(await screen.findByText('Материалы победителя скрыты.')).toBeVisible();
    expect(requests).toBe(3);
    expect(container.querySelector('img')).toBeNull();
  });
});
