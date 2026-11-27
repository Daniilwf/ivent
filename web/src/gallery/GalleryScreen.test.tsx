import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import type { SeasonUpdate } from '../api/realtime';
import { fakeServer, json, type Call } from '../test/fakeServer';
import { GalleryScreen } from './GalleryScreen';

const subscriptions = vi.hoisted(() => new Set<(updates: SeasonUpdate[]) => void>());
vi.mock('../api/realtime', () => ({
  watchSeason(_season: string, notify: (updates: SeasonUpdate[]) => void) {
    subscriptions.add(notify);
    notify([]);
    return () => {
      subscriptions.delete(notify);
    };
  },
}));

const seasonId = '1b8a7070-032d-4e0e-87c4-0cad83348c79';
const itemId = '6080a751-1070-4995-a6aa-bc672c7f1145';
const readPath = `/api/seasons/${seasonId}/gallery`;

const seasonList = [{ id: seasonId, name: 'Сезон' }];
const view = {
  items: [
    {
      id: itemId,
      playerId: 'player',
      playerName: 'Лиса',
      caption: 'Первый момент',
      createdAt: '2026-11-20T10:00:00Z',
      imageUrl: '/photo',
      thumbnailUrl: '/thumb',
      canWithdraw: false,
      reactions: [{ emoji: '🔥', selected: false, count: 0 }],
    },
  ],
  nextBefore: null,
  players: [{ id: 'player', name: 'Лиса', token: 0 }],
  canPublish: false,
};

function notify(type: string) {
  for (const subscription of subscriptions) {
    subscription([{ seasonId, fromSequence: 1, toSequence: 2, types: [type] }]);
  }
}

function renderGallery() {
  return render(
    <GalleryScreen
      requestedSeasonId={seasonId}
      currentSeasonId={seasonId}
      admin={false}
      onSignedOut={vi.fn()}
    />,
  );
}

test.each([false, true])(
  'the author can withdraw an archived moment with admin access %s',
  async (admin) => {
    const route = `${readPath}/${itemId}/withdraw`;
    let withdrawn = false;
    const { sent } = fakeServer({
      '/api/seasons/gallery': seasonList,
      [readPath]: () => ({
        body: {
          ...view,
          items: withdrawn
            ? []
            : view.items.map((item) => ({ ...item, reactions: [], canWithdraw: true })),
        },
      }),
      [`POST ${route}`]: () => {
        withdrawn = true;
        return json(204);
      },
    });
    render(
      <GalleryScreen
        requestedSeasonId={seasonId}
        currentSeasonId={null}
        admin={admin}
        onSignedOut={vi.fn()}
      />,
    );
    const card = await screen.findByRole('button', { name: 'Открыть момент: Первый момент' });
    expect(screen.queryByRole('button', { name: 'Добавить момент' })).not.toBeInTheDocument();
    fireEvent.click(card);
    const details = screen.getByRole('dialog', { name: 'Просмотр момента' });
    const hide = within(details).queryByRole('button', { name: 'Скрыть момент' });
    if (admin) {
      expect(hide).toBeInTheDocument();
    } else {
      expect(hide).not.toBeInTheDocument();
    }
    fireEvent.click(within(details).getByRole('button', { name: 'Отозвать публикацию' }));
    const confirmation = screen.getByRole('alertdialog', { name: 'Отозвать публикацию кадра?' });
    expect(within(confirmation).getByText(/останется в дневном лимите/)).toBeInTheDocument();
    expect(sent('POST', route)).toHaveLength(0);
    fireEvent.click(within(confirmation).getByRole('button', { name: 'Отозвать публикацию' }));

    expect(await screen.findByText('Публикация отозвана.')).toBeInTheDocument();
    expect(sent('POST', route)).toHaveLength(1);
    const body = sent('POST', route)[0]?.body as { commandId: string };
    expect(body.commandId).toMatch(/^[\da-f-]+$/i);
    expect(details).not.toBeInTheDocument();
    expect(card).not.toBeInTheDocument();
    await waitFor(() => {
      expect(sent('GET', readPath)).toHaveLength(2);
      expect(screen.getByRole('heading', { name: 'Галерея моментов' })).toHaveFocus();
    });
  },
);

