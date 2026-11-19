import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { Schemas } from '../api/client';
import { ru } from '../i18n/ru';
import { answer, fakeServer, seasonId, type Handler } from '../test/fakeServer';
import { IrlProofQueue } from './IrlProofQueue';
import { SuggestionsSection } from './SuggestionsSection';

const otherSeason = '5ea50000-0000-0000-0000-000000000002';
const irlPath = `/api/admin/seasons/${seasonId}/irl-proofs`;
const suggestionsPath = `/api/admin/seasons/${seasonId}/suggestions`;
const photoId = '10000000-0000-0000-0000-000000000001';
const ok: Schemas['CommandResponse'] = { duplicate: false, events: [] };

function irl(
  overrides: Partial<Schemas['IrlProofQueueItemView']> = {},
): Schemas['IrlProofQueueItemView'] {
  return {
    effectId: 'karaoke-proof',
    playerId: 'p1',
    playerName: 'Сова',
    objectId: 'karaoke',
    name: 'Караоке',
    description: 'Спой песню про кота.',
    files: [],
    link: 'https://video.example/recording',
    submittedAt: '2026-09-20T09:00:00+00:00',
    ...overrides,
  };
}

function suggestion(overrides: Partial<Schemas['SuggestionView']> = {}): Schemas['SuggestionView'] {
  return {
    id: 'suggestion-1',
    playerId: 'p1',
    kind: 'item',
    text: 'Свиток возвращения',
    game: null,
    submittedAt: '2026-09-20T09:00:00+00:00',
    status: 'pending',
    comment: null,
    ...overrides,
  };
}

const suggestedGame: Schemas['GameCard'] = {
  title: 'It Takes Two',
  tags: ['Кооп'],
  hours: 12,
  year: 2021,
  steamAppId: '1426210',
  coverFileId: null,
  note: 'Для двух игроков.',
  isCoop: true,
  completionCondition: 'Посмотреть финальные титры.',
};

function openQueue(
  items: Schemas['IrlProofQueueItemView'][] | Handler,
  review: Handler = answer(200, ok),
) {
  const server = fakeServer({
    [`GET ${irlPath}`]: items,
    'POST /api/admin/seasons/*/effects/*/irl-proof/review': review,
  });
  return { ...server, ...render(<IrlProofQueue seasonId={seasonId} version={0} />) };
}

function openSuggestions(
  items: Schemas['SuggestionView'][] | Handler,
  review: Handler = answer(200, ok),
) {
  const server = fakeServer({
    'GET /api/admin/seasons/*/players': [{ id: 'p1', name: 'Сова' }],
    [`GET ${suggestionsPath}`]: items,
    [`POST ${suggestionsPath}/*/review`]: review,
  });
  return { ...server, ...render(<SuggestionsSection seasonId={seasonId} version={0} />) };
}

afterEach(() => vi.unstubAllGlobals());

