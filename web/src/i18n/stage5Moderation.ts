const gameNotAdded = 'Игра не добавлена в пул. Проверь её в разделе «Пул и категории».';
const poolWarnings = new Map([
  ['pool.duplicate', 'Игра уже есть в пуле.'],
  [
    'pool.similar',
    'Игра не добавлена в пул. В пуле уже есть похожее название. Проверь его в разделе «Пул и категории».',
  ],
]);
const filteredEmpty = 'Нет предложений с таким статусом';

export const stage5Moderation = {
  irlProofs: {
    disabledTitle: 'IRL-ивенты выключены',
    disabledText: 'Очередь появится после включения ивентов в правилах этого сезона.',
    emptyTitle: 'Нет IRL-пруфов на проверке',
    emptyText: 'Здесь появятся фото и ссылки от игроков, выполнивших IRL-задания.',
    openVideo: 'Открыть видео',
    mediaUnavailable: 'Не удалось показать приложенные фото или ссылку.',
    rejectTitle: (name: string) => `Отклонить IRL-пруф «${name}»?`,
    rejectConsequence: 'Игрок получит комментарий и сможет отправить пруф заново.',
    approved: 'IRL-пруф одобрен',
    rejected: 'IRL-пруф отклонён',
  },
  suggestions: {
    disabledTitle: 'Предложка выключена',
    disabledText: 'Включи предложку в правилах этого сезона, чтобы увидеть предложения.',
    emptyTitle: 'В этом сезоне ещё нет предложений',
    emptyText: 'Здесь появятся идеи игроков для пула, ивентов и предметов.',
    filteredEmpty: {
      pending: 'Нет предложений на рассмотрении',
      approved: filteredEmpty,
      rejected: filteredEmpty,
      all: filteredEmpty,
    },
    statusFilter: 'Статус',
    statuses: {
      all: 'Все',
      pending: 'На рассмотрении',
      approved: 'Одобрено',
      rejected: 'Отклонено',
    },
    kinds: { game: 'Игра', event: 'Ивент', item: 'Предмет' },
    unknownAuthor: 'Имя игрока недоступно',
    completionCondition: 'Условие прохождения',
    steam: 'Страница игры в Steam',
    rejectTitle: 'Отклонить предложение?',
    rejectConsequence: 'Автор увидит решение и комментарий.',
    approved: 'Предложение одобрено',
    rejected: 'Предложение отклонено',
    poolWarning: (code: string) => poolWarnings.get(code) ?? gameNotAdded,
  },
};
