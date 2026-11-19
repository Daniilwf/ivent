import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { ru } from '../i18n/ru';
import { answer, fakeServer, seasonId } from '../test/fakeServer';
import { WeeklyChallengesSection } from './WeeklyChallengesSection';

const challengeId = 'ac100000-0000-0000-0000-000000000002';
const playerId = 'ac100000-0000-0000-0000-000000000003';
const runId = 'ac100000-0000-0000-0000-000000000004';
const gameId = 'ac100000-0000-0000-0000-000000000005';
const challenge = {
  id: challengeId,
  name: 'Неделя хоррора',
  description: 'Пройди хоррор.',
  check: 'manual',
  rewardCoins: 10,
  startsAt: '2026-11-15T12:00:00Z',
  endsAt: '2026-11-22T12:00:00Z',
  completedBy: [],
};
const run = {
  runId,
  seasonId,
  playerId,
  playerName: 'Маша',
  status: 'completed',
  completedAt: '2026-11-16T12:00:00Z',
};
const ok = { events: [], duplicate: false };

describe('The admin weekly challenges', () => {
  afterEach(() => vi.unstubAllGlobals());

  it('sets an automatic challenge from a filter, reward and Moscow start time', async () => {
    const user = userEvent.setup();
    const server = fakeServer({
      'GET /api/seasons/*/weekly-challenges': [],
      'POST /api/admin/seasons/*/weekly-challenges': ok,
    });
    render(<WeeklyChallengesSection seasonId={seasonId} version={0} />);

    await user.click(await screen.findByTestId('weekly-challenge-open-set'));
    const dialog = within(await screen.findByRole('dialog'));
    await user.type(dialog.getByTestId('weekly-challenge-name'), challenge.name);
    await user.type(dialog.getByTestId('weekly-challenge-description'), challenge.description);
    await user.type(dialog.getByTestId('weekly-challenge-starts'), '2026-11-15T15:00');
    await user.type(dialog.getByTestId('weekly-challenge-coins'), '10');
    await user.type(dialog.getByTestId('weekly-challenge-tags'), 'Horror');
    await user.click(dialog.getByTestId('weekly-challenge-set'));

    await waitFor(() => {
      expect(server.sent('POST', '/weekly-challenges')).toHaveLength(1);
    });
    expect(server.sent('POST', '/weekly-challenges')[0]?.body).toMatchObject({
      startsAt: '2026-11-15T15:00:00+03:00',
      challenge: {
        name: challenge.name,
        description: challenge.description,
        reward: { coins: 10 },
        check: 'auto',
        condition: { game: { tags: ['Horror'] } },
      },
    });
    expect(server.sent('POST', '/weekly-challenges')[0]?.body).toHaveProperty('commandId');
  });

  it('sets a manual challenge with a written task and reports a command refusal in place', async () => {
    const user = userEvent.setup();
    const server = fakeServer({
      'GET /api/seasons/*/weekly-challenges': [],
      'POST /api/admin/seasons/*/weekly-challenges': answer(409, { code: 'season.deadlinePassed' }),
    });
    render(<WeeklyChallengesSection seasonId={seasonId} version={0} />);

    await user.click(await screen.findByTestId('weekly-challenge-open-set'));
    const dialog = within(await screen.findByRole('dialog'));
    await user.selectOptions(dialog.getByTestId('weekly-challenge-check'), 'manual');
    expect(dialog.queryByTestId('weekly-challenge-tags')).not.toBeInTheDocument();
    await user.type(dialog.getByTestId('weekly-challenge-name'), challenge.name);
    await user.type(dialog.getByTestId('weekly-challenge-description'), challenge.description);
    await user.type(dialog.getByTestId('weekly-challenge-starts'), '2026-11-15T15:00');
    await user.type(dialog.getByTestId('weekly-challenge-coins'), '10');
    await user.click(dialog.getByTestId('weekly-challenge-set'));

    expect(await dialog.findByText(ru.rejection['season.deadlinePassed'])).toBeInTheDocument();
    expect(server.sent('POST', '/weekly-challenges')[0]?.body).toMatchObject({
      challenge: {
        name: challenge.name,
        description: challenge.description,
        check: 'manual',
        condition: { game: { minHours: 0 } },
      },
    });
  });

  it('reuses the submitted command when retrying after a lost reply', async () => {
    const user = userEvent.setup();
    let attempts = 0;
    const server = fakeServer({
      'GET /api/seasons/*/weekly-challenges': [],
      'POST /api/admin/seasons/*/weekly-challenges': () => {
        if (attempts++ === 0) throw new TypeError('Connection closed before the reply');
        return { body: ok };
      },
    });
    render(<WeeklyChallengesSection seasonId={seasonId} version={0} />);

    await user.click(await screen.findByTestId('weekly-challenge-open-set'));
    const dialog = within(await screen.findByRole('dialog'));
    await user.selectOptions(dialog.getByTestId('weekly-challenge-check'), 'manual');
    await user.type(dialog.getByTestId('weekly-challenge-name'), challenge.name);
    await user.type(dialog.getByTestId('weekly-challenge-description'), challenge.description);
    await user.type(dialog.getByTestId('weekly-challenge-starts'), '2026-11-15T15:00');
    await user.type(dialog.getByTestId('weekly-challenge-coins'), '10');
    await user.click(dialog.getByTestId('weekly-challenge-set'));
    expect(await dialog.findByText(ru.admin.failed)).toBeInTheDocument();
    await user.click(dialog.getByTestId('weekly-challenge-set'));

    await waitFor(() => {
      expect(server.sent('POST', '/weekly-challenges')).toHaveLength(2);
    });
    const [first, retry] = server.sent('POST', '/weekly-challenges');
    expect(retry?.body).toEqual(first?.body);
  });

  it('marks a manual challenge using a completed run from this season and window', async () => {
    const user = userEvent.setup();
    let marked = false;
    const server = fakeServer({
      'GET /api/seasons/*/weekly-challenges': () => ({
        body: [
          { ...challenge, completedBy: marked ? [{ playerId, name: 'Маша' }] : [] },
          { ...challenge, id: 'auto', name: 'Авто', check: 'auto' },
        ],
      }),
      'GET /api/pool': [{ id: gameId, title: 'Amnesia', isDeleted: true }],
      'GET /api/pool/*/runs': [
        run,
        { ...run, runId: 'foreign', seasonId: 'other', playerName: 'Чужой сезон' },
        { ...run, runId: 'late', completedAt: challenge.endsAt, playerName: 'После конца' },
        { ...run, runId: 'rejected', status: 'rejected', playerName: 'Реджеkт' },
      ],
      'POST /api/admin/seasons/*/weekly-challenges/*/mark': () => {
        marked = true;
        return { body: ok };
      },
    });
    render(<WeeklyChallengesSection seasonId={seasonId} version={0} />);

    await user.click(await screen.findByTestId('weekly-challenge-open-mark'));
    const dialog = within(await screen.findByRole('dialog'));
    const gameSelect = await dialog.findByTestId('weekly-challenge-game');
    expect(gameSelect).toHaveTextContent(ru.gamePage.deleted);
    expect(server.sent('GET', '/pool')[0]?.query.get('deleted')).toBe('true');
    await user.selectOptions(gameSelect, gameId);
    const runSelect = await dialog.findByTestId('weekly-challenge-run');
    expect(runSelect).toHaveTextContent('Маша');
    expect(runSelect).not.toHaveTextContent(/Чужой сезон|После конца|Реджеkт/);
    await user.selectOptions(runSelect, runId);
    await user.click(dialog.getByTestId('weekly-challenge-mark'));

    await waitFor(() => {
      expect(server.sent('POST', '/mark')).toHaveLength(1);
    });
    expect(server.sent('POST', '/mark')[0]?.body).toMatchObject({ playerId, runId });
    expect(await screen.findByText('Маша')).toBeInTheDocument();
  });

  it('hides writes when the selected season disables the feature', async () => {
    fakeServer({
      'GET /api/seasons/*/weekly-challenges': answer(409, { code: 'feature.disabled' }),
    });
    render(<WeeklyChallengesSection seasonId={seasonId} version={0} />);

    await waitFor(() => {
      expect(screen.queryByTestId('loading')).toBeNull();
    });
    expect(screen.queryByTestId('weekly-challenge-open-set')).toBeNull();
  });
});
