import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { ru } from '../i18n/ru';
import { fakeServer, seasonId } from '../test/fakeServer';
import { SeasonSection } from './SeasonSection';

const enter = 'Подготовить голосование';
const launch = 'Открыть голосование';
const category = 'Номинация';
const review = 'Лучший отзыв';
const game = 'Игра сезона';
const hidden = 'Самый смешной момент';

const configured = {
  features: { seasonNominations: true, polls: true },
  nominations: { voted: ['bestReview', 'gameOfSeason'] },
};
const candidates = {
  category: 'bestReview',
  durationHours: 24,
  minCandidates: 2,
  maxCandidates: 2,
  candidates: [
    { id: 'first', label: 'Celeste', detail: 'Больше всего запомнилась вершина.' },
    { id: 'second', label: 'Hades', detail: 'Понравилось возвращаться в дом.' },
    { id: 'third', label: 'Portal', detail: 'Хорошие головоломки.' },
  ],
};
const success = { duplicate: false, events: [] };

function open(status: 'active' | 'closing', routes: Parameters<typeof fakeServer>[0] = {}) {
  const season = { id: seasonId, name: 'Осень', status, deadline: null, cells: [] };
  const server = fakeServer({
    'GET /api/seasons': [season],
    'GET /api/seasons/*': season,
    'GET /api/seasons/*/rules': { ruleset: configured },
    'GET /api/seasons/*/polls': [],
    'GET /api/admin/seasons/*/nominations/candidates': candidates,
    'POST /api/admin/seasons/*/nominations/polls': success,
    ...routes,
  });
  render(<SeasonSection seasonId={seasonId} version={0} onPick={vi.fn()} />);
  return server;
}

async function compose() {
  await userEvent.click(await screen.findByRole('button', { name: enter }));
  return within(await screen.findByRole('dialog'));
}

describe('Final nomination ballot in the season section', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('only shows the opening action in Closing with both switches on', async () => {
    const server = open('active');
    await screen.findByTestId('season-status');
    expect(screen.queryByRole('button', { name: enter })).not.toBeInTheDocument();
    expect(server.sent('GET', '/rules')).toHaveLength(0);
  });

  it('keeps the action hidden when ordinary polls are disabled', async () => {
    const server = open('closing', {
      'GET /api/seasons/*/rules': {
        ruleset: { ...configured, features: { ...configured.features, polls: false } },
      },
    });
    await waitFor(() => {
      expect(server.sent('GET', '/rules')).toHaveLength(1);
    });
    expect(screen.queryByRole('button', { name: enter })).not.toBeInTheDocument();
  });

  it('loads a configured category lazily and submits only the selected candidates', async () => {
    const server = open('closing');
    const trigger = await screen.findByRole('button', { name: enter });
    expect(server.sent('GET', '/nominations/candidates')).toHaveLength(0);
    const dialog = await compose();
    expect(await dialog.findByRole('option', { name: review })).toBeInTheDocument();
    expect(dialog.getByRole('option', { name: game })).toBeInTheDocument();
    expect(dialog.queryByRole('option', { name: hidden })).not.toBeInTheDocument();
    expect(server.sent('GET', '/nominations/candidates')).toHaveLength(0);
    await userEvent.selectOptions(dialog.getByRole('combobox', { name: category }), 'bestReview');
    const first = await dialog.findByRole('checkbox', { name: 'Celeste' });
    expect(first).toHaveAccessibleDescription('Больше всего запомнилась вершина.');
    expect(dialog.getByRole('button', { name: launch })).toBeDisabled();
    await userEvent.click(first);
    await userEvent.click(dialog.getByRole('checkbox', { name: 'Hades' }));
    expect(dialog.getByRole('checkbox', { name: 'Portal' })).toBeDisabled();
    expect(server.sent('GET', '/nominations/candidates')[0]?.query.get('category')).toBe(
      'bestReview',
    );
    await userEvent.click(dialog.getByRole('button', { name: launch }));
    expect(await screen.findByText('Итоговое голосование открыто.')).toBeInTheDocument();
    const body = server.sent('POST', '/nominations/polls')[0]?.body;
    expect(body).toHaveProperty('commandId');
    expect(body).toHaveProperty('pollId');
    expect(body).toMatchObject({
      category: 'bestReview',
      candidateIds: ['first', 'second'],
    });
    expect(trigger).toHaveFocus();
  });

  it('offers a cancelled nomination again and explains a pending proof without losing the selection', async () => {
    let posts = 0;
    const server = open('closing', {
      'GET /api/seasons/*/polls': [
        { id: 'open', nomination: 'gameOfSeason', status: 'open' },
        { id: 'cancelled', nomination: 'bestReview', status: 'cancelled' },
      ],
      'POST /api/admin/seasons/*/nominations/polls': () => {
        posts += 1;
        return posts === 1
          ? { status: 409, body: { code: 'season.proofsPending' } }
          : { body: success };
      },
    });
    const dialog = await compose();
    expect(await dialog.findByRole('option', { name: /Игра сезона/ })).toBeDisabled();
    expect(dialog.getByRole('option', { name: review })).toBeEnabled();
    await userEvent.selectOptions(dialog.getByRole('combobox', { name: category }), 'bestReview');
    const first = await dialog.findByRole('checkbox', { name: 'Celeste' });
    await userEvent.click(first);
    await userEvent.click(dialog.getByRole('checkbox', { name: 'Hades' }));
    const submit = dialog.getByRole('button', { name: launch });
    await userEvent.click(submit);
    expect(await dialog.findByRole('alert')).toHaveTextContent(
      ru.rejection['season.proofsPending'],
    );
    expect(first).toBeChecked();
    await userEvent.click(submit);
    expect(await screen.findByText('Итоговое голосование открыто.')).toBeInTheDocument();
    expect(server.sent('POST', '/nominations/polls')).toHaveLength(2);
  });

  it('keeps the command and poll IDs when an opening reply is lost', async () => {
    let lost = true;
    const server = open('closing', {
      'POST /api/admin/seasons/*/nominations/polls': () => {
        if (lost) {
          lost = false;
          throw new TypeError('The connection closed before the reply.');
        }
        return { body: success };
      },
    });
    const dialog = await compose();
    await dialog.findByRole('option', { name: review });
    await userEvent.selectOptions(dialog.getByRole('combobox', { name: category }), 'bestReview');
    await userEvent.click(await dialog.findByRole('checkbox', { name: 'Celeste' }));
    await userEvent.click(dialog.getByRole('checkbox', { name: 'Hades' }));
    const submit = dialog.getByRole('button', { name: launch });
    await userEvent.click(submit);
    expect(await dialog.findByRole('alert')).toHaveTextContent(ru.admin.failed);
    await userEvent.click(submit);
    expect(await screen.findByText('Итоговое голосование открыто.')).toBeInTheDocument();
    const posts = server.sent('POST', '/nominations/polls');
    expect(posts).toHaveLength(2);
    expect(posts[1]?.body).toEqual(posts[0]?.body);
  });
});
