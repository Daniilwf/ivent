// The single dictionary of interface texts. Terms come from docs/GLOSSARY.md.

// игра / игры / игр
const games = (n: number) => {
  const tens = n % 100;
  const ones = n % 10;
  if (tens >= 11 && tens <= 14) return `${n} игр`;
  if (ones === 1) return `${n} игра`;
  if (ones >= 2 && ones <= 4) return `${n} игры`;
  return `${n} игр`;
};

// монетка / монетки / монеток
const coins = (n: number) => {
  const tens = n % 100;
  const ones = n % 10;
  const word =
    tens >= 11 && tens <= 14
      ? 'монеток'
      : ones === 1
        ? 'монетка'
        : ones >= 2 && ones <= 4
          ? 'монетки'
          : 'монеток';
  return `${n.toLocaleString('ru-RU')} ${word}`;
};

// прохождение / прохождения / прохождений
const runs = (n: number) => {
  const tens = n % 100;
  const ones = n % 10;
  const word =
    tens >= 11 && tens <= 14
      ? 'прохождений'
      : ones === 1
        ? 'прохождение'
        : ones >= 2 && ones <= 4
          ? 'прохождения'
          : 'прохождений';
  return `${n} ${word}`;
};

const effectSources = {
  paidReroll: '(за реролл)',
  drop: '(за дроп)',
  difficulty: '(за сложность)',
} as const;

type DropPenalty = {
  count: number;
  sides: number;
  affectsPoints: boolean;
  affectsPosition: boolean;
  badEvent: boolean;
};

type RerollPayment = 'freeThisRoll' | 'freeRerollResource' | 'coins' | 'badEvent' | 'freeMode';

const rejection = {
  'turn.wrongPhase': 'Это действие сейчас недоступно: обнови страницу.',
  'turn.choicePending': 'Сначала сделай выбор.',
  'turn.noPendingChoice': 'Этот выбор уже сделан или снят. Обнови страницу.',
  'turn.unknownOption': 'Такого варианта нет. Обнови страницу.',
  'roll.noAvailableGames': 'Нет доступных игр для ролла. Сообщи админу.',
  'roll.notEnoughCoins': 'Не хватает монеток на реролл.',
  'roll.tooManyUnchecked':
    'Слишком много прохождений ждут проверки админом. Новый ролл откроется, когда их станет меньше лимита сезона.',
  'roll.gameNotOffered': 'Эта игра тебе сейчас не предложена. Обнови страницу.',
  'proof.invalidLink': 'Ссылка должна начинаться с http:// или https://, ссылок не больше пяти.',
  'proof.empty': 'Добавь ссылку на пруф или выбери свидетеля.',
  'proof.witnessInvalid': 'Свидетелем может быть только другой игрок сезона.',
  'proof.alreadyReviewed': 'Пруф уже проверен.',
  'account.avatarNotYours': 'Аватаркой может быть только твоя загрузка.',
  'account.avatarFileUnknown': 'Картинка не найдена. Загрузи её ещё раз.',
  'proof.invalidFile': 'Можно прикрепить до 5 своих разных скринов.',
  'finish.nothingToRecalculate':
    'Пересчитывать нечего: у финишировавших уже бонусы по текущим правилам.',
  'proof.difficultyAboveClaimed': 'По пруфу нельзя поднять заявленную сложность.',
  'run.techRerollWindowClosed': 'Окно тех-реролла после ролла закрылось. Обратись к админу.',
  'run.reasonCommentRequired': 'Для причины «Другое» нужен комментарий.',
  'run.hoursRequired': 'У игры нет данных о длине: укажи оценку часов.',
  'run.invalidHours': 'Часы должны быть больше нуля.',
  'run.hoursSourceTooLong': 'Источник оценки не длиннее 300 символов.',
  'feature.disabled': 'Эта возможность выключена в правилах сезона.',
  'run.hoursSourceRequired': 'Укажи, откуда оценка часов: ссылку или короткую пометку.',
  'run.notYours': 'Это не твоё прохождение.',
  'run.notCompleted': 'Это прохождение ещё не завершено.',
  'run.nothingToChange': 'Значение уже такое: менять нечего.',
  'run.unknown': 'Прохождение не найдено. Обнови страницу.',
  'review.invalidRating': 'Оценка — от 1 до 10.',
  'review.tooLong': 'Отзыв слишком длинный: не больше 2000 символов.',
  'season.closed': 'Сезон закрыт для этого действия.',
  'effect.notPending': 'Этот эффект уже разрешён.',
  'account.mustChangePassword': 'Сначала смени временный пароль.',
  'account.currentPasswordWrong': 'Текущий пароль введён неверно.',
  'account.repeat':
    'Этот запрос уже выполнен. Если пароль ещё нужно сменить — отправь форму заново.',
  'account.stale': 'Аккаунт изменился, пока шёл запрос. Войди заново.',
  'account.roleInvalid': 'Неизвестная роль.',
  'account.passwordInvalid': 'Пароль — от 8 символов, не совпадает с логином и с текущим паролем.',
  'account.loginInvalid':
    'Логин — от 2 до 32 латинских букв, цифр, точек, дефисов и подчёркиваний.',
  'account.loginTaken': 'Такой логин уже занят.',
  'account.nameInvalid': 'Имя — от 1 до 64 символов.',
  'account.lastAdmin': 'Последний админ остаётся админом.',
  'account.deleted': 'Аккаунт уже удалён.',
  'account.notDeleted': 'Аккаунт не удалён.',
  'account.nothingToChange': 'Ничего не изменилось.',
  'undo.unknownCommand': 'Такого действия в логе сезона нет.',
  'undo.notUndoable': 'Создание сезона и сам откат не откатываются — сделай действие заново.',
  'undo.alreadyUndone': 'Это действие уже откатано.',
  'undo.dependents':
    'От этого действия зависят более поздние: сначала откати их или поправь вручную.',
  'undo.noHistory': 'Откат сейчас недоступен. Попробуй ещё раз.',
  'command.undone': 'Это действие откатили. Сделай его заново.',
  'season.deadlineInPast': 'Дедлайн должен быть в будущем.',
  'player.commentRequired': 'Нужен комментарий.',
  'player.commentTooLong': 'Комментарий слишком длинный: не больше 500 символов.',
  'effect.notYours': 'Это не твой эффект.',
  'effect.unknownOutcome': 'Неизвестный исход.',
  'season.deadlinePassed': 'Дедлайн прошёл: броски закрыты, пруфы принимаются.',
  'season.deadlineNotReached': 'Дедлайн ещё не наступил.',
  'season.proofsPending': 'Сначала проверь все пруфы: итоги — только после проверки.',
  'season.notActive': 'Сезон сейчас не идёт.',
  'player.finished': 'Позиция финишировавшего меняется только через его прохождения.',
  'player.unknown': 'Ты не участвуешь в этом сезоне.',
  'season.notCreated': 'Сезон ещё не создан.',
  'season.mismatch': 'Действие отправлено не в тот сезон. Обнови страницу.',
  'command.idReused': 'Действие уже выполнялось. Обнови страницу.',
  'command.invalid': 'Действие не удалось разобрать. Обнови страницу и повтори.',
  'ruleset.invalid': 'В правилах есть ошибки: исправь отмеченные поля.',
  'ruleset.unchanged': 'Правила не изменились: сохранять нечего.',
  'ruleset.versionConflict': 'Правила уже изменил кто-то другой. Обнови страницу и повтори правку.',
  'site.maintenance': 'Идёт обслуживание: сайт на минуту только для чтения. Повтори чуть позже.',
  'bugReport.invalid': 'Опиши, что случилось (до 4000 символов).',
  'bugReport.screenshotUnknown': 'Скриншот не найден. Отправь отчёт ещё раз.',
  unknown: 'Действие отклонено. Попробуй ещё раз.',
} as const;

