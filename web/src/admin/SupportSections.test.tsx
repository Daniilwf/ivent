import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MAINTENANCE_EVENT } from '../api/client';
import { ru } from '../i18n/ru';
import { BugsSection } from './BugsSection';
import { EffectsSection } from './EffectsSection';
import { ErrorsSection } from './ErrorsSection';
import { answer, fakeServer, seasonId } from './fakeServer';
import { SiteSection } from './SiteSection';

// H8: the smaller admin pages — manual effects (D-102), bug reports (E5), the error journal (D7) and maintenance mode
// (D-121).

const ok = { duplicate: false, events: [] };

describe('Manual effects in the admin pages', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  const effect = {
    id: 'e1',
    playerId: 'p1',
    playerName: 'Вася',
    drawEvent: 'bad' as const,
    source: 'drop' as const,
    runId: null,
  };

  it('resolves a player’s effect with the comment it needs', async () => {
    const server = fakeServer({
      'GET /api/admin/seasons/*/effects': [effect],
      'POST /api/admin/seasons/*/effects/*/resolve': ok,
    });
    render(<EffectsSection seasonId={seasonId} version={0} />);

    const row = await screen.findByTestId('effect-e1');
    expect(row).toHaveTextContent(ru.effects.drawEvent('bad', 'drop'));
    await userEvent.click(within(row).getByTestId('effect-not-applicable'));
    expect(within(row).getByText(ru.admin.commentRequired)).toBeInTheDocument();

    await userEvent.type(within(row).getByTestId('effect-comment'), 'Нечего снимать');
    await userEvent.click(within(row).getByTestId('effect-not-applicable'));

    await waitFor(() => {
      expect(server.sent('POST', '/effects/e1/resolve')[0]?.body).toMatchObject({
        outcome: 'notApplicable',
        comment: 'Нечего снимать',
      });
    });
    expect(await screen.findByText(ru.admin.effects.resolved('Вася'))).toBeInTheDocument();
  });

  it('says there is nothing to resolve', async () => {
    fakeServer({ 'GET /api/admin/seasons/*/effects': [] });
    render(<EffectsSection seasonId={seasonId} version={0} />);

    expect(await screen.findByText(ru.admin.effects.emptyTitle)).toBeInTheDocument();
  });
});

describe('Bug reports in the admin pages', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  const t = ru.admin.bugs;
  const report = {
    id: 'b1',
    author: 'Вася',
    page: '/?x=1',
    text: 'Кнопка не жмётся',
    context: {
      userAgent: 'Firefox',
      viewport: '390x844',
      actions: [{ at: null, text: 'нажал «Крутить колесо»' }],
      errors: [{ at: null, text: 'TypeError: x is undefined' }],
    },
    screenshot: { id: 'f1', url: '/api/files/f1', thumbnailUrl: '/api/files/f1/thumbnail' },
    status: 'new' as const,
    createdAt: '2026-09-20T09:00:00Z',
  };

  it('shows the new reports first with their context and moves one into work', async () => {
    const server = fakeServer({
      'GET /api/admin/bug-reports': [report],
      'PUT /api/admin/bug-reports/*/status': { ...report, status: 'inWork' },
    });
    render(<BugsSection />);

    const card = await screen.findByTestId('bug-b1');
    expect(server.sent('GET', '/bug-reports')[0]?.query.get('status')).toBe('new');
    expect(card).toHaveTextContent('Кнопка не жмётся');
    expect(card).toHaveTextContent('TypeError: x is undefined');
    expect(within(card).getByRole('img', { name: t.screenshot })).toBeInTheDocument();

    await userEvent.click(within(card).getByTestId('bug-move'));
    await waitFor(() => {
      expect(server.sent('PUT', '/bug-reports/b1/status')[0]?.body).toMatchObject({
        status: 'inWork',
      });
    });
    expect(await screen.findByText(t.moved(t.statuses.inWork))).toBeInTheDocument();
  });

  it('filters by status and exports the shown ones for the agent', async () => {
    const server = fakeServer({ 'GET /api/admin/bug-reports': [] });
    render(<BugsSection />);

    expect(await screen.findByText(t.emptyTitle)).toBeInTheDocument();
    await userEvent.click(screen.getByRole('radio', { name: ru.admin.bugs.filters.all }));

    await waitFor(() => {
      expect(server.sent('GET', '/bug-reports').at(-1)?.query.has('status')).toBe(false);
    });
    expect(screen.getByRole('radio', { name: ru.admin.bugs.filters.all })).toBeChecked();
    expect(screen.getByTestId('bugs-export')).toHaveAttribute(
      'href',
      '/api/admin/bug-reports/export',
    );
  });
});

