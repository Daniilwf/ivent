import { render, screen, waitFor } from '@testing-library/react';
import { ru } from '../i18n/ru';
import { answer, fakeServer } from '../test/fakeServer';
import { EnvironmentBanner } from './EnvironmentBanner';

// H9, D-220: every page of a copy that is not the live site says so, so nobody takes the test copy for the real site;
// the live site shows nothing, and neither does a site that did not answer.

describe('The environment strip', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it.each(['staging', 'development', 'test', 'other'] as const)(
    'names the copy of the site on %s',
    async (environment) => {
      fakeServer({
        'GET /api/status': { maintenance: false, version: '1', environment, testTools: false },
      });

      render(<EnvironmentBanner />);

      const strip = await screen.findByTestId('environment-banner');
      expect(strip).toHaveTextContent(ru.environment[environment]);
      expect(screen.getByRole('complementary', { name: ru.environment.label })).toBe(strip);
    },
  );

  it('shows nothing on the live site', async () => {
    const server = fakeServer({
      'GET /api/status': {
        maintenance: false,
        version: '1',
        environment: 'production',
        testTools: false,
      },
    });

    render(<EnvironmentBanner />);

    await waitFor(() => {
      expect(server.sent('GET', '/api/status')).toHaveLength(1);
    });
    expect(screen.queryByTestId('environment-banner')).toBeNull();
  });

  it('shows nothing when the site does not say, or does not answer', async () => {
    const server = fakeServer({ 'GET /api/status': { maintenance: false, version: '1' } });
    const { unmount } = render(<EnvironmentBanner />);
    await waitFor(() => {
      expect(server.sent('GET', '/api/status')).toHaveLength(1);
    });
    expect(screen.queryByTestId('environment-banner')).toBeNull();
    unmount();

    const failing = fakeServer({ 'GET /api/status': answer(500) });
    render(<EnvironmentBanner />);
    await waitFor(() => {
      expect(failing.sent('GET', '/api/status')).toHaveLength(1);
    });
    expect(screen.queryByTestId('environment-banner')).toBeNull();
  });
});
