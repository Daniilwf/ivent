import { act, render, screen } from '@testing-library/react';
import { MAINTENANCE_EVENT } from '../api/client';
import { ru } from '../i18n/ru';
import { MaintenanceBanner } from './MaintenanceBanner';

// A8, D-121: the banner is shown while the site only reads, and at once after a write met a 503.

function serve(maintenance: () => boolean) {
  const fetch = vi.fn(() =>
    Promise.resolve(
      new Response(JSON.stringify({ maintenance: maintenance() }), {
        status: 200,
        headers: { 'Content-Type': 'application/json' },
      }),
    ),
  );
  vi.stubGlobal('fetch', fetch);
  return fetch;
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('MaintenanceBanner', () => {
  it('says the site only reads while it is under maintenance', async () => {
    serve(() => true);

    render(<MaintenanceBanner />);

    expect(await screen.findByTestId('maintenance-banner')).toHaveTextContent(
      ru.maintenance.banner,
    );
  });

  it('shows nothing while the site writes', async () => {
    const fetch = serve(() => false);

    render(<MaintenanceBanner />);

    await vi.waitFor(() => {
      expect(fetch).toHaveBeenCalled();
    });
    expect(screen.queryByTestId('maintenance-banner')).not.toBeInTheDocument();
  });

  it('asks again at once when a write meets maintenance, and hides when it ends', async () => {
    let on = false;
    const fetch = serve(() => on);
    render(<MaintenanceBanner />);
    await vi.waitFor(() => {
      expect(fetch).toHaveBeenCalledTimes(1);
    });

    on = true;
    act(() => {
      globalThis.dispatchEvent(new Event(MAINTENANCE_EVENT));
    });
    expect(await screen.findByTestId('maintenance-banner')).toBeInTheDocument();

    on = false;
    act(() => {
      globalThis.dispatchEvent(new Event(MAINTENANCE_EVENT));
    });
    await vi.waitFor(() =>
      expect(screen.queryByTestId('maintenance-banner')).not.toBeInTheDocument(),
    );
  });

  it('keeps quiet when the site does not answer', async () => {
    const fetch = vi.fn(() => Promise.reject(new TypeError('offline')));
    vi.stubGlobal('fetch', fetch);

    render(<MaintenanceBanner />);

    await vi.waitFor(() => {
      expect(fetch).toHaveBeenCalled();
    });
    expect(screen.queryByTestId('maintenance-banner')).not.toBeInTheDocument();
  });
});
