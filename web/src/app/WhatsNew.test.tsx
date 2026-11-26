import { StrictMode } from 'react';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { ru } from '../i18n/ru';
import { paths } from './router';
import { ReleaseList, SEEN_KEY, UpdateBanner, type Release } from './WhatsNew';
import { WhatsNewScreen } from './WhatsNewScreen';

// J4, D-201: after an update the site says so once, with «Что нового»; a first visit only remembers the version.

function serve(version: string) {
  const fetch = vi.fn(() =>
    Promise.resolve(
      new Response(JSON.stringify({ maintenance: false, version }), {
        status: 200,
        headers: { 'Content-Type': 'application/json' },
      }),
    ),
  );
  vi.stubGlobal('fetch', fetch);
  return fetch;
}

const releases: Release[] = [
  { version: 'v1.1.0', date: '2026-10-12', items: ['Колесо показывает промахи', 'Кубы крутятся'] },
  { version: 'v1.0.0', date: '2026-10-01', items: [] },
];

afterEach(() => {
  vi.unstubAllGlobals();
  localStorage.clear();
});

describe('UpdateBanner', () => {
  it('says nothing on a first visit and remembers the version', async () => {
    const fetch = serve('v1.1.0');

    render(<UpdateBanner releases={releases} />);

    await waitFor(() => {
      expect(localStorage.getItem(SEEN_KEY)).toBe('v1.1.0');
    });
    expect(fetch).toHaveBeenCalled();
    expect(screen.queryByTestId('update-banner')).not.toBeInTheDocument();
  });

  it('says nothing when the version is the one seen before', async () => {
    localStorage.setItem(SEEN_KEY, 'v1.1.0');
    const fetch = serve('v1.1.0');

    render(<UpdateBanner releases={releases} />);

    await waitFor(() => {
      expect(fetch).toHaveBeenCalled();
    });
    expect(screen.queryByTestId('update-banner')).not.toBeInTheDocument();
  });

  it('tells about a new version and opens «Что нового» with the releases', async () => {
    localStorage.setItem(SEEN_KEY, 'v1.0.0');
    serve('v1.1.0');

    render(<UpdateBanner releases={releases} />);

    const banner = await screen.findByTestId('update-banner');
    expect(banner).toHaveTextContent(ru.whatsNew.banner);
    await userEvent.click(within(banner).getByTestId('whats-new-open'));
    const list = await screen.findByTestId('whats-new-list');
    expect(within(list).getByText('Колесо показывает промахи')).toBeInTheDocument();
    expect(within(list).getByText(ru.whatsNew.quiet)).toBeInTheDocument();
  });

  it('hides for good once closed: the new version is remembered', async () => {
    localStorage.setItem(SEEN_KEY, 'v1.0.0');
    serve('v1.1.0');
    render(<UpdateBanner releases={releases} />);

    await userEvent.click(await screen.findByTestId('update-banner-close'));

    expect(screen.queryByTestId('update-banner')).not.toBeInTheDocument();
    expect(localStorage.getItem(SEEN_KEY)).toBe('v1.1.0');
  });
});

describe('ReleaseList', () => {
  it('lists the releases newest first, each with its day', () => {
    render(<ReleaseList releases={releases} />);

    const headings = screen.getAllByRole('heading', { level: 3 }).map((h) => h.textContent);
    expect(headings).toEqual([
      ru.whatsNew.release('v1.1.0', '2026-10-12'),
      ru.whatsNew.release('v1.0.0', '2026-10-01'),
    ]);
  });

  it('says so when there is no release yet', () => {
    render(<ReleaseList releases={[]} />);

    expect(screen.getByText(ru.whatsNew.empty)).toBeInTheDocument();
  });
});

describe('WhatsNewScreen', () => {
  it('shows the full release history under the page heading', () => {
    render(<WhatsNewScreen releases={releases} />);

    expect(screen.getByRole('heading', { level: 1, name: ru.whatsNew.title })).toBeInTheDocument();
    const headings = screen.getAllByRole('heading', { level: 2 }).map((h) => h.textContent);
    expect(headings).toEqual(
      releases.map((release) => ru.whatsNew.release(release.version, release.date)),
    );
    expect(screen.getByText('Колесо показывает промахи')).toBeInTheDocument();
    expect(screen.getByText(ru.whatsNew.quiet)).toBeInTheDocument();
  });

  it('shows one empty state when there are no releases', () => {
    render(<WhatsNewScreen releases={[]} />);

    expect(screen.getByRole('heading', { level: 2, name: ru.whatsNew.empty })).toBeInTheDocument();
    expect(screen.queryByTestId('whats-new-list')).not.toBeInTheDocument();
  });

  it('restores the saved scroll again after the release history mounts', async () => {
    const scroll = vi.spyOn(window, 'scrollTo');
    window.history.replaceState({ scrollY: 680 }, '', paths.whatsNew());
    try {
      window.dispatchEvent(new PopStateEvent('popstate'));
      await waitFor(() => {
        expect(scroll).toHaveBeenCalledWith(0, 680);
      });
      scroll.mockClear();

      render(
        <StrictMode>
          <WhatsNewScreen releases={releases} />
        </StrictMode>,
      );

      await waitFor(() => {
        expect(scroll).toHaveBeenCalledWith(0, 680);
      });
    } finally {
      scroll.mockRestore();
      window.history.replaceState(null, '', '/');
    }
  });
});