describe('The error journal', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('shows the request, the user, the exception and its stack', async () => {
    fakeServer({
      'GET /api/admin/errors': [
        {
          id: 'x1',
          at: '2026-09-20T09:00:00Z',
          method: 'POST',
          path: '/api/seasons/s/roll',
          userLogin: 'vasya',
          exceptionType: 'InvalidOperationException',
          message: 'Boom',
          stackTrace: 'at Roll()',
          traceId: 't-1',
        },
      ],
    });
    render(<ErrorsSection />);

    const entry = await screen.findByTestId('error-x1');
    expect(entry).toHaveTextContent('POST /api/seasons/s/roll');
    expect(entry).toHaveTextContent('InvalidOperationException: Boom');
    expect(entry).toHaveTextContent(ru.admin.errors.user('vasya'));
    expect(entry).toHaveTextContent('at Roll()');
  });

  it('says there are no errors, and says a failed load with a retry', async () => {
    fakeServer({ 'GET /api/admin/errors': [] });
    const { unmount } = render(<ErrorsSection />);
    expect(await screen.findByText(ru.admin.errors.emptyTitle)).toBeInTheDocument();
    unmount();

    fakeServer({ 'GET /api/admin/errors': answer(429) });
    render(<ErrorsSection />);
    expect(await screen.findByText(ru.admin.loadErrorTitle)).toBeInTheDocument();
    expect(screen.getByRole('button', { name: ru.ui.retry })).toBeInTheDocument();
  });
});

describe('Maintenance mode', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  const t = ru.admin.site;

  it('turns maintenance on only after a confirmation and tells the banner', async () => {
    let on = false;
    const server = fakeServer({
      'GET /api/status': () => ({ body: { maintenance: on } }),
      'PUT /api/admin/maintenance': () => {
        on = true;
        return { body: { maintenance: true } };
      },
    });
    const banner = vi.fn();
    globalThis.addEventListener(MAINTENANCE_EVENT, banner);
    render(<SiteSection />);

    expect(await screen.findByTestId('maintenance-state')).toHaveTextContent(t.off);
    await userEvent.click(screen.getByTestId('maintenance-on'));
    const dialog = await screen.findByRole('alertdialog');
    expect(within(dialog).getByText(t.onConsequences[0])).toBeInTheDocument();
    await userEvent.click(within(dialog).getByRole('button', { name: t.turnOn }));

    await waitFor(() => {
      expect(server.sent('PUT', '/maintenance')[0]?.body).toEqual({ on: true });
    });
    expect(await screen.findByTestId('maintenance-state')).toHaveTextContent(t.on);
    expect(banner).toHaveBeenCalled();
    globalThis.removeEventListener(MAINTENANCE_EVENT, banner);
  });

  it('turns maintenance off at once', async () => {
    const server = fakeServer({
      'GET /api/status': { maintenance: true },
      'PUT /api/admin/maintenance': { maintenance: false },
    });
    render(<SiteSection />);

    await userEvent.click(await screen.findByTestId('maintenance-off'));

    await waitFor(() => {
      expect(server.sent('PUT', '/maintenance')[0]?.body).toEqual({ on: false });
    });
    expect(await screen.findByText(t.turnedOff)).toBeInTheDocument();
  });
});