test('a live approval reversal removes the moment from a spectator and restores focus', async () => {
  let approved = true;
  fakeServer({
    '/api/seasons/gallery': seasonList,
    [readPath]: () => ({
      body: {
        ...view,
        items: approved
          ? view.items.map((item) => ({ ...item, reactions: [], canWithdraw: undefined }))
          : [],
      },
    }),
  });
  renderGallery();
  const card = await screen.findByRole('button', { name: 'Открыть момент: Первый момент' });
  fireEvent.click(card);
  const details = screen.getByRole('dialog', { name: 'Просмотр момента' });
  expect(
    within(details).queryByRole('button', { name: 'Отозвать публикацию' }),
  ).not.toBeInTheDocument();
  approved = false;
  notify('command-undone');
  await waitFor(() => {
    expect(details).not.toBeInTheDocument();
    expect(card).not.toBeInTheDocument();
    expect(screen.getByRole('heading', { name: 'Галерея моментов' })).toHaveFocus();
  });
});

test('the author can withdraw hidden proof consent after loading another gallery page', async () => {
  const hiddenId = '3d56eb2d-b52a-4a3b-81f5-236f520112b7';
  const route = `${readPath}/${hiddenId}/withdraw`;
  let withdrawn = false;
  const { sent } = fakeServer({
    '/api/seasons/gallery': seasonList,
    [readPath]: ({ query }) => ({
      body: query.has('before')
        ? { ...view, items: [] }
        : {
            ...view,
            nextBefore: itemId,
            hiddenProofPublications: withdrawn
              ? undefined
              : [{ id: hiddenId, createdAt: '2026-11-20T10:00:00Z' }],
          },
    }),
    [`POST ${route}`]: () => {
      withdrawn = true;
      return json(204);
    },
  });
  renderGallery();
  const hidden = await screen.findByRole('region', { name: 'Твои скрытые кадры' });
  expect(within(hidden).queryByRole('img')).not.toBeInTheDocument();
  fireEvent.click(screen.getByRole('button', { name: 'Показать ещё' }));
  await waitFor(() => {
    expect(sent('GET', readPath).at(-1)?.query.get('before')).toBe(itemId);
    expect(screen.queryByRole('button', { name: 'Показать ещё' })).not.toBeInTheDocument();
  });
  fireEvent.click(within(hidden).getByRole('button', { name: 'Отозвать публикацию' }));
  const confirmation = screen.getByRole('alertdialog', { name: 'Отозвать публикацию кадра?' });
  fireEvent.click(within(confirmation).getByRole('button', { name: 'Отозвать публикацию' }));
  expect(await screen.findByText('Публикация отозвана.')).toBeInTheDocument();
  expect(sent('POST', route)).toHaveLength(1);
  expect(hidden).not.toBeInTheDocument();
  await waitFor(() => {
    expect(screen.getByRole('heading', { name: 'Галерея моментов' })).toHaveFocus();
  });
});

test('live reactions leave a closed gallery alone and read only its open moment at most every two seconds', async () => {
  const { sent } = fakeServer({
    '/api/seasons/gallery': seasonList,
    [readPath]: view,
  });
  renderGallery();
  const card = await screen.findByRole('button', { name: 'Открыть момент: Первый момент' });
  const reads = () => sent('GET', readPath);

  vi.useFakeTimers();
  try {
    await act(async () => {
      notify('gallery-reaction-set');
      await vi.advanceTimersByTimeAsync(2_000);
    });
    expect(reads()).toHaveLength(1);

    await act(async () => {
      fireEvent.click(card);
      await vi.advanceTimersByTimeAsync(0);
    });
    expect(reads()).toHaveLength(2);
    expect(reads()[1]?.query.get('itemId')).toBe(itemId);

    await act(async () => {
      notify('gallery-reaction-set');
      await vi.advanceTimersByTimeAsync(1);
    });
    expect(reads()).toHaveLength(3);
    await act(async () => {
      for (let index = 0; index < 10; index++) {
        notify('gallery-reaction-set');
        await vi.advanceTimersByTimeAsync(100);
      }
    });
    expect(reads()).toHaveLength(3);
    await act(async () => {
      await vi.advanceTimersByTimeAsync(1_000);
    });
    expect(reads()).toHaveLength(4);
    expect(
      reads()
        .slice(1)
        .map((call) => call.query.get('itemId')),
    ).toEqual([itemId, itemId, itemId]);
  } finally {
    vi.useRealTimers();
  }
});