// H5: the feed, the profile and the game page

// A Russian noun after a number: 1 очко, 2 очка, 5 очков
const plural = (n: number, one: string, few: string, many: string) => {
  const tens = Math.abs(n) % 100;
  const ones = Math.abs(n) % 10;
  if (tens >= 11 && tens <= 14) return many;
  if (ones === 1) return one;
  if (ones >= 2 && ones <= 4) return few;
  return many;
};
// A change with its sign: +7, −3 (a real minus)
const signed = (n: number) => (n < 0 ? `−${Math.abs(n)}` : `+${n}`);
const hoursText = (n: number) => `${n.toLocaleString('ru-RU')} ч`;

// Why points or coins changed, when that is the whole line
const pointsReasons: Record<string, string> = {
  startingBalance: ' на старте',
  adminAdjustment: ' от админа',
  finishBonus: ' — бонус за финиш',
  finishBonusRevoked: ' — бонус за финиш снят',
  runCorrection: ' после правки прохождения',
};
const coinsReasons: Record<string, string> = {
  startingBalance: ' на старте',
  adminAdjustment: ' от админа',
  completionReward: ' за прохождение',
  reroll: ' за реролл',
  runCorrection: ' после правки прохождения',
};

/** A line of the feed: words and the player's and the game's names, which the screen turns into links */
type Line<T> = readonly (string | T)[];

const feedLines = {
  seasonCreated: <T>(name: string): Line<T> => [`Сезон «${name}» создан`],
  seasonStatus: <T>(to: 'draft' | 'active' | 'closing' | 'finished' | 'archived'): Line<T> => [
    {
      draft: 'Сезон снова в подготовке',
      active: 'Сезон начался! Крути колесо',
      closing: 'Дедлайн прошёл: броски закрыты, пруфы ещё принимаются',
      finished: 'Сезон завершён',
      archived: 'Сезон ушёл в архив',
    }[to],
  ],
  deadlineSet: <T>(when: string): Line<T> => [`Дедлайн сезона: ${when}`],
  deadlineRemoved: <T>(): Line<T> => ['Дедлайн сезона снят'],
  results: <T>(): Line<T> => ['Итоги сезона подведены'],
  rulesChanged: <T>(version: number): Line<T> => [`Правила сезона обновлены, версия ${version}`],
  finishBonuses: <T>(): Line<T> => ['Бонусы за финиш пересчитаны по новым правилам'],
  joined: <T>(p: T): Line<T> => [p, ' в игре'],
  paused: <T>(p: T): Line<T> => [p, ' на паузе'],
  back: <T>(p: T): Line<T> => [p, ' снова в игре'],
  adjusted: <T>(p: T): Line<T> => [p, ': правка админа'],
  offerDiscarded: <T>(p: T, g: T): Line<T> => [p, ': админ снимает ', g],
  choiceDiscarded: <T>(p: T): Line<T> => [p, ': админ снимает выбор игры'],
  rolled: <T>(p: T, g: T): Line<T> => [p, ' выкручивает ', g],
  choiceRolled: <T>(p: T, count: number): Line<T> => [
    p,
    ` выкручивает выбор из ${count} ${plural(count, 'игры', 'игр', 'игр')}`,
  ],
  rerolled: <T>(p: T): Line<T> => [p, ' крутит колесо заново'],
  alreadyPlayed: <T>(p: T, g: T): Line<T> => [p, ' отмечает ', g, ': «Уже проходил»'],
  chose: <T>(p: T): Line<T> => [p, ' выбирает игру'],
  started: <T>(p: T, g: T): Line<T> => [p, ' начинает ', g],
  completed: <T>(p: T, g: T): Line<T> => [p, ' проходит ', g],
  reviewed: <T>(p: T, g: T): Line<T> => [p, ' оценивает ', g],
  dropped: <T>(p: T, g: T): Line<T> => [p, ' дропает ', g],
  techRerolled: <T>(p: T, g: T): Line<T> => [p, ': тех-реролл ', g],
  techToDrop: <T>(p: T, g: T): Line<T> => [p, ': тех-реролл ', g, ' засчитан как дроп'],
  hoursCorrected: <T>(p: T, g: T, from: number, to: number): Line<T> => [
    p,
    ': часы ',
    g,
    ` — ${hoursText(from)} → ${hoursText(to)}`,
  ],
  difficultyChanged: <T>(p: T, g: T, from: string, to: string): Line<T> => [
    p,
    ': сложность ',
    g,
    ` — ${from} → ${to}`,
  ],
  proofSent: <T>(p: T, g: T): Line<T> => [p, ' отправляет пруф по ', g],
  proofApproved: <T>(p: T, g: T, withoutProof: boolean): Line<T> => [
    p,
    withoutProof ? ': прохождение ' : ': пруф по ',
    g,
    withoutProof ? ' принято без скрина' : ' принят',
  ],
  proofRejected: <T>(p: T, g: T): Line<T> => [p, ': прохождение ', g, ' отклонено'],
  finished: <T>(p: T, order: number): Line<T> => [p, ` финиширует ${order}-м!`],
  finishRevoked: <T>(p: T): Line<T> => [p, ': финиш отменён'],
  effectDrawn: <T>(p: T, kind: 'good' | 'bad'): Line<T> => [
    p,
    kind === 'bad' ? ' тянет плохой ивент' : ' тянет хороший ивент',
  ],
  effectResolved: <T>(p: T, applied: boolean): Line<T> => [
    p,
    applied ? ': ивент разыгран' : ': ивент не применим',
  ],
  points: <T>(p: T, delta: number, reason: string | null): Line<T> => [
    p,
    `: ${signed(delta)} ${plural(delta, 'очко', 'очка', 'очков')}${pointsReasons[reason ?? ''] ?? ''}`,
  ],
  coins: <T>(p: T, delta: number, reason: string | null): Line<T> => [
    p,
    `: ${signed(delta)} ${plural(delta, 'монетка', 'монетки', 'монеток')}${coinsReasons[reason ?? ''] ?? ''}`,
  ],
  moved: <T>(p: T): Line<T> => [p, ': фишка переставлена'],
  undone: <T>(): Line<T> => ['Админ откатывает действие'],
};

