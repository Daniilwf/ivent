import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { ru } from '../i18n/ru';
import { answer, fakeServer, seasonId } from './fakeServer';
import { SeasonSection } from './SeasonSection';

// H8: the season (SE1, SE2, D-101, D-105): the next status after a confirmation with the consequences, the deadline in
// Moscow time, the archive for download, the integrity check, the list of seasons and a new season.

const t = ru.admin.season;

const other = '5ea50000-0000-0000-0000-000000000002';
const list = [
  {
    id: seasonId,
    name: 'Осень',
    status: 'active',
    deadline: null,
    createdAt: '2026-09-01T00:00:00Z',
  },
  {
    id: other,
    name: 'Весна',
    status: 'archived',
    deadline: null,
    createdAt: '2026-03-01T00:00:00Z',
  },
];
const ok = { duplicate: false, events: [] };

function open(
  status = 'active',
  routes: Record<string, unknown> = {},
  onPick: (id: string) => void = vi.fn(),
) {
  const server = fakeServer({
    'GET /api/seasons': list,
    'GET /api/seasons/*': {
      id: seasonId,
      name: 'Осень',
      status,
      deadline: '2026-12-31T21:00:00+00:00',
      cells: [],
    },
    ...routes,
  });
  render(<SeasonSection seasonId={seasonId} version={0} onPick={onPick} />);
  return server;
}