describe('IRL proof review', () => {
  it('follows the queue order and shows the event, player, private photos and a safe video link', async () => {
    openQueue([
      irl({
        files: [
          {
            id: photoId,
            url: `/api/files/${photoId}`,
            thumbnailUrl: `/api/files/${photoId}/thumbnail`,
          },
        ],
      }),
      irl({
        effectId: 'cooking-proof',
        name: 'Готовка',
        link: null,
        submittedAt: '2026-09-21T09:00:00+00:00',
      }),
    ]);

    const cards = await screen.findAllByRole('article');
    expect(cards.map((card) => within(card).getByRole('heading').textContent)).toEqual([
      'Караоке',
      'Готовка',
    ]);
    const [first] = cards;
    if (!first) throw new Error('No first proof');
    const karaoke = within(first);
    expect(karaoke.getByText('Сова')).toBeInTheDocument();
    expect(karaoke.getByText('Спой песню про кота.')).toBeInTheDocument();
    expect(karaoke.getByRole('img')).toHaveAttribute('src', `/api/files/${photoId}/thumbnail`);
    expect(karaoke.getByRole('link', { name: /открыть видео/i })).toHaveAttribute(
      'href',
      'https://video.example/recording',
    );
    expect(karaoke.getByRole('link', { name: /открыть видео/i })).toHaveAttribute(
      'rel',
      'noopener noreferrer',
    );
  });

  it('does not open unexpected external links or photos embedded in the response', async () => {
    openQueue([
      irl({
        link: 'javascript:alert(1)',
        files: [
          {
            id: photoId,
            url: 'https://outside.example/photo',
            thumbnailUrl: 'https://outside.example/collect',
          },
        ],
      }),
    ]);

    const card = within(await screen.findByRole('article'));
    expect(card.queryByRole('link')).not.toBeInTheDocument();
    expect(card.queryByRole('img')).not.toBeInTheDocument();
  });

  it('sends an explicit approval and refreshes to the empty queue', async () => {
    let items = [irl()];
    const { sent } = openQueue(
      () => ({ body: items }),
      () => {
        items = [];
        return { body: ok };
      },
    );
    await userEvent.click(await screen.findByRole('button', { name: 'Одобрить' }));

    expect(await screen.findByText('Нет IRL-пруфов на проверке')).toBeInTheDocument();
    const reviews = sent('POST', '/effects/karaoke-proof/irl-proof/review');
    expect(reviews).toHaveLength(1);
    expect(reviews[0]?.body).toMatchObject({ approved: true, comment: null });
    expect(reviews[0]?.body).toHaveProperty('commandId', expect.any(String));
    expect(sent('GET', '/irl-proofs')).toHaveLength(2);
  });

  it('requires a rejection comment, keeps a refusal in the dialog and sends the trimmed reason', async () => {
    const { sent } = openQueue([irl()], answer(409, { code: 'irlProof.notSubmitted' }));
    await userEvent.click(await screen.findByRole('button', { name: 'Отклонить' }));
    const dialog = within(screen.getByRole('alertdialog'));
    await userEvent.click(dialog.getByRole('button', { name: 'Отклонить' }));
    expect(dialog.getByText(ru.admin.commentRequired)).toBeInTheDocument();
    expect(sent('POST', '/irl-proof/review')).toEqual([]);

    await userEvent.type(
      dialog.getByRole('textbox', { name: ru.admin.comment }),
      '  Не слышно песню.  ',
    );
    await userEvent.click(dialog.getByRole('button', { name: 'Отклонить' }));
    expect(await dialog.findByText(ru.rejection['irlProof.notSubmitted'])).toBeInTheDocument();
    const review = sent('POST', '/effects/karaoke-proof/irl-proof/review')[0]?.body;
    expect(review).toMatchObject({ approved: false, comment: 'Не слышно песню.' });
    expect(review).toHaveProperty('commandId', expect.any(String));
  });

  it('retries a read error and clears the old queue when another season is selected', async () => {
    let fail = true;
    const { rerender, sent } = {
      ...fakeServer({
        [`GET ${irlPath}`]: () => (fail ? { status: 503 } : { body: [irl()] }),
        [`GET /api/admin/seasons/${otherSeason}/irl-proofs`]: [],
      }),
      ...render(<IrlProofQueue seasonId={seasonId} version={0} />),
    };
    expect(screen.getByTestId('loading')).toBeInTheDocument();
    const retry = await screen.findByRole('button', { name: ru.ui.retry });
    fail = false;
    await userEvent.click(retry);
    expect(await screen.findByRole('heading', { name: 'Караоке' })).toBeInTheDocument();

    rerender(<IrlProofQueue seasonId={otherSeason} version={0} />);
    expect(await screen.findByText('Нет IRL-пруфов на проверке')).toBeInTheDocument();
    expect(screen.queryByText('Караоке')).not.toBeInTheDocument();
    expect(sent('GET', '/irl-proofs').map((call) => call.path)).toEqual([
      irlPath,
      irlPath,
      `/api/admin/seasons/${otherSeason}/irl-proofs`,
    ]);
  });
});