export const ru = {
  app: {
    title: 'Игровой ивент',
    loading: 'Загружаем…',
    loadError: 'Не удалось загрузить данные. Проверь интернет и обнови страницу.',
  },
  password: {
    title: 'Смена пароля',
    why: 'Это временный пароль. Придумай свой — дальше входи с ним.',
    whyOwn: 'Новый пароль заменит текущий на всех устройствах.',
    current: 'Временный пароль',
    currentOwn: 'Текущий пароль',
    rule: (min: number) => `Не меньше ${min} символов`,
    back: 'Вернуться к игре',
    next: 'Новый пароль',
    repeat: 'Новый пароль ещё раз',
    submit: 'Сменить пароль',
    mismatch: 'Пароли не совпадают.',
    tooShort: (min: number) => `Пароль — не меньше ${min} символов.`,
  },
  login: {
    title: 'Вход',
    login: 'Логин',
    password: 'Пароль',
    submit: 'Войти',
    failed: 'Неверный логин или пароль.',
    throttled: 'Слишком много попыток. Подожди немного и попробуй снова.',
    logout: 'Выйти',
  },
  turn: {
    after: 'После прохождения',
    afterGame: (title: string) => `Прошлая игра: ${title}`,
    proofToSend: 'Пруф: ещё не отправлен',
    proofSummary: 'Пруф и проверка',
    uncheckedWaiting: (count: number, limit: number) =>
      `На проверке ${count} из ${limit}: на ${limit}-м новый ролл закроется до проверки`,
    uncheckedBlocked: (count: number, limit: number) =>
      `Проверки ждут уже ${runs(count)}: новый ролл откроется, когда их станет меньше ${limit}. Пока можно дослать пруфы.`,
    retrying: 'Пробуем ещё раз…',
    title: 'Твой ход',
    spectatorTitle: 'Ты зритель',
    finishedTitle: 'Сезон завершён',
    finishedText: 'Ходы закончились, итоги — в лидерборде.',
    closingTitle: 'Ждём проверку пруфов',
    closingText: 'Броски закрыты. Пруфы ещё можно дослать — итоги после проверки всех.',
    todo: (count: number) => `Сначала дела: ${count}`,
    roll: 'Крутить колесо',
    start: 'Начать',
    alreadyPlayed: 'Уже проходил',
    reroll: 'Реролл',
    rerollFor: (payment: RerollPayment, price: number) =>
      payment === 'freeThisRoll' || payment === 'freeMode'
        ? 'Реролл — бесплатно'
        : payment === 'freeRerollResource'
          ? 'Реролл — купон реролла'
          : payment === 'badEvent'
            ? 'Реролл — плохой ивент'
            : price === 0
              ? 'Реролл — бесплатно'
              : `Реролл — ${coins(price)}`,
    rerollConfirm: (payment: RerollPayment, price: number) =>
      payment === 'badEvent'
        ? 'За этот реролл тебе достанется плохой ивент. Продолжить?'
        : `Реролл стоит ${coins(price)}. Потратить?`,
    rerollConfirmYes: 'Да, реролл',
    rerollConfirmNo: 'Отмена',
    drop: 'Дропнуть',
    dropTitle: (title: string) => `Дропнуть «${title}»?`,
    // What a drop does, as the confirmation lists it: the penalty from the server's rules, then what always happens
    dropConsequences: (penalty: DropPenalty | null, frozen = false): string[] => {
      const always = 'Игра больше не выпадет тебе в этом сезоне, дальше — новый ролл';
      // The frozen first drops with no penalty (D-99)
      if (frozen) return ['Штрафа нет: ты уже финишировал первым', always];
      if (!penalty) return [always];
      const takes = [
        penalty.affectsPoints ? 'очки' : null,
        penalty.affectsPosition ? 'клетки (не дальше старта)' : null,
      ]
        .filter((part) => part !== null)
        .join(' и ');
      return [
        takes === ''
          ? 'Штрафа кубами нет'
          : `Штраф — кубы за дроп ${String(penalty.count)}d${String(penalty.sides)}: отнимут ${takes}`,
        ...(penalty.badEvent ? ['Тебе достанется плохой ивент'] : []),
        always,
      ];
    },
    dropHint: (minutes: number) =>
      `По правилам дропать стоит не раньше чем через ${minutes} мин. игры.`,
    dropConfirmYes: 'Дропнуть игру',
    techReroll: 'Тех-реролл',
    techRerollTitle: (title: string) => `Тех-реролл «${title}»`,
    techRerollConsequences: [
      'Бесплатно: очки и клетки не меняются',
      'Игра больше не выпадет тебе в этом сезоне',
      'Сразу новый ролл',
      'Админ может превратить тех-реролл в дроп со штрафом',
    ],
    techRerollReason: 'Причина',
    techRerollReasonPlaceholder: 'Выбери причину',
    techRerollReasonRequired: 'Выбери причину тех-реролла.',
    techRerollReasons: {
      weakPc: 'Слабый ПК',
      paidUnavailable: 'Игра платная, её нет',
      doesNotLaunch: 'Не запускается',
      emulatorTooSlow: 'Эмулятор не тянет',
      other: 'Другое',
    },
    techRerollComment: 'Комментарий',
    techRerollCommentHint: 'Для причины «Другое» — обязательно',
    techRerollCommentRequired: 'Для причины «Другое» напиши комментарий.',
    techRerollSubmit: 'Сделать тех-реролл',
    techRerollCancel: 'Отмена',
    techRerollClosed:
      'Окно тех-реролла закрылось. Если с игрой техническая проблема, напиши админу.',
    techRerollUntil: (time: string) => `Тех-реролл сам можно сделать до ${time}.`,
    gameMark: (player: string, kind: 'dropped' | 'techRerolled') =>
      kind === 'dropped' ? `Дропнул ${player}` : `Тех-реролл у ${player}`,
    alreadyPlayedGame: (title: string) => `Уже проходил: ${title}`,
    choose: 'Выбери одну из выпавших игр',
    pick: 'Выбрать:',
    pickShort: 'Выбрать',
    rolled: 'Выпала игра',
    playing: (title: string) => `Сейчас играешь: ${title}`,
    throwing: (title: string) =>
      `Пройдено: ${title}. Кубы летят на карте — потом фишка сделает ход.`,
    complete: 'Завершить прохождение',
    completeTitle: 'Игра пройдена? Отметь прохождение',
    difficulty: 'Сложность',
    difficultyHint: 'На какой сложности проходил. Админ сверит её с пруфом',
    // The die of each difficulty and the event it grants, from the run's snapshot
    difficultyDice: (dice: { label: string; sides: number; grant: 'good' | 'bad' | null }[]) =>
      `Кубы: ${dice
        .map(
          (d) =>
            `${d.label} — d${String(d.sides)}${d.grant === 'good' ? ' и хороший ивент' : d.grant === 'bad' ? ' и плохой ивент' : ''}`,
        )
        .join(', ')}.`,
    hours: 'Часы (оценка)',
    hoursHint: 'У игры нет данных о длине: укажи оценку.',
    hoursInvalid: 'Укажи число часов больше нуля.',
    hoursSource: 'Источник оценки',
    hoursSourceHint: 'Ссылка, например на HowLongToBeat, или короткая пометка.',
    hoursSourceRequired: 'Укажи источник оценки: ссылку или пометку.',
    challengeDone: 'Челлендж выполнен',
    review: 'Отзыв — по желанию',
    reviewRating: 'Оценка игры',
    reviewNoRating: 'Без оценки',
    reviewText: 'Отзыв',
    reviewRatingRequired: 'Чтобы оставить отзыв, поставь оценку от 1 до 10.',
    lastRejected: (title: string) => `Прохождение отклонено (${title}): очки и клетки сняты.`,
    lastChallengeDice: (dice: number[]) => `Кубы за челлендж: ${dice.join(' + ')}`,
    lastReview: (rating: number, text: string | null) =>
      text
        ? `Твой отзыв: ${rating.toString()}/10 — ${text}`
        : `Твоя оценка: ${rating.toString()}/10`,
    lastDice: (title: string, dice: number[], total: number, sides: number[] = []) =>
      `Кубы за прохождение (${title}): ${dice.join(' + ')} — итого ${total.toLocaleString('ru-RU')}${sides.length === 0 ? '' : ` (${sides.map((n) => `d${String(n)}`).join(' и ')})`}`,
    spectator: 'Ты смотришь сезон как зритель.',
  },
  difficulty: {
    easy: 'Лёгкая',
    normal: 'Нормальная',
    hard: 'Сложная',
    extreme: 'Выше сложной',
  },
  proof: {
    title: 'Пруф',
    lead: 'Хватит одного: ссылки, скрины или свидетель.',
    link: 'Ссылка на скрин или видео',
    linkNumber: (n: number) => `Ссылка ${n}: скрин или видео`,
    addLink: 'Ещё ссылка',
    note: 'Заметка',
    submit: 'Отправить пруф',
    linkRequired: 'Добавь ссылку или скрин либо выбери свидетеля.',
    shot: 'Добавить скрин',
    shotHint: (left: number) => `JPEG, PNG, WebP или GIF, можно ещё ${left}`,
    shotAlt: (n: number) => `Скрин ${n}`,
    removeShot: (n: number) => `Убрать скрин ${n}`,
    remove: 'Убрать',
    witness: 'Свидетель',
    witnessHint: 'Игрок, который видел прохождение',
    noWitness: 'Без свидетеля',
    linkInvalid: 'Ссылка должна начинаться с http:// или https://.',
    status: {
      pending: 'Пруф ждёт проверки админом.',
      approved: 'Пруф одобрен.',
      rejected: 'Пруф отклонён.',
    },
    reviewComment: (comment: string) => `Комментарий админа: ${comment}`,
  },
  avatar: {
    title: 'Моя аватарка',
    current: 'Текущая аватарка',
    none: 'Аватарки пока нет.',
    file: 'Загрузить картинку',
    link: 'Или ссылка на гифку (Tenor, Giphy, Klipy)',
    useLink: 'Взять по ссылке',
    linkRequired: 'Вставь ссылку.',
    remove: 'Убрать аватарку',
  },
  upload: {
    uploading: 'Загружаем…',
    failed: 'Не удалось загрузить файл. Попробуй ещё раз.',
    errors: {
      'file.typeInvalid': 'Это не картинка: подходят JPEG, PNG, WebP и GIF.',
      'file.broken': 'Картинка повреждена или обрезана.',
      'file.tooLarge': 'Файл слишком большой.',
      'file.tooOften': 'Слишком много загрузок подряд. Подожди минуту.',
      'file.tooManyPixels': 'Картинка слишком большого разрешения.',
      'file.tooManyFrames': 'В GIF слишком много кадров.',
      'file.dailyLimit': 'На сегодня лимит загрузок исчерпан.',
      'file.busy': 'Сервер занят другими картинками. Попробуй через минуту.',
      'file.downloadsPerHour': 'Лимит скачиваний по ссылке на этот час исчерпан.',
      'file.urlInvalid': 'Нужна ссылка, начинающаяся с https://.',
      'file.hostNotAllowed': 'Ссылки принимаются только с Tenor, Giphy и Klipy.',
      'file.addressNotPublic': 'По этой ссылке картинку взять нельзя.',
      'file.downloadFailed': 'Не удалось скачать картинку по ссылке.',
    },
  },
  // H1: the shell of every page
  time: {
    moscow: (date: string, clock: string) => `${date}, ${clock} МСК`,
  },
  shell: {
    menu: (name: string) => `Меню: ${name}`,
    changePassword: 'Сменить пароль',
    noSeasonTitle: 'Сезон ещё не начался',
    noSeasonText: 'Когда админ заведёт сезон, здесь появятся карта, лидерборд и твой ход.',
    loadErrorTitle: 'Сайт не загрузился',
    loadErrorText: 'Сервер не ответил. Проверь интернет и попробуй ещё раз.',
    loginLead: 'Настольная игра, где ходы зарабатываются прохождением видеоигр.',
    meeples: 'ИВЕНТ',
  },
  // G3: the design system's components (src/ui, src/board)
  ui: {
    loading: 'Загружаем…',
    retry: 'Попробовать ещё раз',
    cancel: 'Отмена',
    close: 'Закрыть',
    connectionLost: 'Нет связи с сервером, переподключаемся',
    toFinish: (cells: number) => (cells <= 0 ? 'на финише' : `до финиша ${cells} кл.`),
    routeProgress: 'Путь от старта до финиша',
  },
  board: {
    start: 'Старт',
    finish: 'Финиш',
    you: 'ты',
    more: (count: number) => `+${count}`,
    zoomIn: 'Приблизить',
    zoomOut: 'Отдалить',
    toMe: 'К моей фишке',
    leaderboard: 'Лидерборд',
    first: 'Первый',
    firstProvisional: 'Первый, пока предварительно',
    inactive: 'не в игре',
    noWay: 'нет пути до финиша',
    place: (place: number) => `${place} место`,
    points: (points: number) => `${points} очк.`,
    sheet: (place: number, points: number) => `Ты на ${place} месте, ${points} очк.`,
    sheetPeek: (leader: string, place: number) => `Лидирует ${leader} · ты ${place}-й`,
    sheetLeader: (leader: string) => `Лидирует ${leader}`,
    rule: 'Первый — кто первым дошёл до финиша, дальше по очкам',
    nowPlaying: 'Сейчас проходишь',
    hours: (value: number | null) =>
      value === null ? 'без оценки по HLTB' : `≈ ${value} ч по HLTB`,
    complete: 'Завершить прохождение',
    drop: 'Дропнуть',
    dropTitle: (title: string) => `Дропнуть «${title}»?`,
    dropConfirm: 'Дропнуть игру',
    noCover: 'Без обложки',
    roleDescription: 'карта',
    mapLabelNobody:
      'Карта сезона: развилки и зоны. Стрелки двигают карту, плюс и минус меняют масштаб',
    mapLabel: (at: number) =>
      `Карта сезона: развилки и зоны, ты на клетке ${at}. Стрелки двигают карту, плюс и минус меняют масштаб`,
  },
  moments: {
    skip: 'Показать результат',
    skipHint: 'Нажми на анимацию, чтобы сразу увидеть результат',
    wheel: {
      category: (name: string) => `Категория: ${name}`,
      miss: (who: string) => `Уже прошёл ${who}, крутим дальше`,
      missPlaying: (who: string) => `Сейчас играет ${who}, крутим дальше`,
      // The result, when the wheel stands: past tense, no «крутим дальше»
      missed: (count: number) => `Колесо пропустило ${games(count)}`,
      missedCompleted: (title: string, who: string, day: string | null) =>
        `${title} — уже прошёл ${who}${day ? `, ${day}` : ''}`,
      missedPlaying: (title: string, who: string) => `${title} — сейчас играет ${who}`,
      announce: (category: string, result: string) => `Категория: ${category}. ${result}`,
      toMap: 'К карте',
      choice: (count: number) => `На выбор: ${games(count)}`,
      missNote: (title: string) => `Промах: ${title}.`,
      result: (title: string) => `Выпала игра: ${title}`,
    },
    dice: {
      challengeDie: 'челлендж',
      sides: (n: number) => `d${String(n)}`,
      // «до»: steps past the finish burn, the points do not
      result: (plain: number[], challenge: number[], total: number) =>
        `${plain.join(' + ')}${challenge.length === 0 ? '' : ` + ${challenge.join(' + ')} за челлендж`}. Итого +${total} очков и до ${total} клеток вперёд`,
      // The frozen first plays in free mode: dice only, no points (Q-3)
      resultFree: (plain: number[], challenge: number[]) =>
        `${plain.join(' + ')}${challenge.length === 0 ? '' : ` + ${challenge.join(' + ')} за челлендж`}. Свободный режим: очки не начисляются`,
      resultStay: (plain: number[], challenge: number[], total: number) =>
        `${plain.join(' + ')}${challenge.length === 0 ? '' : ` + ${challenge.join(' + ')} за челлендж`}. Итого +${total} очков, фишка стоит на месте`,
      // The dice's result, shown big once the token stands
      after: (total: number, at: string) => `+${total} очков. ${at}`,
      afterStay: (total: number) => `+${total} очков, фишка стоит на месте`,
      afterFree: 'Свободный режим: очки не начисляются',
      // The completion's moment, said once when the token stands: the game, the dice and where the token is now
      announce: (title: string, dice: string, at: string) =>
        [`Пройдено: ${title}`, dice, at].filter((part) => part !== '').join('. '),
      at: (cell: number, finish: boolean) =>
        finish ? 'Фишка на финише' : `Фишка на клетке ${cell}`,
      // The first finish stands only when the proofs are approved (SPEC: provisional until then)
      atFirst: (provisional: boolean) =>
        provisional
          ? 'Фишка на финише — ты первый! Предварительно: ждём проверку пруфов'
          : 'Фишка на финише — ты первый!',
      atLater: (order: number) =>
        `Фишка на финише — ты ${order}-й. Позиция закреплена, очки ещё растут`,
    },
    finish: {
      first: (name: string) => `${name} финиширует первым!`,
      provisional: 'Предварительно: ждём проверку пруфов',
      frozen: 'Первое место: очки больше не меняются, эффекты других на тебя не действуют',
    },
  },
  // G3: the styleguide page (/styleguide) with every component in every state
  styleguide: {
    title: 'Стайлгайд',
    lead: 'Все токены и компоненты сайта во всех состояниях. Экраны собираются только из них; значения вне токенов запрещает линтер.',
    contents: 'Разделы',
    sections: [
      ['shell', 'Шапка'],
      ['colors', 'Цвета'],
      ['tokens', 'Фишки'],
      ['type', 'Шрифты'],
      ['shape', 'Отступы и формы'],
      ['buttons', 'Кнопки'],
      ['marks', 'Метки'],
      ['fields', 'Поля'],
      ['states', 'Состояния'],
      ['progress', 'Прогресс и лидерборд'],
      ['run', 'Текущая игра'],
      ['offer', 'Результат ролла'],
      ['complete', 'Завершение'],
      ['proof', 'Пруф'],
      ['dialogs', 'Окна'],
      ['map', 'Карта'],
      ['wheel', 'Колесо'],
      ['dice', 'Кубики'],
      ['move', 'Ход фишки'],
      ['finish', 'Финиш'],
      ['feed', 'Лента'],
      ['profile', 'Профиль'],
      ['game', 'Страница игры'],
    ] as [string, string][],
    zones: {
      meadow: 'Поляна новичков',
      forest: 'Лес долгих игр',
      mountains: 'Хардкорные пики',
      swamp: 'Болото рероллов',
      city: 'Город кооператива',
      castle: 'Замок',
    },
    shell: {
      title: 'Шапка',
      lead: 'Название, знак потери связи, сообщение о баге и моё меню. На телефоне 390 px — без связи, знак только иконкой; ниже — широкая, связь есть.',
    },
    states: {
      normal: 'Обычное',
      hover: 'Наведение',
      focus: 'Фокус',
      active: 'Нажатие',
      disabled: 'Отключено',
      loading: 'Загрузка',
    },
    colors: {
      title: 'Цвета',
      lead: 'Палитра «Кухонный стол»: деревянный стол под лампой и разноцветные мипла. Синий — только главное действие, красный — только опасные действия.',
      list: [
        ['page', 'Фон страницы'],
        ['card', 'Карточки и панели'],
        ['ink', 'Текст и все контуры'],
        ['ink-soft', 'Второстепенный текст'],
        ['action', 'Главное действие экрана'],
        ['action-strong', 'Главное действие под указателем'],
        ['me', 'Моя фишка, моя клетка, мои полосы'],
        ['gold', 'Первое место, клетки ивентов, кубик челленджа'],
        ['danger', 'Дроп, откат, реджект, удаление'],
        ['success', 'Готово, одобрено'],
        ['warning', 'Внимание, нет связи'],
        ['info', 'Подсказки и новости'],
        ['table', 'Стол под картой'],
        ['zone-meadow', 'Зона: поляна'],
        ['zone-forest', 'Зона: лес'],
        ['zone-mountains', 'Зона: горы'],
        ['zone-swamp', 'Зона: болото'],
        ['zone-city', 'Зона: город'],
        ['zone-castle', 'Зона: замок'],
      ] as [string, string][],
    },
    tokens: {
      title: 'Цвета фишек',
      lead: '16 цветов, различимых и при дальтонизме. Цвет буквы на фишке подбирается по контрасту.',
      letters: ['А', 'Б', 'В', 'Г', 'Д', 'Е', 'Ж', 'З', 'И', 'К', 'Л', 'М', 'Н', 'О', 'П', 'Р'],
    },
    type: {
      title: 'Шрифты',
      lead: 'Unbounded — заголовки, числа и карта; Onest — всё остальное. Семь размеров, цифры табличные.',
      h1: 'Заголовок экрана',
      h2: 'Заголовок раздела',
      h3: 'Заголовок карточки',
      body: 'Основной текст: живой русский на «ты», строки не длиннее 75 символов, цифры 0123456789 не прыгают.',
      small: 'Подпись: 2 ч по HLTB, 12 мин назад',
      tiny: 'Самая мелкая подпись: до финиша 11 кл.',
    },
    shape: {
      title: 'Отступы и формы',
      lead: 'Отступы кратны 4 px. Скругления по уровням: метка, строка, карточка. Жёсткая тень — только у главной кнопки и карточек моментов.',
      lift: 'Карточка момента',
      radius: { sm: 'Метка, 8 px', md: 'Строка, 14 px', lg: 'Карточка, 20 px' },
    },
    buttons: {
      title: 'Кнопки',
      lead: 'Одно главное действие на экран. Опасные действия красные и подтверждаются.',
      main: 'Главная',
      quiet: 'Обычная',
      quietText: 'Крутить ещё раз',
      danger: 'Опасная',
      dangerMain: 'Опасная в подтверждении',
      link: 'Ссылка',
      linkText: 'Все правила',
      dangerLink: 'Опасная ссылка',
      icon: 'Иконка',
    },
    marks: { title: 'Метки', deadline: '5 дн. до дедлайна' },
    fields: {
      title: 'Поля',
      lead: 'Подпись над полем, формат ввода рядом, ошибка сразу под полем.',
      hours: 'Сколько часов играл',
      hoursHint: 'Число, можно с дробью: 27 или 12,5',
      link: 'Ссылка на пруф',
      linkError: 'Нужна ссылка целиком, с https://',
      select: 'Причина',
      selectError: 'Выбери причину',
      choice: 'Сложность',
      file: 'Добавить скрин',
      fileHint: 'JPEG, PNG, WebP или GIF',
    },
    feedback: {
      title: 'Состояния',
      lead: 'Статус всегда с иконкой и словами, не только цветом.',
      success: 'Пруф одобрен: +11 очков и клеток',
      info: 'Сова выкрутила Hollow Knight',
      warning: 'До дедлайна 2 часа: успей закончить ход',
      danger: 'Игра дропнута: −4 очка и клетки',
      emptyTitle: 'В пуле пока нет игр',
      emptyText: 'Добавь первую игру или загрузи таблицу, и колесо можно будет крутить.',
      emptyAction: 'Добавить игру',
      errorTitle: 'Лента не загрузилась',
      errorText: 'Сервер не ответил. Проверь интернет и попробуй ещё раз.',
      loading: 'Загрузка',
    },
    progress: {
      title: 'Прогресс и лидерборд',
      lead: 'Полоса пути до финиша в моём цвете, полосы очков относительно лидера: серые, моя — моим цветом.',
    },
    offer: {
      title: 'Результат ролла',
      lead: 'После колеса: выпавшая игра с категорией, промахами и пометками других игроков, одно главное действие «Начать». Вторая карточка — выбор из нескольких игр одной категории: вся карточка — кнопка.',
    },
    run: {
      title: 'Текущая игра',
      lead: 'Самое длинное название пула, путь до финиша, одно главное действие и тихий дроп в стороне. Вторая карточка — без обложки и во время отправки.',
    },
    complete: {
      title: 'Завершение прохождения',
      lead: 'Сложность — пилюлями на виду, часы с источником — только у игры без часов, челлендж, отзыв свёрнут. Одно главное действие; тех-реролл и дроп — тихо внизу. Вторая карточка — во время отправки: окно тех-реролла закрылось.',
      message: 'Сервер не ответил. Проверь интернет и попробуй ещё раз.',
    },
    proof: {
      title: 'Пруф',
      lead: 'Ссылки, скрины через свою кнопку, свидетель и заметка; ошибка — у своего поля. Дальше — пруф на проверке, одобренный со скринами и отклонённый с комментарием админа.',
      comment: 'На скрине не видно титров',
    },
    dialogs: {
      title: 'Окна',
      lead: 'Подтверждение опасного действия показывает последствия. Лидерборд на телефоне выезжает снизу.',
    },
    map: {
      title: 'Карта',
      lead: 'Демо-мир: две развилки, зоны, 16 игроков. Двигается мышью, пальцем и стрелками, масштаб — колесом, щипком, кнопками и клавишами плюс и минус.',
      phone: 'Телефон: вид вокруг моей фишки',
    },
    wheel: {
      title: 'Ролл колеса',
      lead: 'Только категории со свободными играми. Промах показывается, потом колесо крутится дальше.',
      spin: 'Крутить колесо',
      again: 'Крутить ещё раз',
      start: 'Начать прохождение',
    },
    dice: {
      title: 'Бросок кубиков',
      lead: 'Hollow Knight, 27 ч на «Сложно»: 3 кубика и кубик за челлендж.',
      roll: 'Бросить кубы',
      again: 'Бросить ещё раз',
    },
    move: {
      title: 'Ход фишки',
      lead: 'Фишка идёт клетка за клеткой, на развилке уходит в горы и останавливается на клетке ивента.',
      fork: 'Развилка: через Хардкорные пики',
      landed: (at: number) => `Фишка на клетке ${at}: клетка ивента, тяни карточку`,
      go: (steps: number) => `Сходить на ${steps}`,
      reset: 'Вернуть фишку',
    },
    // H5
    feed: {
      title: 'Лента',
      lead: 'Одна строка — одно действие: стикер того, о ком строка, со значком события, имена игрока и игры — ссылки, факты — плашками, отзыв и комментарий — цитатой. Отменённое зачёркнуто и подписано. Дни — по московскому времени.',
      loading: 'Загрузка',
      end: 'Конец ленты и следующая страница',
    },
    profile: {
      title: 'Профиль',
      lead: 'Имя, аватарка целиком, число пройденных игр, сезоны с очками и местом, отзывы. Ниже — мой профиль без сезонов и отзывов.',
    },
    gamePage: {
      title: 'Страница игры',
      lead: 'Карточка из пула и все прохождения во всех сезонах: статус словом и значком, сложность, часы и отзыв. Ниже — игра, которую ещё никто не брал.',
    },
    finish: {
      title: 'Финиш',
      lead: 'Первый дошедший до финиша. Место предварительное, пока админ не проверит пруфы.',
      go: 'Дойти до финиша',
      again: 'Ещё раз',
    },
  },
  maintenance: {
    banner: 'Идёт обслуживание: сайт на минуту только для чтения. Изменения сейчас не сохраняются.',
  },
  bugReport: {
    open: 'Сообщить о баге',
    title: 'Сообщить о баге',
    what: 'Что случилось и как должно было быть?',
    attach: 'Приложить скриншот страницы',
    noScreenshot: 'Скриншот снять не удалось — отчёт уйдёт без него.',
    context:
      'К отчёту приложатся страница, твои последние действия и ошибки браузера. То, что введено в поля, не попадёт.',
    send: 'Отправить',
    cancel: 'Отмена',
    sent: 'Спасибо! Отчёт отправлен.',
    done: 'Готово',
    textRequired: 'Опиши, что случилось.',
    tooOften: 'Слишком много отчётов за час. Попробуй позже.',
    failed: 'Отчёт не отправился. Попробуй ещё раз.',
  },
  effects: {
    title: 'Нужно разыграть',
    drawEvent: (kind: 'good' | 'bad', source: keyof typeof effectSources) =>
      `${kind === 'bad' ? 'Плохой ивент' : 'Хороший ивент'} ${effectSources[source]}`,
    comment: 'Комментарий',
    applied: 'Применено',
    notApplicable: 'Не применимо',
    commentNeeded: 'Для «не применимо» напиши комментарий',
  },
  map: {
    title: 'Карта',
    start: 'Старт',
    finish: 'Финиш',
    cellNumber: (n: number) => `Клетка ${n}`,
  },
  season: {
    deadline: (text: string) => `Дедлайн: ${text}`,
    closing: 'Дедлайн прошёл: броски закрыты, пруфы принимаются.',
    finished: 'Сезон завершён. Итоги ниже.',
  },
  leaderboard: {
    title: 'Лидерборд',
    row: (place: number, name: string, points: number, cellsToFinish: number | null) =>
      `${place}. ${name}: ${points.toLocaleString('ru-RU')} очк.` +
      (cellsToFinish === null
        ? ''
        : cellsToFinish === 0
          ? ', на финише'
          : `, до финиша ${cellsToFinish} кл.`),
    first: '— первое место',
    provisional: '— первое место (предварительно)',
  },
  // H5: the season's feed, a player's profile and a game's page
  feed: {
    title: 'Лента сезона',
    pageNotFoundTitle: 'Такой страницы нет',
    pageNotFoundText: 'Проверь адрес или вернись к сезону.',
    nav: { label: 'Разделы сайта', season: 'Сезон', feed: 'Лента', profile: 'Мой профиль' },
    preview: 'Свежее в ленте',
    all: 'Вся лента',
    more: 'Показать ещё',
    start: 'Это самое начало сезона',
    today: 'Сегодня',
    yesterday: 'Вчера',
    undone: 'Отменено',
    undoneHint: 'Админ откатил это действие: оно больше не считается',
    someone: 'Игрок',
    someGame: 'игра',
    fresh: (n: number) =>
      `${n} ${plural(n, 'новая запись', 'новые записи', 'новых записей')} в ленте`,
    emptyTitle: 'В ленте пока пусто',
    emptyText: 'Здесь появятся роллы, прохождения и дропы всех игроков. Начни с первого ролла!',
    emptyAction: 'К карте',
    errorTitle: 'Лента не загрузилась',
    errorText: 'Сервер не ответил. Проверь интернет и попробуй ещё раз.',
    moreError: 'Следующие записи не загрузились. Попробуй ещё раз.',
    noSeasonTitle: 'Такого сезона нет',
    noSeasonText: 'Возможно, ссылка устарела. Открой ленту текущего сезона.',
    lines: feedLines,
    facts: {
      points: (n: number) => `${signed(n)} ${plural(n, 'очко', 'очка', 'очков')}`,
      cells: (n: number) => `${signed(n)} ${plural(n, 'клетка', 'клетки', 'клеток')}`,
      pointsAndCells: (n: number) =>
        `${signed(n)} ${plural(n, 'очко', 'очка', 'очков')} и ${plural(n, 'клетка', 'клетки', 'клеток')}`,
      coins: (n: number) => `${signed(n)} ${plural(n, 'монетка', 'монетки', 'монеток')}`,
      dice: (values: number[]) =>
        values.length === 1
          ? `Кубик: ${values[0] ?? 0}`
          : `Кубы: ${values.join(' + ')} = ${values.reduce((a, b) => a + b, 0)}`,
      challenge: (values: number[]) => `За челлендж: ${values.join(' + ')}`,
      penalty: (values: number[]) =>
        `Штраф: ${values.join(' + ')} = ${values.reduce((a, b) => a + b, 0)}`,
      hours: hoursText,
      category: (name: string) => `Категория: ${name}`,
      misses: (n: number) => `${n} ${plural(n, 'промах', 'промаха', 'промахов')} колеса`,
      reroll: {
        freeThisRoll: 'Бесплатный реролл',
        freeRerollResource: 'Реролл по купону',
        coins: 'Реролл за монетки',
        badEvent: 'Реролл за плохой ивент',
        freeMode: 'Реролл в свободном режиме',
      } as Record<string, string>,
      finish: (order: number) => `Финиш: ${order}-е место`,
      frozen: 'Очки заморожены: первый финиш',
      chainCut: 'Цепочка эффектов оборвана',
      afterFinish: 'После финиша',
      moved: 'Фишка переставлена',
    },
    rating: (n: number) => `${n} из 10`,
    ratingLabel: (n: number) => `Оценка: ${n} из 10`,
  },
  profile: {
    completed: (n: number) =>
      n === 0
        ? 'Пока без пройденных игр'
        : `${plural(n, 'Пройдена', 'Пройдены', 'Пройдено')} ${games(n)}`,
    seasons: 'Сезоны',
    reviews: 'Отзывы',
    place: (n: number) => `${n} место`,
    noPlace: 'Место — после итогов',
    points: (n: number) => `${n.toLocaleString('ru-RU')} ${plural(n, 'очко', 'очка', 'очков')}`,
    status: {
      draft: 'Готовится',
      active: 'Идёт',
      closing: 'Дедлайн прошёл',
      finished: 'Завершён',
      archived: 'В архиве',
    } as Record<string, string>,
    seasonFeed: 'Лента сезона',
    noSeasonsTitle: 'Сезонов пока нет',
    noSeasonsText: 'Сезоны появятся здесь, когда админ добавит игрока.',
    noReviewsTitle: 'Отзывов пока нет',
    noReviewsText:
      'Отзыв пишется после прохождения — его увидят в ленте, профиле и на странице игры.',
    noReviewsMine: 'Пройди игру и напиши отзыв — его увидят в ленте, профиле и на странице игры.',
    you: 'ты',
    errorTitle: 'Профиль не загрузился',
    notFoundTitle: 'Такого игрока нет',
    notFoundText: 'Возможно, аккаунт удалён или ссылка неверная.',
    toSeason: 'К сезону',
  },
  gamePage: {
    runsTitle: (n: number) => (n === 0 ? 'Прохождения' : `Прохождения: ${n}`),
    tags: 'Категории',
    year: (n: number) => `${n} г.`,
    hours: (n: number | null) => (n === null ? 'Нет оценки по HLTB' : `≈ ${hoursText(n)} по HLTB`),
    coop: 'Кооператив',
    deleted: 'Удалена из пула',
    condition: (text: string) => `Условие прохождения: ${text}`,
    status: {
      playing: 'Проходит',
      completed: 'Пройдена',
      dropped: 'Дроп',
      techRerolled: 'Тех-реролл',
      rejected: 'Отклонено',
    } as Record<string, string>,
    played: (hours: number) => `${hoursText(hours)} в игре`,
    noRunsTitle: 'Эту игру ещё никто не брал',
    noRunsText: 'Когда она выпадет на колесе, прохождения и отзывы появятся здесь.',
    errorTitle: 'Страница игры не загрузилась',
    notFoundTitle: 'Такой игры нет',
    notFoundText: 'Возможно, её удалили из пула или ссылка неверная.',
  },
  rejection: rejection as Readonly<Record<string, string>> & typeof rejection,
} as const;
