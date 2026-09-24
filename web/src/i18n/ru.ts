// The single dictionary of interface texts. Terms come from docs/GLOSSARY.md.

const hours = (value: number) => `${value.toLocaleString('ru-RU')} ч`;

const rejection = {
  'turn.wrongPhase': 'Это действие сейчас недоступно: обновите страницу.',
  'turn.choicePending': 'Сначала сделайте выбор.',
  'turn.noPendingChoice': 'Этот выбор уже сделан или снят. Обновите страницу.',
  'turn.unknownOption': 'Такого варианта нет. Обновите страницу.',
  'roll.noAvailableGames': 'Нет доступных игр для ролла. Сообщите админу.',
  'roll.gameNotOffered': 'Эта игра вам сейчас не предложена. Обновите страницу.',
  'run.hoursRequired': 'У игры нет данных о длине: укажите оценку часов.',
  'run.invalidHours': 'Часы должны быть больше нуля.',
  'player.unknown': 'Вы не участвуете в этом сезоне.',
  'season.notCreated': 'Сезон ещё не создан.',
  'season.mismatch': 'Действие отправлено не в тот сезон. Обновите страницу.',
  'command.idReused': 'Действие уже выполнялось. Обновите страницу.',
  'ruleset.invalid': 'В правилах есть ошибки: исправьте отмеченные поля.',
  'ruleset.unchanged': 'Правила не изменились: сохранять нечего.',
  'ruleset.versionConflict':
    'Правила уже изменил кто-то другой. Обновите страницу и повторите правку.',
  unknown: 'Действие отклонено. Попробуйте ещё раз.',
} as const;

export const ru = {
  app: {
    title: 'Игровой ивент',
    loading: 'Загрузка…',
    loadError: 'Не удалось загрузить данные. Проверьте соединение и обновите страницу.',
    noSeason: 'Сезонов пока нет.',
  },
  login: {
    title: 'Вход',
    login: 'Логин',
    password: 'Пароль',
    submit: 'Войти',
    failed: 'Неверный логин или пароль.',
    throttled: 'Слишком много попыток. Подождите и попробуйте снова.',
    logout: 'Выйти',
  },
  turn: {
    title: 'Ваш ход',
    roll: 'Крутить колесо',
    offered: (title: string, gameHours: number | null) =>
      gameHours == null ? `Выпала игра: ${title}` : `Выпала игра: ${title} (${hours(gameHours)})`,
    start: 'Начать',
    alreadyPlayed: 'Уже проходил',
    alreadyPlayedGame: (title: string) => `Уже проходил: ${title}`,
    choose: 'Выберите одну из выпавших игр',
    option: (title: string, gameHours: number | null) =>
      gameHours == null ? title : `${title} (${hours(gameHours)})`,
    playing: (title: string) => `Сейчас играете: ${title}`,
    complete: 'Завершить',
    difficulty: 'Сложность',
    hours: 'Часы (оценка)',
    hoursHint: 'У игры нет данных о длине: укажите оценку.',
    hoursInvalid: 'Укажите число часов больше нуля.',
    lastDice: (title: string, dice: number[], total: number) =>
      `Кубы за прохождение (${title}): ${dice.join(' + ')} — итого ${total.toLocaleString('ru-RU')}`,
    spectator: 'Вы смотрите сезон как зритель.',
  },
  difficulty: {
    easy: 'Лёгкая',
    normal: 'Нормальная',
    hard: 'Сложная',
    extreme: 'Выше сложной',
  },
  map: {
    title: 'Карта',
    start: 'Старт',
    finish: 'Финиш',
    cell: '·',
  },
  leaderboard: {
    title: 'Лидерборд',
    row: (name: string, points: number) => `${name}: ${points.toLocaleString('ru-RU')} очк.`,
  },
  rejection: rejection as Readonly<Record<string, string>> & typeof rejection,
} as const;
