import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { Schemas } from '../api/client';
import { ru } from '../i18n/ru';
import { answer, fakeServer, seasonId } from '../test/fakeServer';
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
const finished = '5ea50000-0000-0000-0000-000000000003';
const templateRoutes = {
  'GET /api/status': { seasonTemplates: true },
  'GET /api/seasons': [
    ...list,
    { ...list[1], id: finished, name: 'Лето', status: 'finished' },
    { ...list[1], id: 'closing', name: 'Проверка', status: 'closing' },
    { ...list[1], id: 'draft', name: 'Черновик', status: 'draft' },
  ],
};

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
    expect(status).toHaveTextContent(ru.seasonStatus['active'] ?? '');
    await userEvent.click(within(status).getByTestId('season-next'));
    const dialog = await screen.findByRole('alertdialog');
    for (const line of t.next.active.consequences)
      expect(within(dialog).getByText(line)).toBeInTheDocument();
    await userEvent.click(within(dialog).getByRole('button', { name: t.next.active.label }));

    await waitFor(() => {
      expect(server.sent('POST', '/status')[0]?.body).toMatchObject({ to: 'closing' });
    });
    expect(await screen.findByText(t.moved(ru.seasonStatus['closing'] ?? ''))).toBeInTheDocument();
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

  it('asks again before a deadline within a day: after it the turns close for good (K1A, D-101)', async () => {
    const server = open('active', { 'POST /api/admin/seasons/*/deadline': ok });
    // Two hours from now in Moscow time, as the field takes it (a day typed wrong, say)
    const soon = new Date(Date.now() + 2 * 60 * 60 * 1000 + 60_000);
    const parts = Object.fromEntries(
      new Intl.DateTimeFormat('en-CA', {
        timeZone: 'Europe/Moscow',
        year: 'numeric',
        month: '2-digit',
        day: '2-digit',
        hour: '2-digit',
        minute: '2-digit',
        hourCycle: 'h23',
      })
        .formatToParts(soon)
        .map((p) => [p.type, p.value]),
    );
    const typed = `${parts.year}-${parts.month}-${parts.day}T${parts.hour}:${parts.minute}`;

    const panel = await screen.findByTestId('season-deadline-panel');
    await userEvent.type(within(panel).getByTestId('deadline-input'), typed);
    await userEvent.click(within(panel).getByTestId('deadline-save'));

    const dialog = await screen.findByRole('alertdialog');
    expect(dialog).toHaveTextContent(t.deadlineSoonTitle);
    expect(server.sent('POST', '/deadline')).toHaveLength(0);
    await userEvent.click(within(dialog).getByRole('button', { name: t.deadlineSoonConfirm }));
    await waitFor(() => {
      expect(server.sent('POST', '/deadline')).toHaveLength(1);
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

  it('gets to the existing creation form from the top of a long season list', async () => {
    const archives = Array.from({ length: 12 }, (_, index) => ({
      id: `archive-${index}`,
      name: `Длинное название завершённого архивного сезона ${index}`,
      status: 'archived',
      deadline: null,
      createdAt: '2026-03-01T00:00:00Z',
    }));
    const originalScroll = Object.getOwnPropertyDescriptor(Element.prototype, 'scrollIntoView');
    const scrollIntoView = vi.fn();
    Object.defineProperty(Element.prototype, 'scrollIntoView', {
      configurable: true,
      value: scrollIntoView,
    });
    try {
      open('active', { 'GET /api/seasons': [...archives, ...list] });
      const shortcut = await screen.findByRole('button', { name: 'К созданию сезона' });
      const heading = screen.getByRole('heading', { name: t.create, level: 2 });
      await userEvent.tab();
      expect(shortcut).toHaveFocus();

      await userEvent.keyboard('{Enter}');

      expect(heading).toHaveFocus();
      expect(screen.getAllByTestId('season-name')).toHaveLength(1);
      expect(scrollIntoView).toHaveBeenCalledWith({ block: 'center' });
      await userEvent.tab();
      expect(screen.getByTestId('season-name')).toHaveFocus();
    } finally {
      if (originalScroll) {
        Object.defineProperty(Element.prototype, 'scrollIntoView', originalScroll);
      } else {
        Reflect.deleteProperty(Element.prototype, 'scrollIntoView');
      }
    }
  });

  it('creates a season by its name and opens it', async () => {
    const onPick = vi.fn();
    const server = open('active', { 'POST /api/admin/seasons': ok }, onPick);

    await userEvent.click(await screen.findByTestId('season-create'));
    expect(screen.getByText(ru.ui.nameRequired)).toBeInTheDocument();
    await userEvent.type(screen.getByTestId('season-name'), 'Зима');
    await userEvent.click(screen.getByTestId('season-create'));

    await waitFor(() => {
      expect(server.sent('POST', '/api/admin/seasons')[0]?.body).toMatchObject({ name: 'Зима' });
    });
    const created = (server.sent('POST', '/api/admin/seasons')[0]?.body as { seasonId: string })
      .seasonId;
    expect(onPick).toHaveBeenCalledWith(created);
  });

  it('keeps season templates hidden when the site capability is disabled', async () => {
    const server = open('active', {
      'GET /api/status': { seasonTemplates: false },
      'POST /api/admin/seasons': ok,
    });

    await userEvent.type(await screen.findByTestId('season-name'), 'Зима');
    expect(screen.queryByRole('combobox', { name: 'Сезон по образцу' })).toBeNull();
    expect(screen.queryByTestId('season-template-deadline')).toBeNull();
    await userEvent.click(screen.getByTestId('season-create'));

    await waitFor(() => {
      expect(server.sent('POST', '/api/admin/seasons')).toHaveLength(1);
    });
    const body = server.sent('POST', '/api/admin/seasons')[0]?.body;
    expect(body).not.toHaveProperty('sourceSeasonId');
    expect(body).not.toHaveProperty('deadline');
  });

  it('offers only completed sources and opens the copy with the new Moscow deadline', async () => {
    const onPick = vi.fn();
    const server = open('active', { ...templateRoutes, 'POST /api/admin/seasons': ok }, onPick);

    const source = await screen.findByRole('combobox', { name: 'Сезон по образцу' });
    expect(source).toHaveValue('');
    expect(
      within(source)
        .getAllByRole('option')
        .map((option) => option.textContent),
    ).toEqual(['Без образца', 'Весна', 'Лето']);
    expect(screen.queryByTestId('season-template-deadline')).toBeNull();

    await userEvent.selectOptions(source, finished);
    expect(screen.getByText(t.templateLead('Лето'))).toBeInTheDocument();
    await userEvent.type(screen.getByTestId('season-name'), 'Новый сезон');
    await userEvent.type(screen.getByTestId('season-template-deadline'), '2028-12-20T18:00');
    await userEvent.click(screen.getByTestId('season-create'));

    await waitFor(() => {
      expect(server.sent('POST', '/api/admin/seasons')[0]?.body).toMatchObject({
        name: 'Новый сезон',
        sourceSeasonId: finished,
        deadline: '2028-12-20T18:00:00+03:00',
      });
    });
    const body = server.sent('POST', '/api/admin/seasons')[0]
      ?.body as Schemas['CreateSeasonRequest'];
    expect(onPick).toHaveBeenCalledWith(body.seasonId);
    expect(await screen.findByText(t.created('Новый сезон'))).toBeInTheDocument();
  });

  it('retries an unchanged copy once and uses new ids after changing the source', async () => {
    const server = open('active', {
      ...templateRoutes,
      'POST /api/admin/seasons': () => {
        throw new TypeError('Failed to fetch');
      },
    });

    const source = await screen.findByRole('combobox', { name: 'Сезон по образцу' });
    await userEvent.selectOptions(source, finished);
    await userEvent.type(screen.getByTestId('season-name'), 'Новый сезон');
    const submit = screen.getByTestId('season-create');
    await userEvent.click(submit);
    await screen.findByText(ru.admin.failed);
    await userEvent.click(submit);
    await screen.findByText(ru.admin.failed);
    await userEvent.selectOptions(source, other);
    await userEvent.click(submit);

    await waitFor(() => {
      expect(server.sent('POST', '/api/admin/seasons')).toHaveLength(3);
    });
    const [first, again, changed] = server
      .sent('POST', '/api/admin/seasons')
      .map((call) => call.body as Schemas['CreateSeasonRequest']);
    expect(first).toMatchObject({ sourceSeasonId: finished });
    expect(first).not.toHaveProperty('deadline');
    expect(again).toEqual(first);
    expect(changed).toMatchObject({ sourceSeasonId: other });
    expect(changed?.seasonId).not.toBe(first?.seasonId);
    expect(changed?.commandId).not.toBe(first?.commandId);
  });

  it.each([
    ['season.templateUnavailable', ru.admin.rejection['season.templateUnavailable'] ?? ''],
    ['content.invalid', t.incompatibleTemplateContent],
    ['feature.disabled', t.incompatibleTemplateContent],
  ])('shows a reason that applies to the source after %s', async (code, message) => {
    open('active', {
      ...templateRoutes,
      'POST /api/admin/seasons': answer(409, { status: 409, code }),
    });

    const source = await screen.findByRole('combobox', { name: 'Сезон по образцу' });
    await userEvent.selectOptions(source, other);
    await userEvent.type(screen.getByTestId('season-name'), 'Зима');
    await userEvent.click(screen.getByTestId('season-create'));

    expect(await screen.findByText(message)).toBeVisible();
    expect(source).toHaveValue(other);
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