describe('Suggestion moderation', () => {
  it('shows pending proposals by default and shows already considered proposals using the status filter', async () => {
    openSuggestions([
      suggestion({ kind: 'game', game: suggestedGame, text: 'Подходит для коопа.' }),
      suggestion({
        id: 'suggestion-2',
        kind: 'event',
        text: 'Ночной финиш',
        status: 'rejected',
        comment: 'Слишком долго.',
      }),
    ]);

    const game = within(await screen.findByRole('article'));
    expect(game.getByRole('heading', { name: 'It Takes Two' })).toBeInTheDocument();
    expect(game.getByText('Сова')).toBeInTheDocument();
    expect(game.getByText('Подходит для коопа.')).toBeInTheDocument();
    expect(game.getByText('Посмотреть финальные титры.')).toBeInTheDocument();
    expect(screen.queryByText('Ночной финиш')).not.toBeInTheDocument();

    await userEvent.selectOptions(screen.getByRole('combobox', { name: 'Статус' }), 'rejected');
    expect(screen.getByText('Ночной финиш')).toBeInTheDocument();
    expect(screen.getByText('Слишком долго.')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Одобрить' })).not.toBeInTheDocument();
  });

  it('displays a pool warning with a successful approval and shows the approved status after refresh', async () => {
    const proposal = suggestion({
      id: 'game-suggestion',
      kind: 'game',
      game: suggestedGame,
      text: '',
    });
    let items = [proposal];
    const { sent } = openSuggestions(
      () => ({ body: items }),
      () => {
        items = [{ ...proposal, status: 'approved' }];
        return { body: { ...ok, warnings: ['pool.similar'] } satisfies Schemas['CommandResponse'] };
      },
    );
    await userEvent.click(await screen.findByRole('button', { name: 'Одобрить' }));

    expect(await screen.findByText('Предложение одобрено')).toBeInTheDocument();
    expect(screen.getByText(/игра не добавлена в пул.*похожее название/i)).toBeInTheDocument();
    await waitFor(() =>
      expect(screen.queryByRole('heading', { name: 'It Takes Two' })).not.toBeInTheDocument(),
    );
    const review = sent('POST', '/game-suggestion/review')[0]?.body;
    expect(review).toMatchObject({ approve: true, comment: null });
    expect(review).toHaveProperty('commandId', expect.any(String));
    await userEvent.selectOptions(screen.getByRole('combobox', { name: 'Статус' }), 'approved');
    expect(await screen.findByRole('heading', { name: 'It Takes Two' })).toBeInTheDocument();
  });

  it('sends no implicit or unexplained rejection and shows the empty pending state after the decision', async () => {
    let items = [suggestion()];
    const { sent } = openSuggestions(
      () => ({ body: items }),
      () => {
        items = [{ ...suggestion(), status: 'rejected', comment: 'Нужен пример.' }];
        return { body: ok };
      },
    );
    await userEvent.click(await screen.findByRole('button', { name: 'Отклонить' }));
    const dialog = within(screen.getByRole('alertdialog'));
    await userEvent.click(dialog.getByRole('button', { name: 'Отклонить' }));
    expect(dialog.getByText(ru.admin.commentRequired)).toBeInTheDocument();
    expect(sent('POST', '/review')).toEqual([]);
    await userEvent.type(dialog.getByRole('textbox', { name: ru.admin.comment }), 'Нужен пример.');
    await userEvent.click(dialog.getByRole('button', { name: 'Отклонить' }));

    expect(await screen.findByText('Нет предложений на рассмотрении')).toBeInTheDocument();
    const review = sent('POST', '/suggestion-1/review')[0]?.body;
    expect(review).toMatchObject({ approve: false, comment: 'Нужен пример.' });
    expect(review).toHaveProperty('commandId', expect.any(String));
  });

  it('hides the list when a live refresh reports that suggestions were turned off', async () => {
    let enabled = true;
    const server = fakeServer({
      'GET /api/admin/seasons/*/players': [{ id: 'p1', name: 'Сова' }],
      [`GET ${suggestionsPath}`]: () =>
        enabled ? { body: [suggestion()] } : { status: 409, body: { code: 'feature.disabled' } },
    });
    const { rerender } = render(<SuggestionsSection seasonId={seasonId} version={0} />);
    expect(await screen.findByText('Свиток возвращения')).toBeInTheDocument();

    enabled = false;
    rerender(<SuggestionsSection seasonId={seasonId} version={1} />);
    expect(await screen.findByText('Предложка выключена')).toBeInTheDocument();
    expect(screen.queryByRole('article')).not.toBeInTheDocument();
    expect(server.sent('GET', '/suggestions')).toHaveLength(2);
    expect(server.sent('GET', '/players')).toHaveLength(1);
  });
});
