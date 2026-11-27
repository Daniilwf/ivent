import { act, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { App } from '../App';
import { api, type Schemas } from '../api/client';
import { ru } from '../i18n/ru';
import { adminUser, fakeServer, json, seasonId } from '../test/fakeServer';
import { PlayerPreviewScreen } from './PlayerPreviewScreen';
import { AdminScreen } from './AdminScreen';
import { keepSeason, keptSeason } from './pickedSeason';
import {
  otherPreviewPlayerId,
  playerPreviewFixture,
  previewApiPath,
  previewPath,
  previewPlayerId,
  required,
} from './playerPreviewFixtures';

const listeners = new Map<string, Set<() => void>>();
vi.mock('../api/realtime', () => ({
  watchSeason: (id: string, changed: (updates: unknown[]) => void) => {
    const notify = () => {
      changed([]);
    };
    const watched = listeners.get(id) ?? new Set<() => void>();
    listeners.set(id, watched);
    watched.add(notify);
    notify();
    return () => {
      watched.delete(notify);
    };
  },
}));

function direct() {
  return render(
    <PlayerPreviewScreen
      seasonId={seasonId}
      playerId={previewPlayerId}
      onSignedOut={() => undefined}
    />,
  );
}

function withMaterials(): Schemas['PlayerPreviewView'] {
  const value = playerPreviewFixture();
  value.season.economy = { items: true, shop: false, bets: false };
  const me = required(value.season.me);
  me.eventsEnabled = true;
  const file = {
    id: 'f0000000-0000-0000-0000-000000000001',
    url: `${previewApiPath()}/files/f0000000-0000-0000-0000-000000000001`,
    thumbnailUrl: `${previewApiPath()}/files/f0000000-0000-0000-0000-000000000001/thumbnail`,
  };
  value.season.players.push({
    ...required(value.season.players[0]),
    id: otherPreviewPlayerId,
    name: 'Петя',
  });
  me.lastCompleted = {
    id: '70000000-0000-0000-0000-000000000001',
    game: {
      id: '60000000-0000-0000-0000-000000000002',
      title: 'Hollow Knight',
      hours: 20,
      cover: null,
    },
    difficulty: 'normal',
    dice: [{ value: 6, sides: 6 }],
    challengeDice: [],
    total: 6,
    review: null,
    status: 'rejected',
    proof: {
      status: 'rejected',
      links: ['https://example.com/run-proof'],
      note: 'Заметка выбранного игрока',
      comment: 'Добавь финальные титры',
      witnessId: otherPreviewPlayerId,
      files: [file],
    },
  };
  me.manualEffects = [
    {
      id: '80000000-0000-0000-0000-000000000001',
      drawEvent: 'bad',
      source: 'event',
      objectId: 'karaoke',
      name: 'Караоке',
      description: 'Спой песню',
      irl: true,
      irlProof: {
        status: 'pending',
        files: [
          {
            id: 'f0000000-0000-0000-0000-000000000002',
            url: `${previewApiPath()}/files/f0000000-0000-0000-0000-000000000002`,
            thumbnailUrl: `${previewApiPath()}/files/f0000000-0000-0000-0000-000000000002/thumbnail`,
          },
        ],
        link: 'https://example.com/irl-proof',
        comment: null,
        submittedAt: '2026-11-27T01:00:00Z',
      },
    },
  ];
  value.economy = {
    coins: 12,
    inventoryLimit: 5,
    shop: null,
    bets: null,
    serverNow: '2026-11-27T01:00:00Z',
    items: [
      {
        instanceId: '90000000-0000-0000-0000-000000000001',
        objectId: 'potion',
        kind: 'item',
        name: 'Зелье',
        description: 'Текст предмета выбранного игрока',
        rarity: 'common',
        window: 'beforeRoll',
        hostile: false,
        usesLeft: 2,
        runsLeft: null,
        expiresAt: null,
        fromPlayer: null,
        usable: true,
        refusal: null,
        choosesTarget: false,
        targets: [],
        steps: [],
      },
    ],
  };
  return value;
}

describe('Player preview', () => {
  afterEach(() => {
    globalThis.history.pushState(null, '', '/');
    listeners.clear();
  });

  it('opens the selected private game view for the real admin without mutation or account controls', async () => {
    globalThis.history.pushState(null, '', previewPath());
    const value = playerPreviewFixture();
    const server = fakeServer({
      'GET /api/auth/me': adminUser,
      'GET /api/status': { playerPreview: true },
      'GET /api/seasons/current': { id: seasonId },
      'GET /api/seasons/*/navigation': { id: seasonId, pollsEnabled: false },
      [previewApiPath()]: value,
    });
    render(<App />);

    expect(
      await screen.findByRole('heading', { name: 'Просмотр глазами игрока', level: 1 }),
    ).toBeVisible();
    expect(screen.getByText('Только просмотр')).toBeVisible();
    expect(
      await screen.findByText('Вася', { selector: '[data-testid="preview-player-name"]' }),
    ).toBeVisible();
    expect(screen.getByText('Осенний сезон')).toBeVisible();
    const context = screen.getByRole('region', { name: ru.playerPreview.context });
    expect(within(context).getByTestId('preview-player-name')).toHaveTextContent('Вася');
    expect(within(context).getByTestId('preview-season-name')).toHaveTextContent('Осенний сезон');
    expect(within(context).getByText(ru.playerPreview.readOnly)).toBeVisible();
    expect(within(context).getByRole('link', { name: ru.playerPreview.exit })).toBeVisible();
    expect(screen.getAllByRole('link', { name: ru.playerPreview.exit })).toHaveLength(1);
    expect(screen.getByRole('heading', { name: 'Celeste' })).toBeVisible();
    expect(screen.getByRole('link', { name: 'Вернуться в админку' })).toHaveAttribute(
      'href',
      `/admin/players?season=${seasonId}`,
    );
    expect(screen.queryByTestId('user-menu')).toBeNull();
    expect(screen.queryByRole('form')).toBeNull();
    expect(
      screen.queryByRole('button', { name: /Начать|Дропнуть|Бросить|Применить|Загрузить/ }),
    ).toBeNull();
    expect(server.sent('GET', previewApiPath())).toHaveLength(1);
    expect(server.calls.every((call) => call.method === 'GET')).toBe(true);
  });

  it.each(['player', 'spectator'] as const)(
    'does not fetch a private preview for the %s role',
    async (role) => {
      globalThis.history.pushState(null, '', previewPath());
      const server = fakeServer({
        'GET /api/auth/me': { ...adminUser, role },
        'GET /api/status': {},
        'GET /api/seasons/current': { id: seasonId },
        [previewApiPath()]: playerPreviewFixture(),
      });
      render(<App />);
      expect(await screen.findByRole('heading', { name: ru.feed.pageNotFoundTitle })).toBeVisible();
      expect(server.sent('GET', previewApiPath())).toHaveLength(0);
    },
  );

  it('shows actual rejected and pending materials and usable inventory without enabling any game action', async () => {
    const value = withMaterials();
    const server = fakeServer({ [previewApiPath()]: value });
    const { container } = direct();
    expect(await screen.findByRole('heading', { name: 'Hollow Knight' })).toBeVisible();
    expect(screen.getByText(ru.proof.status.rejected)).toBeVisible();
    expect(screen.getByText(ru.proof.status.pending)).toBeVisible();
    expect(screen.getByText(/Заметка выбранного игрока/)).toBeVisible();
    expect(screen.getByText(/Добавь финальные титры/)).toBeVisible();
    expect(screen.getByText('Свидетель: Петя')).toBeVisible();
    expect(screen.getByRole('link', { name: /run-proof/ })).toHaveAttribute(
      'href',
      'https://example.com/run-proof',
    );
    expect(screen.getByRole('link', { name: /irl-proof/ })).toHaveAttribute(
      'href',
      'https://example.com/irl-proof',
    );
    expect(screen.getAllByRole('img', { name: ru.proof.shotAlt(1) })).toHaveLength(2);
    for (const image of screen.getAllByRole('img', { name: ru.proof.shotAlt(1) }))
      expect(image.getAttribute('src')).toContain(`${previewApiPath()}/files/`);
    expect(screen.getByRole('heading', { name: 'Зелье' })).toBeVisible();
    expect(container.querySelectorAll('form,input,textarea,select')).toHaveLength(0);
    expect(screen.queryByRole('button', { name: ru.inventory.use })).toBeNull();
    expect(screen.queryByRole('button', { name: ru.proof.submit })).toBeNull();
    expect(screen.queryByRole('button', { name: ru.effects.applied })).toBeNull();
    const read = server.fetch.mock.calls.find(
      ([request]) => new URL(request.url).pathname === previewApiPath(),
    )?.[0];
    expect(read?.headers.get('X-GameEvent-Player-Preview')).toBe('true');
    expect(server.calls.every((call) => call.method === 'GET')).toBe(true);
  });

  it('renders a waiting effect choice as text without radio buttons or answer forms', async () => {
    const value = playerPreviewFixture();
    required(value.season.me).offer = null;
    required(value.season.me).choice = {
      id: 'c',
      kind: 'effect',
      prompt: 'Как поступить?',
      name: 'Выбор судьбы',
      description: 'Исходный текст',
      options: [
        { id: 'yes', label: 'Помочь', game: null },
        { id: 'no', label: 'Пройти мимо', game: null },
      ],
    };
    fakeServer({ [previewApiPath()]: value });
    const { container } = direct();
    expect(await screen.findByText('Как поступить?')).toBeVisible();
    expect(screen.getByText('Помочь')).toBeVisible();
    expect(screen.getByText('Пройти мимо')).toBeVisible();
    expect(container.querySelectorAll('form,input,textarea,select')).toHaveLength(0);
  });

  it('keeps Exit available while loading and retries a read error without a mutation', async () => {
    let attempts = 0;
    const server = fakeServer({
      [previewApiPath()]: () =>
        ++attempts === 1 ? { status: 503 } : { body: playerPreviewFixture() },
    });
    direct();
    expect(screen.getByRole('link', { name: ru.playerPreview.exit })).toBeVisible();
    expect(await screen.findByRole('heading', { name: ru.playerPreview.failed })).toBeVisible();
    await userEvent.click(screen.getByRole('button', { name: ru.ui.retry }));
    expect(await screen.findByRole('heading', { name: 'Celeste' })).toBeVisible();
    expect(server.sent('GET', previewApiPath())).toHaveLength(2);
    expect(server.calls.every((call) => call.method === 'GET')).toBe(true);
  });

  it('shows an unavailable scope without retaining private data', async () => {
    fakeServer({ [previewApiPath()]: () => ({ status: 404 }) });
    direct();
    expect(
      await screen.findByRole('heading', { name: ru.playerPreview.unavailable }),
    ).toBeVisible();
    expect(screen.queryByTestId('preview-player-name')).toBeNull();
    expect(screen.queryByTestId('player-preview-content')).toBeNull();
  });

  it('aborts the previous target and never displays its late reply after switching scope', async () => {
    let finish: (() => void) | undefined;
    const delayed = new Promise<Response>((resolve) => {
      finish = () => {
        resolve(json(200, playerPreviewFixture()));
      };
    });
    const other = playerPreviewFixture();
    other.playerId = otherPreviewPlayerId;
    other.playerName = 'Лиса';
    required(other.season.me).playerId = otherPreviewPlayerId;
    required(other.season.players[0]).id = otherPreviewPlayerId;
    required(other.season.players[0]).name = 'Лиса';
    const server = fakeServer({
      [previewApiPath()]: () => delayed,
      [previewApiPath(otherPreviewPlayerId)]: other,
    });
    const { rerender } = direct();
    await waitFor(() => {
      expect(server.sent('GET', previewApiPath())).toHaveLength(1);
    });
    const previousRequest = required(
      server.fetch.mock.calls.find(
        ([request]) => new URL(request.url).pathname === previewApiPath(),
      ),
    )[0];
    rerender(
      <PlayerPreviewScreen
        seasonId={seasonId}
        playerId={otherPreviewPlayerId}
        onSignedOut={() => undefined}
      />,
    );
    expect(
      await screen.findByText('Лиса', { selector: '[data-testid="preview-player-name"]' }),
    ).toBeVisible();
    expect(previousRequest.signal.aborted).toBe(true);
    await act(async () => {
      finish?.();
      await delayed;
    });
    expect(
      screen.queryByText('Вася', { selector: '[data-testid="preview-player-name"]' }),
    ).toBeNull();
    expect(
      screen.getByText('Лиса', { selector: '[data-testid="preview-player-name"]' }),
    ).toBeVisible();
  });

  it('aborts an unfinished read when the administrator exits', async () => {
    const server = fakeServer({ [previewApiPath()]: () => new Promise<Response>(() => undefined) });
    const { unmount } = direct();
    await waitFor(() => {
      expect(server.sent('GET', previewApiPath())).toHaveLength(1);
    });
    const pending = required(server.fetch.mock.calls[0])[0];
    unmount();
    expect(pending.signal.aborted).toBe(true);
  });

  it('refreshes the same scoped snapshot after a public season update', async () => {
    let name = 'Вася';
    const server = fakeServer({
      [previewApiPath()]: () => ({ body: playerPreviewFixture({ playerName: name }) }),
    });
    direct();
    expect(await screen.findByTestId('preview-player-name')).toHaveTextContent('Вася');
    name = 'Вася после обновления';
    act(() => {
      for (const notify of listeners.get(seasonId) ?? []) notify();
    });
    await waitFor(() => {
      expect(screen.getByTestId('preview-player-name')).toHaveTextContent(name);
    });
    expect(server.sent('GET', previewApiPath())).toHaveLength(2);
    expect([...listeners.keys()]).toEqual([seasonId]);
  });

  it('clears private data when a live read loses permission', async () => {
    let allowed = true;
    fakeServer({
      [previewApiPath()]: () => (allowed ? { body: playerPreviewFixture() } : { status: 403 }),
    });
    direct();
    await screen.findByRole('heading', { name: 'Celeste' });
    allowed = false;
    act(() => {
      for (const notify of listeners.get(seasonId) ?? []) notify();
    });
    expect(
      await screen.findByRole('heading', { name: ru.playerPreview.unavailable }),
    ).toBeVisible();
    expect(screen.queryByTestId('player-preview-content')).toBeNull();
    expect(screen.queryByTestId('preview-player-name')).toBeNull();
  });

  it('rejects a response for another target instead of showing its private data', async () => {
    fakeServer({
      [previewApiPath()]: playerPreviewFixture({
        playerId: otherPreviewPlayerId,
        playerName: 'Чужой игрок',
      }),
    });
    direct();
    expect(
      await screen.findByRole('heading', { name: ru.playerPreview.unavailable }),
    ).toBeVisible();
    expect(screen.queryByText('Чужой игрок')).toBeNull();
    expect(screen.queryByTestId('player-preview-content')).toBeNull();
  });

  it('keeps finish, deadline and dense game state visible without adding an action', async () => {
    const value = withMaterials();
    const me = required(value.season.me);
    value.season.status = 'finished';
    value.season.deadline = '2026-11-25T18:00:00Z';
    required(value.season.players[0]).cellId = 'finish';
    required(value.season.players[0]).finishOrder = 1;
    required(value.season.leaderboard[0]).isFirst = true;
    me.phase = 'idle';
    me.offer = null;
    me.finish = { order: 1, frozen: true };
    required(me.lastCompleted).status = 'completed';
    required(required(me.lastCompleted).proof).status = 'approved';
    required(value.economy).items = Array.from({ length: 16 }, (_, index) => ({
      ...required(required(required(value.economy).items)[0]),
      instanceId: `item-${String(index)}`,
      name: `Предмет с длинным названием ${String(index + 1)}`,
    }));
    fakeServer({ [previewApiPath()]: value });
    const { container } = direct();
    expect(await screen.findByTestId('my-finish')).toContainHTML(ru.turn.myFinish.firstFrozen);
    expect(screen.getByText(required(ru.seasonStatus.finished))).toBeVisible();
    expect(screen.getByText(/^Дедлайн:/)).toHaveTextContent('МСК');
    expect(screen.getAllByRole('heading', { name: /Предмет с длинным названием/ })).toHaveLength(
      16,
    );
    expect(container.querySelectorAll('form,input,textarea,select')).toHaveLength(0);
  });

  it('shows the remaining branch steps and distinct destination cells and zones without a choice action', async () => {
    const value = playerPreviewFixture();
    value.season.mapMode = 'graph';
    value.season.cells = [
      { id: 'start', type: 'start' },
      { id: 'fork', type: 'fork' },
      { id: 'left1', type: 'empty', zone: 'forest' },
      { id: 'left2', type: 'checkpoint', zone: 'forest' },
      { id: 'right1', type: 'empty', zone: 'city' },
      { id: 'right2', type: 'pointsBonus', amount: 5, zone: 'city' },
      { id: 'finish', type: 'finish' },
    ];
    value.season.edges = [
      ['start', 'fork'],
      ['fork', 'left1'],
      ['fork', 'right1'],
      ['left1', 'left2'],
      ['right1', 'right2'],
      ['left2', 'finish'],
      ['right2', 'finish'],
    ].map(([from, to]) => ({
      from: required(from),
      to: required(to),
      isDefaultForward: true,
      isPrimaryBackward: true,
    }));
    value.season.zones = [
      { id: 'forest', name: 'Лес' },
      { id: 'city', name: 'Город' },
    ];
    required(value.season.me).offer = null;
    required(value.season.me).choice = {
      id: 'branch',
      kind: 'branch',
      steps: 2,
      options: [
        { id: 'left1', game: null },
        { id: 'right1', game: null },
      ],
    };
    fakeServer({ [previewApiPath()]: value });
    const { container } = direct();
    const choice = await screen.findByRole('region', { name: ru.map.branch.title });
    expect(choice).toHaveTextContent(ru.map.branch.lead(2));
    expect(choice).toHaveTextContent(/чекпоинт/);
    expect(choice).toHaveTextContent(/бонус/);
    expect(choice).toHaveTextContent('Лес');
    expect(choice).toHaveTextContent('Город');
    expect(choice.querySelectorAll('button,input,select')).toHaveLength(0);
    expect(container.querySelectorAll('form')).toHaveLength(0);
  });

  it('keeps inventory effects and waiting events visible without claiming there are no active effects', async () => {
    const value = withMaterials();
    required(value.season.me).manualEffects = [];
    required(value.season.me).waitingEvents = [
      {
        objectId: 'later',
        name: 'Поздний ивент',
        description: 'Сработает на будущем шаге',
        kind: 'good',
        waitingForRoll: true,
        waitingForThrow: false,
      },
    ];
    required(required(value.economy).items)[0] = {
      ...required(required(required(value.economy).items)[0]),
      kind: 'effect',
      name: 'Ускорение',
    };
    fakeServer({ [previewApiPath()]: value });
    direct();
    expect(await screen.findByRole('heading', { name: 'Ускорение' })).toBeVisible();
    expect(screen.getByText('Поздний ивент')).toBeVisible();
    expect(screen.queryByText('Активных эффектов нет.')).toBeNull();
  });

  it('shows current global events, own partner request and duel as saved information', async () => {
    const value = withMaterials();
    const me = required(value.season.me);
    me.phase = 'playing';
    me.offer = null;
    me.activeRun = {
      id: 'run',
      game: { id: 'game', title: 'Кооп-игра', hours: 8, isCoop: true },
      startedAt: '2026-11-27T01:00:00Z',
    };
    me.partnerRequest = {
      id: 'request',
      playerId: previewPlayerId,
      runId: 'run',
      createdAt: '2026-11-27T01:00:00Z',
      expiresAt: '2030-11-27T01:00:00Z',
      status: 'open',
      gameTitle: 'Кооп-игра',
    };
    me.activeDuels = [
      {
        id: 'duel',
        challengerId: previewPlayerId,
        opponentId: otherPreviewPlayerId,
        acceptedAt: '2026-11-27T01:00:00Z',
        expiresAt: '2030-11-27T01:00:00Z',
        status: 'active',
        winnerId: null,
        runId: null,
        coins: 0,
        end: null,
        endedAt: null,
      },
    ];
    value.season.globalEvents = [
      {
        id: 'event',
        objectId: 'long-night',
        name: 'Длинная ночь',
        description: 'Общий ивент сезона',
        status: 'active',
        startsAt: '2026-11-27T01:00:00Z',
        endsAt: '2030-11-27T01:00:00Z',
        goal: null,
      },
    ];
    fakeServer({ [previewApiPath()]: value });
    direct();
    expect(await screen.findByRole('heading', { name: 'Длинная ночь' })).toBeVisible();
    expect(screen.getByText('Общий ивент сезона')).toBeVisible();
    expect(screen.getByRole('heading', { name: ru.interactions.ownRequest })).toBeVisible();
    expect(screen.getByText('Вася и Петя')).toBeVisible();
    expect(screen.queryByRole('button', { name: ru.interactions.close })).toBeNull();
    expect(screen.queryByRole('button', { name: ru.turn.drop })).toBeNull();
  });

  it('does not change the global API client or expose another user to a normal admin read', async () => {
    const server = fakeServer({
      [previewApiPath()]: playerPreviewFixture(),
      'GET /api/auth/me': adminUser,
    });
    direct();
    await screen.findByRole('heading', { name: 'Celeste' });
    const own = await api.GET('/api/auth/me');
    expect(own.data).toEqual(adminUser);
    const request = required(
      server.fetch.mock.calls.find(([call]) => new URL(call.url).pathname === '/api/auth/me'),
    )[0];
    expect(request.headers.has('X-GameEvent-Player-Preview')).toBe(false);
  });

  it.each([false, true])(
    'shows the participant entry only when the feature is enabled (%s)',
    async (enabled) => {
      const value = playerPreviewFixture();
      const player: Schemas['AdminPlayerView'] = {
        id: previewPlayerId,
        userId: 'u',
        name: 'Вася',
        cellId: 'start',
        points: 7,
        coins: 12,
        resources: {},
        phase: 'rolling',
        playing: false,
        isInactive: false,
        lastActionAt: null,
        inactiveHint: false,
        choosingBranch: false,
        eventQuestion: false,
      };
      fakeServer({
        'GET /api/status': { playerPreview: enabled },
        'GET /api/admin/seasons/*/proofs': [],
        'GET /api/seasons/*/navigation': { id: seasonId, pollsEnabled: false },
        'GET /api/admin/seasons/*/players': [player],
        'GET /api/seasons/*': value.season,
      });
      render(<AdminScreen section="players" currentSeasonId={seasonId} user={adminUser} />);
      await screen.findByTestId(`player-${previewPlayerId}`);
      const link = screen.queryByRole('link', { name: ru.playerPreview.entry });
      if (enabled) expect(link).toHaveAttribute('href', previewPath());
      else expect(link).toBeNull();
    },
  );

  it('returns to the preview season when another tab picked a different one without overwriting that pick', async () => {
    const otherSeasonId = '5ea50000-0000-0000-0000-000000000002';
    const value = playerPreviewFixture();
    const other = {
      ...value.season,
      id: otherSeasonId,
      name: 'Независимый архив',
      status: 'draft' as const,
      me: null,
    };
    globalThis.history.pushState(null, '', previewPath());
    const server = fakeServer({
      'GET /api/auth/me': adminUser,
      'GET /api/status': { playerPreview: true },
      'GET /api/seasons/current': { id: seasonId },
      'GET /api/seasons': [value.season, other],
      [previewApiPath()]: value,
      'GET /api/admin/seasons/*/proofs': [],
      'GET /api/admin/seasons/*/players': [],
      'GET /api/seasons/*/navigation': { id: seasonId, pollsEnabled: false },
      [`GET /api/seasons/${seasonId}`]: value.season,
      [`GET /api/seasons/${otherSeasonId}`]: other,
    });
    render(<App />);
    await screen.findByRole('heading', { name: 'Celeste' });
    // A different tab selected its own season while the private snapshot was open.
    keepSeason(otherSeasonId);
    await userEvent.click(screen.getByRole('link', { name: ru.playerPreview.exit }));
    expect(await screen.findByTestId('admin-season-name')).toHaveTextContent(
      'Сезон: Осенний сезон (идёт)',
    );
    expect(server.sent('GET', `/api/admin/seasons/${seasonId}/players`)).toHaveLength(1);
    expect(server.sent('GET', `/api/admin/seasons/${otherSeasonId}/players`)).toHaveLength(0);
    expect(keptSeason()).toBe(otherSeasonId);
    expect(screen.getByTestId('user-menu')).toHaveTextContent(adminUser.name);
  });

  it.each([true, false])(
    'restores an explicit valid return season on reload and ignores a malformed one (%s)',
    async (valid) => {
      const otherSeasonId = '5ea50000-0000-0000-0000-000000000002';
      const value = playerPreviewFixture();
      const other = {
        ...value.season,
        id: otherSeasonId,
        name: 'Независимый архив',
        status: 'draft' as const,
        me: null,
      };
      keepSeason(otherSeasonId);
      globalThis.history.pushState(
        null,
        '',
        `/admin/players?season=${valid ? seasonId.toUpperCase() : 'not-a-guid'}`,
      );
      const server = fakeServer({
        'GET /api/auth/me': adminUser,
        'GET /api/status': { playerPreview: true },
        'GET /api/seasons/current': { id: seasonId },
        'GET /api/seasons': [value.season, other],
        'GET /api/admin/seasons/*/proofs': [],
        'GET /api/admin/seasons/*/players': [],
        'GET /api/seasons/*/navigation': { id: seasonId, pollsEnabled: false },
        [`GET /api/seasons/${seasonId}`]: value.season,
        [`GET /api/seasons/${otherSeasonId}`]: other,
      });
      render(<App />);
      const expectedName = valid
        ? 'Сезон: Осенний сезон (идёт)'
        : 'Сезон: Независимый архив (готовится)';
      expect(await screen.findByTestId('admin-season-name')).toHaveTextContent(expectedName);
      expect(keptSeason()).toBe(otherSeasonId);
      expect(server.calls.some((call) => call.path.includes('not-a-guid'))).toBe(false);
    },
  );

  it('returns to the real admin page without changing the target into an identity', async () => {
    globalThis.history.pushState(null, '', previewPath());
    const server = fakeServer({
      'GET /api/auth/me': adminUser,
      'GET /api/status': { playerPreview: true },
      'GET /api/seasons/current': { id: seasonId },
      [previewApiPath()]: playerPreviewFixture(),
      'GET /api/admin/seasons/*/proofs': [],
      'GET /api/admin/seasons/*/players': [],
      'GET /api/seasons/*/navigation': { id: seasonId, pollsEnabled: false },
      'GET /api/seasons/*': playerPreviewFixture().season,
    });
    render(<App />);
    await screen.findByRole('heading', { name: 'Celeste' });
    await userEvent.click(screen.getByRole('link', { name: ru.playerPreview.exit }));
    expect(await screen.findByTestId('admin')).toBeVisible();
    expect(globalThis.location.pathname).toBe('/admin/players');
    expect(screen.getByTestId('user-menu')).toHaveTextContent(adminUser.name);
    expect(server.calls.filter((call) => call.method !== 'GET')).toHaveLength(0);
  });
});
