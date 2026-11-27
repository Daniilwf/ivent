import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { ru } from '../i18n/ru';
import { answer, fakeServer, seasonId } from '../test/fakeServer';
import { GlobalEventsSection } from './GlobalEventsSection';

const eventId = 'ac100000-0000-0000-0000-000000000001';
const event = {
  id: eventId,
  objectId: 'weekend',
  name: 'Общий выходной',
  description: 'Играем вместе',
  startsAt: '2026-11-15T12:00:00Z',
  endsAt: '2026-11-16T12:00:00Z',
  status: 'active',
  goal: { metric: 'runs', target: 10, progress: 2, reached: false },
};

const catalog = {
  pack: {
    objects: [
      { id: 'weekend', kind: 'event', name: 'Общий выходной' },
      { id: 'token', kind: 'item', name: 'Жетон' },
    ],
  },
};

describe('The admin global events', () => {
  afterEach(() => vi.unstubAllGlobals());

  it('starts a published event with its own duration and goal and reloads the list', async () => {
    const user = userEvent.setup();
    let launched = false;
    const server = fakeServer({
      'GET /api/seasons/*/global-events': () => ({ body: launched ? [event] : [] }),
      'GET /api/admin/seasons/*/content': catalog,
      'POST /api/admin/seasons/*/global-events': () => {
        launched = true;
        return { body: { events: [], duplicate: false } };
      },
    });
    render(<GlobalEventsSection seasonId={seasonId} version={0} />);

    const trigger = await screen.findByTestId('global-event-open-launch');
    await user.click(trigger);
    const dialog = within(await screen.findByRole('dialog'));
    await user.selectOptions(await dialog.findByTestId('global-event-object'), 'weekend');
    expect(dialog.queryByRole('option', { name: 'Жетон' })).toBeNull();
    await user.clear(dialog.getByTestId('global-event-duration'));
    await user.type(dialog.getByTestId('global-event-duration'), '24');
    await user.selectOptions(dialog.getByTestId('global-event-goal'), 'runs');
    await user.type(dialog.getByTestId('global-event-target'), '10');
    await user.click(dialog.getByTestId('global-event-launch'));

    await waitFor(() => {
      expect(server.sent('POST', '/global-events')).toHaveLength(1);
    });
    expect(server.sent('POST', '/global-events')[0]?.body).toMatchObject({
      objectId: 'weekend',
      durationHours: 24,
      goal: { metric: 'runs', target: 10 },
    });
    expect(server.sent('POST', '/global-events')[0]?.body).toHaveProperty('commandId');
    expect(await screen.findByRole('heading', { name: event.name })).toBeInTheDocument();
    expect(screen.getByText(/2\s*из\s*10/)).toBeInTheDocument();
    await waitFor(() => {
      expect(trigger).toHaveFocus();
    });
  });

  it('confirms early ending and refreshes the history with the terminal status', async () => {
    const user = userEvent.setup();
    let ended = false;
    const server = fakeServer({
      'GET /api/seasons/*/global-events': () => ({
        body: [{ ...event, status: ended ? 'ended' : 'active' }],
      }),
      'POST /api/admin/seasons/*/global-events/*/end': () => {
        ended = true;
        return { body: { events: [], duplicate: false } };
      },
    });
    render(<GlobalEventsSection seasonId={seasonId} version={0} />);

    await user.click(await screen.findByTestId('global-event-open-end'));
    expect(server.sent('POST', '/end')).toHaveLength(0);
    await user.click(
      within(await screen.findByRole('alertdialog')).getByTestId('global-event-end-yes'),
    );
    expect(await screen.findByText('Завершён')).toBeInTheDocument();
    expect(server.sent('POST', '/end')[0]?.path).toContain(eventId);
  });

  it('hides writes when the selected season disables the feature', async () => {
    const server = fakeServer({
      'GET /api/seasons/*/global-events': answer(409, { code: 'feature.disabled' }),
    });
    render(<GlobalEventsSection seasonId={seasonId} version={0} />);

    await waitFor(() => {
      expect(screen.queryByTestId('loading')).toBeNull();
    });
    expect(screen.queryByTestId('global-event-open-launch')).toBeNull();
    expect(server.sent('GET', '/content')).toHaveLength(0);
  });

  it('rejects an invalid duration locally and keeps the draft after a command refusal', async () => {
    const user = userEvent.setup();
    const server = fakeServer({
      'GET /api/seasons/*/global-events': [],
      'GET /api/admin/seasons/*/content': catalog,
      'POST /api/admin/seasons/*/global-events': answer(409, { code: 'globalEvent.tooMany' }),
    });
    render(<GlobalEventsSection seasonId={seasonId} version={0} />);

    await user.click(await screen.findByTestId('global-event-open-launch'));
    const dialog = within(await screen.findByRole('dialog'));
    await user.selectOptions(await dialog.findByTestId('global-event-object'), 'weekend');
    const duration = dialog.getByTestId('global-event-duration');
    const launch = dialog.getByTestId('global-event-launch');
    await user.click(launch);
    expect(dialog.getByRole('alert')).toHaveTextContent(/число часов/);
    expect(server.sent('POST', '/global-events')).toHaveLength(0);

    await user.type(duration, '24');
    await user.click(launch);
    expect(await dialog.findByText(ru.rejection['globalEvent.tooMany'])).toBeInTheDocument();
    expect(duration).toHaveValue(24);
  });
});