describe('The season section', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('moves the season on only after a confirmation with the consequences', async () => {
    const server = open('active', { 'POST /api/admin/seasons/*/status': ok });

    const status = await screen.findByTestId('season-status');
    expect(status).toHaveTextContent(t.statuses.active);
    await userEvent.click(within(status).getByTestId('season-next'));
    const dialog = await screen.findByRole('alertdialog');
    for (const line of t.next.active.consequences)
      expect(within(dialog).getByText(line)).toBeInTheDocument();
    await userEvent.click(within(dialog).getByRole('button', { name: t.next.active.label }));

    await waitFor(() => {
      expect(server.sent('POST', '/status')[0]?.body).toMatchObject({ to: 'closing' });
    });
    expect(await screen.findByText(t.moved(t.statuses.closing))).toBeInTheDocument();
  });

  it('offers no next status for an archived season', async () => {
    open('archived');

    await screen.findByTestId('season-status');
    expect(screen.queryByTestId('season-next')).toBeNull();
  });

  it('shows why the season cannot be finished yet', async () => {
    open('closing', {
      'POST /api/admin/seasons/*/status': answer(409, {
        title: 'Rejected',
        status: 409,
        code: 'season.proofsPending',
      }),
    });

    await userEvent.click(await screen.findByTestId('season-next'));
    await userEvent.click(
      within(await screen.findByRole('alertdialog')).getByRole('button', {
        name: t.next.closing.label,
      }),
    );

    expect(await screen.findByText(ru.rejection['season.proofsPending'])).toBeInTheDocument();
  });

  it('shows the deadline in Moscow time and sets a new one typed in Moscow time', async () => {
    const server = open('active', { 'POST /api/admin/seasons/*/deadline': ok });

    const panel = await screen.findByTestId('season-deadline-panel');
    expect(panel).toHaveTextContent('МСК');
    await userEvent.click(within(panel).getByTestId('deadline-save'));
    expect(within(panel).getByText(t.deadlineRequired)).toBeInTheDocument();

    await userEvent.type(within(panel).getByTestId('deadline-input'), '2026-12-20T18:00');
    await userEvent.click(within(panel).getByTestId('deadline-save'));

    await waitFor(() => {
      expect(server.sent('POST', '/deadline')[0]?.body).toMatchObject({
        deadline: '2026-12-20T18:00:00+03:00',
      });
    });
  });

  it('removes the deadline', async () => {
    const server = open('active', { 'POST /api/admin/seasons/*/deadline': ok });

    await userEvent.click(await screen.findByTestId('deadline-remove'));

    await waitFor(() => {
      expect(server.sent('POST', '/deadline')[0]?.body).toMatchObject({ deadline: null });
    });
  });

  it('offers no deadline change once the season is closing', async () => {
    open('closing');

    await screen.findByTestId('season-deadline-panel');
    expect(screen.queryByTestId('deadline-input')).toBeNull();
  });

  it('links the season archive for download and tells how to load one', async () => {
    open();

    const link = await screen.findByTestId('season-export');
    expect(link).toHaveAttribute('href', `/api/admin/seasons/${seasonId}/export`);
    expect(link).toHaveAttribute('download');
    expect(screen.getByText(t.importLead)).toBeInTheDocument();
  });

  it('checks the integrity and lists the differences', async () => {
    open('active', {
      'GET /api/admin/seasons/*/integrity': {
        seasonId,
        lastSequence: 40,
        differences: ['player p1: points 12 ≠ 14'],
        settled: true,
        isIntact: false,
      },
    });

    await userEvent.click(await screen.findByTestId('integrity-check'));

    expect(await screen.findByText(t.notIntact)).toBeInTheDocument();
    expect(screen.getByText('player p1: points 12 ≠ 14')).toBeInTheDocument();
  });

  it('says when the state matches the log', async () => {
    open('active', {
      'GET /api/admin/seasons/*/integrity': {
        seasonId,
        lastSequence: 40,
        differences: [],
        settled: true,
        isIntact: true,
      },
    });

    await userEvent.click(await screen.findByTestId('integrity-check'));

    expect(await screen.findByText(t.intact(40))).toBeInTheDocument();
  });

  it('opens another season in the admin pages', async () => {
    const onPick = vi.fn();
    open('active', {}, onPick);

    await userEvent.click(await screen.findByTestId(`season-open-${other}`));

    expect(onPick).toHaveBeenCalledWith(other);
  });

  it('creates a season by its name and opens it', async () => {
    const onPick = vi.fn();
    const server = open('active', { 'POST /api/admin/seasons': ok }, onPick);

    await userEvent.click(await screen.findByTestId('season-create'));
    expect(screen.getByText(t.nameRequired)).toBeInTheDocument();
    await userEvent.type(screen.getByTestId('season-name'), 'Зима');
    await userEvent.click(screen.getByTestId('season-create'));

    await waitFor(() => {
      expect(server.sent('POST', '/api/admin/seasons')[0]?.body).toMatchObject({ name: 'Зима' });
    });
    const created = (server.sent('POST', '/api/admin/seasons')[0]?.body as { seasonId: string })
      .seasonId;
    expect(onPick).toHaveBeenCalledWith(created);
  });

  it('creates one season when the first answer was lost and the admin tries again', async () => {
    let lost = true;
    const server = open('active', {
      'POST /api/admin/seasons': () => {
        if (lost) {
          lost = false;
          throw new TypeError('Failed to fetch');
        }
        return { body: ok };
      },
    });

    await userEvent.type(await screen.findByTestId('season-name'), 'Зима');
    await userEvent.click(screen.getByTestId('season-create'));
    await screen.findByText(ru.admin.failed);
    await userEvent.click(screen.getByTestId('season-create'));

    await waitFor(() => {
      expect(server.sent('POST', '/api/admin/seasons')).toHaveLength(2);
    });
    const [first, again] = server
      .sent('POST', '/api/admin/seasons')
      .map((c) => c.body as { commandId: string; seasonId: string });
    expect(again?.seasonId).toBe(first?.seasonId);
    expect(again?.commandId).toBe(first?.commandId);
  });

  it('without a season offers only the list and a new season', async () => {
    fakeServer({ 'GET /api/seasons': [] });
    render(<SeasonSection seasonId={null} version={0} onPick={vi.fn()} />);

    expect(await screen.findByTestId('season-create')).toBeInTheDocument();
    expect(screen.queryByTestId('season-status')).toBeNull();
  });
});