test('a confirmed reaction survives a list snapshot that began before the write', async () => {
  let finishRead!: () => void;
  const previousSnapshot = new Promise<void>((resolve) => {
    finishRead = resolve;
  });
  let hold = false;
  const pending = vi.fn();
  fakeServer({
    '/api/seasons/gallery': seasonList,
    [readPath]: async ({ query }: Call) => {
      if (hold && !query.has('itemId')) {
        pending();
        await previousSnapshot;
      }
      return { body: view };
    },
    [`PUT ${readPath}/${itemId}/reactions`]: () => json(204),
  });
  renderGallery();
  fireEvent.click(await screen.findByRole('button', { name: 'Открыть момент: Первый момент' }));
  const reaction = await screen.findByRole('button', { name: 'Реакция 🔥' });
  hold = true;
  notify('gallery-moment-published');
  await waitFor(() => {
    expect(pending).toHaveBeenCalledOnce();
  });
  fireEvent.click(reaction);
  await waitFor(() => {
    expect(reaction).toHaveAttribute('aria-pressed', 'true');
  });
  await act(async () => {
    finishRead();
    await previousSnapshot;
  });
  expect(reaction).toHaveAttribute('aria-pressed', 'true');
  expect(reaction).toHaveAccessibleDescription('1 реакция');
});

test('a viewer reaction read stays newer than an earlier delayed list snapshot', async () => {
  let finishRead!: () => void;
  const previousSnapshot = new Promise<void>((resolve) => {
    finishRead = resolve;
  });
  let holdList = false;
  let remoteReaction = false;
  const pending = vi.fn();
  fakeServer({
    '/api/seasons/gallery': seasonList,
    [readPath]: async ({ query }: Call) => {
      if (query.has('itemId') && remoteReaction) {
        return {
          body: {
            ...view,
            items: view.items.map((item) => ({
              ...item,
              reactions: [{ emoji: '🔥', selected: false, count: 1 }],
            })),
          },
        };
      }
      if (holdList && !query.has('itemId')) {
        pending();
        await previousSnapshot;
      }
      return { body: view };
    },
  });
  renderGallery();
  fireEvent.click(await screen.findByRole('button', { name: 'Открыть момент: Первый момент' }));
  const reaction = await screen.findByRole('button', { name: 'Реакция 🔥' });
  expect(reaction).toHaveAccessibleDescription('0 реакций');

  holdList = true;
  notify('gallery-moment-published');
  await waitFor(() => {
    expect(pending).toHaveBeenCalledOnce();
  });
  remoteReaction = true;
  notify('gallery-reaction-set');
  await waitFor(() => {
    expect(reaction).toHaveAccessibleDescription('1 реакция');
  });
  await act(async () => {
    finishRead();
    await previousSnapshot;
  });
  expect(reaction).toHaveAccessibleDescription('1 реакция');
});

test('a temporary list refusal preserves the chosen screenshot, disabling the gallery closes the form', async () => {
  let listStatus = 200;
  const { sent } = fakeServer({
    '/api/seasons/gallery': seasonList,
    [readPath]: () => ({
      status: listStatus,
      body:
        listStatus === 200
          ? { ...view, canPublish: true }
          : { code: listStatus === 409 ? 'feature.disabled' : 'rate.limit' },
    }),
  });
  renderGallery();
  fireEvent.click(await screen.findByRole('button', { name: 'Добавить момент' }));
  const form = screen.getByRole('dialog', { name: 'Добавить момент' });
  const file = new File(['test'], 'moment.png', { type: 'image/png' });
  fireEvent.change(within(form).getByLabelText('Выбрать скриншот'), { target: { files: [file] } });
  expect(within(form).getByText('moment.png')).toBeInTheDocument();

  listStatus = 429;
  notify('gallery-moment-published');
  await waitFor(() => {
    expect(sent('GET', readPath)).toHaveLength(2);
  });
  await act(async () => Promise.resolve());
  expect(within(form).getByText('moment.png')).toBeInTheDocument();

  listStatus = 409;
  notify('ruleset-changed');
  await waitFor(() => {
    expect(form).not.toBeInTheDocument();
  });
});
