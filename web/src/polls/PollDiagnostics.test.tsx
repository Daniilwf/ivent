import { act, fireEvent, render, screen } from '@testing-library/react';
import { refreshCsrf, type Schemas } from '../api/client';
import { bugContext, clearBugContext, recordRequest, startBugContext } from '../app/bugContext';
import { shown } from '../app/screenshot';
import { fakeServer, json } from '../test/fakeServer';
import { PollCard } from './PollCard';
import { PollsScreen } from './PollsScreen';

vi.mock('../api/realtime', () => ({
  watchSeason: (_seasonId: string, onChange: (updates: []) => void) => {
    onChange([]);
    return () => undefined;
  },
}));

type Poll = Schemas['PollView'];

const seasonId = '30000000-0000-0000-0000-0000000000aa';
const pollId = 'a8450fc8-7435-47ef-a2b3-a31257e7961a';
const horrorId = 'e79ca6e8-7d83-4e71-9e2a-0a52b52008c5';

function poll(overrides: Partial<Poll> = {}): Poll {
  return {
    id: pollId,
    question: 'В каком жанре сыграем вместе?',
    options: [
      { id: horrorId, label: 'Хорроры' },
      { id: '903b75ba-c512-40b4-bce9-b48d41008d9a', label: 'Стратегии' },
    ],
    voters: 'players',
    anonymous: true,
    status: 'open',
    startedAt: '2030-11-17T12:00:00Z',
    closesAt: '2030-11-24T12:00:00Z',
    counts: null,
    result: null,
    myVote: horrorId,
    source: 'admin',
    voting: { canVote: true, reason: null, allowChangeVote: true },
    ...overrides,
  };
}

const onVote = () => Promise.resolve();

function actions() {
  return (bugContext().actions ?? []).map((action) => action.text);
}

describe('poll diagnostics', () => {
  let stop: () => void;

  beforeEach(() => {
    clearBugContext();
    stop = startBugContext();
  });

  afterEach(() => {
    stop();
  });

  it('excludes the checked anonymous choice and its draft replacement from the screenshot', () => {
    render(<PollCard poll={poll()} pending={false} onVote={onVote} />);
    const recorded = screen.getByRole('radio', { name: /Хорроры/ });
    expect(recorded).toBeChecked();
    expect(shown(recorded)).toBe(false);

    const draft = screen.getByRole('radio', { name: /Стратегии/ });
    fireEvent.click(draft);
    expect(draft).toBeChecked();
    expect(shown(draft)).toBe(false);
  });

  it('excludes the read-only own anonymous vote from screenshots', () => {
    render(
      <PollCard
        poll={poll({
          voting: { canVote: false, reason: 'poll.alreadyVoted', allowChangeVote: false },
        })}
        pending={false}
        onVote={onVote}
      />,
    );
    const confirmation = screen.getByText('Твой голос: Хорроры');
    expect(confirmation).toBeVisible();
    expect(shown(confirmation)).toBe(false);
  });

  it('excludes nested private text from the captured label of an ordinary click', () => {
    render(
      <button type="button" data-testid="poll-region">
        Назад <span data-private>Твой голос: Хорроры</span>
      </button>,
    );

    fireEvent.click(screen.getByTestId('poll-region'));

    expect(actions()).toEqual(['click button[data-testid=poll-region] «Назад»']);
  });

  it('omits clicks on anonymous vote options and submit while capturing an ordinary click', async () => {
    render(
      <>
        <PollCard poll={poll({ myVote: null })} pending={false} onVote={onVote} />
        <button type="button">Открыть карту</button>
      </>,
    );

    fireEvent.click(screen.getByText('Стратегии'));
    expect(screen.getByRole('radio', { name: /Стратегии/ })).toBeChecked();
    await act(() => {
      fireEvent.click(screen.getByRole('button', { name: 'Голосовать' }));
      return Promise.resolve();
    });
    fireEvent.click(screen.getByRole('button', { name: 'Открыть карту' }));

    expect(actions()).toEqual(['click button «Открыть карту»']);
  });

  it('keeps a named poll choice visible in the screenshot', () => {
    render(<PollCard poll={poll({ anonymous: false })} pending={false} onVote={onVote} />);
    const recorded = screen.getByRole('radio', { name: /Хорроры/ });

    expect(recorded).toBeChecked();
    expect(shown(recorded)).toBe(true);
  });

  it('does not attach vote timing or the accompanying list refresh to a named report', () => {
    const seasonUrl = `${location.origin}/api/seasons/${seasonId}`;
    const pollUrl = `${seasonUrl}/polls/${pollId}`;

    recordRequest('POST', `${pollUrl}/vote`, 200);
    recordRequest('POST', `${pollUrl}/vote/`, 409);
    recordRequest('GET', `${seasonUrl}/polls`, 200);
    recordRequest('GET', `${seasonUrl}/polls/`, 503);
    recordRequest('GET', pollUrl, 200);
    recordRequest('POST', `${seasonUrl}/roll`, 409);

    expect(actions()).toEqual([
      'GET /api/seasons/…/polls/… → 200',
      'POST /api/seasons/…/roll → 409',
    ]);
  });

  it('does not include the acknowledgement or its retry when refreshing an anonymous vote fails', async () => {
    let reads = 0;
    const url = `/api/seasons/${seasonId}/polls`;
    fakeServer({
      [`GET ${url}`]: () => (++reads === 1 ? json(200, [poll({ myVote: null })]) : json(503)),
      [`POST ${url}/${pollId}/vote`]: { duplicate: false, events: [] },
    });
    await refreshCsrf();
    render(<PollsScreen seasonId={seasonId} onSignedOut={vi.fn()} />);
    fireEvent.click(await screen.findByRole('radio', { name: /Стратегии/ }));
    fireEvent.click(screen.getByRole('button', { name: 'Голосовать' }));
    const note = await screen.findByText('Голос принят. Не удалось обновить список.');
    const retry = screen.getByRole('button', { name: 'Обновить список' });
    fireEvent.click(retry);
    expect(shown(note)).toBe(false);
    expect(shown(retry)).toBe(false);
    expect(actions().join('\n')).not.toContain('Обновить список');
  });
});
