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
  'pool.duplicate': 'Такая игра уже есть в пуле.',
  'pool.similar': 'В пуле есть похожее название: проверь и подтверди.',
  'pool.cardInvalid': 'Проверь поля: что-то заполнено не так.',
  'pool.coverUnknown': 'Обложка не найдена. Загрузи её ещё раз.',
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

/** One word for the tech reroll, whatever map names it (D-202) */
const techReroll = 'Тех-реролл';

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
    techReroll,
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
  // A season's status, one word for the profile and the admin (D-202)
  seasonStatus: {
    draft: 'Готовится',
    active: 'Идёт',
    closing: 'Ждёт проверки пруфов',
    finished: 'Завершён',
    archived: 'В архиве',
  } as Readonly<Record<string, string>>,
  // A game's length, one way everywhere (D-202): the estimate of HowLongToBeat, or hours played
  hours: {
    value: hoursText,
    estimate: (n: number | null) =>
      n === null ? 'Без оценки по HLTB' : `≈ ${hoursText(n)} по HLTB`,
  },
  ui: {
    loading: 'Загружаем…',
    retry: 'Попробовать ещё раз',
    cancel: 'Отмена',
    close: 'Закрыть',
    nameRequired: 'Напиши название.',
    showMore: 'Показать ещё',
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
      ['pool', 'Игры пула'],
      ['rules', 'Правила сезона'],
      ['dialogs', 'Окна'],
      ['admin', 'Админка'],
      ['map', 'Карта'],
      ['wheel', 'Колесо'],
      ['dice', 'Кубики'],
      ['move', 'Ход фишки'],
      ['finish', 'Финиш'],
      ['feed', 'Лента'],
      ['profile', 'Профиль'],
      ['game', 'Страница игры'],
      ['whats-new', 'Что нового'],
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
      selectHint: 'Список работает с клавиатуры и на телефоне',
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
    },
    proof: {
      title: 'Пруф',
      lead: 'Ссылки, скрины через свою кнопку, свидетель и заметка; ошибка — у своего поля. Дальше — пруф на проверке, одобренный со скринами и отклонённый с комментарием админа.',
      comment: 'На скрине не видно титров',
    },
    admin: {
      title: 'Админка',
      lead: 'Прохождение в очереди пруфов: финиш сверху с золотой меткой, закрытый лимитом ролл — предупреждением. Одобрение — главное действие только у первой карточки, реджект подтверждается.',
    },
    dialogs: {
      title: 'Окна',
      lead: 'Подтверждение опасного действия показывает последствия. Лидерборд на телефоне выезжает снизу.',
      form: 'Форма поверх страницы',
    },
    // H6, H7: new sections of the styleguide
    pool: {
      title: 'Игры пула',
      lead: 'Карточка игры пула со статусом в сезоне: уже прошёл, сейчас играет, дроп другого игрока, исключена для меня, свободна. Ниже — форма добавления игры.',
      chips: 'Метки статусов',
    },
    rules: {
      title: 'Правила сезона',
      lead: 'Страница правил из демо-конфига: оглавление, разделы с настоящими числами и история изменений.',
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
    whatsNew: {
      title: 'Что нового',
      lead: 'Список выпусков из заметок к коммитам: новые сверху, выпуск без заметок — одной строкой. Открывается из баннера «Сайт обновился».',
    },
    finish: {
      title: 'Финиш',
      lead: 'Первый дошедший до финиша. Место предварительное, пока админ не проверит пруфы.',
      go: 'Дойти до финиша',
      again: 'Ещё раз',
    },
  },
  whatsNew: {
    banner: 'Сайт обновился.',
    open: 'Что нового',
    close: 'Скрыть',
    title: 'Что нового',
    release: (version: string, date: string) =>
      `${version} · ${new Date(`${date}T12:00:00Z`).toLocaleDateString('ru-RU', { day: 'numeric', month: 'long' })}`,
    quiet: 'Исправления и улучшения под капотом.',
    empty: 'Пока ни одного выпуска.',
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
    preview: 'Свежее в ленте',
    all: 'Вся лента',
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
    noSeasonsTitle: 'Сезонов пока нет',
    noSeasonsText: 'Сезоны появятся здесь, когда админ добавит игрока.',
    noReviewsTitle: 'Отзывов пока нет',
    noReviewsText:
      'Отзыв пишется после прохождения — его увидят в ленте, профиле и на странице игры.',
    noReviewsMine: 'Пройди игру и напиши отзыв — его увидят в ленте, профиле и на странице игры.',
    errorTitle: 'Профиль не загрузился',
    notFoundTitle: 'Такого игрока нет',
    notFoundText: 'Возможно, аккаунт удалён или ссылка неверная.',
    toSeason: 'К сезону',
  },
  gamePage: {
    runsTitle: (n: number) => (n === 0 ? 'Прохождения' : `Прохождения: ${n}`),
    tags: 'Категории',
    year: (n: number) => `${n} г.`,
    coop: 'Кооператив',
    deleted: 'Удалена из пула',
    condition: (text: string) => `Условие прохождения: ${text}`,
    status: {
      playing: 'Проходит',
      completed: 'Пройдена',
      dropped: 'Дроп',
      techRerolled: techReroll,
      rejected: 'Отклонено',
    } as Record<string, string>,
    played: (hours: number) => `${hoursText(hours)} в игре`,
    noRunsTitle: 'Эту игру ещё никто не брал',
    noRunsText: 'Когда она выпадет на колесе, прохождения и отзывы появятся здесь.',
    errorTitle: 'Страница игры не загрузилась',
    notFoundTitle: 'Такой игры нет',
    notFoundText: 'Возможно, её удалили из пула или ссылка неверная.',
  },
  // The sections of the site in the header (H5–H7) and my own pages in my menu
  nav: {
    label: 'Разделы сайта',
    season: 'Сезон',
    feed: 'Лента',
    pool: 'Пул',
    rules: 'Правила',
    profile: 'Мой профиль',
    admin: 'Админка',
  },
  // H6: the pool of games (SPEC «Пул игр», «Статусы игры в сезоне», «Дубли»)
  pool: {
    title: 'Пул игр',
    found: (n: number, total: number) =>
      n === total ? `В пуле ${games(total)}` : `Нашлось ${games(n)} из ${total}`,
    searched: (n: number) => `Нашлось ${games(n)}`,
    add: 'Добавить игру',
    search: 'Поиск по названию',
    searchRegion: 'Поиск и фильтры',
    filters: (n: number) => (n === 0 ? 'Фильтры' : `Фильтры: ${n}`),
    category: 'Категория',
    anyCategory: 'Все категории',
    categoryOption: (name: string, n: number) => `${name} (${n})`,
    length: 'Длина по HowLongToBeat',
    lengths: {
      any: 'Любая',
      short: 'До 5 ч',
      medium: '5–15 ч',
      long: 'Больше 15 ч',
    },
    freeOnly: 'Только свободные для меня',
    reset: 'Сбросить фильтры',
    author: (name: string) => `Добавил ${name}`,
    free: 'Свободна',
    offWheel: 'Не выпадет: ни одной её категории нет на колесе',
    listLabel: 'Игры пула',
    more: (next: number, left: number) =>
      next === left ? `Показать ещё ${games(left)}` : `Показать ещё ${next} из ${left}`,
    completed: (player: string, day: string | null) =>
      day ? `Уже прошёл ${player}, ${day}` : `Уже прошёл ${player}`,
    playing: (player: string) => `Сейчас играет ${player}`,
    excluded: {
      alreadyPlayed: 'Тебе не выпадет: ты уже проходил',
      dropped: 'Тебе не выпадет: ты дропнул',
      techRerolled: 'Тебе не выпадет: у тебя был тех-реролл',
    },
    emptyTitle: 'В пуле пока нет игр',
    emptyText: 'Добавь первую — колесо крутится только по играм из пула.',
    emptyTextViewer: 'Игры добавляют игроки и админ.',
    nothingTitle: 'Ничего не нашлось',
    nothingText: 'Попробуй другое название или сбрось фильтры.',
    loadErrorTitle: 'Пул не загрузился',
    statusError: 'Статусы игр в сезоне не загрузились: видно только сам пул.',
    added: (title: string) => `«${title}» теперь в пуле.`,
    form: {
      title: 'Новая игра',
      name: 'Название',
      nameHint: 'Как в Steam или на HowLongToBeat',
      nameTooLong: 'Название — не длиннее 200 символов.',
      categories: 'Категории',
      categoriesHint: 'Колесо выбирает игру по категории: отметь хотя бы одну.',
      categoriesRequired: 'Отметь хотя бы одну категорию.',
      noCategories: 'Категорий пока нет: их заводит админ.',
      hours: 'Часы по HowLongToBeat, основной сюжет',
      hoursHint: 'Шаг — полчаса, например 12,5. Не знаешь — оставь пустым.',
      hoursInvalid: 'Часы — от 0,5 до 1000 с шагом в полчаса.',
      year: 'Год выхода',
      yearInvalid: 'Год — от 1950 до 2100.',
      note: 'Заметка',
      noteHint: 'Челлендж или условие прохождения для бесконечных игр',
      noteTooLong: 'Заметка — не длиннее 1000 символов.',
      coop: 'Кооп-игра',
      cover: 'Обложка',
      coverHint: 'Картинка с устройства, необязательно',
      coverPick: 'Загрузить обложку',
      coverReplace: 'Другая обложка',
      coverRemove: 'Убрать обложку',
      checking: 'Ищем похожие названия…',
      similarTitle: 'В пуле уже есть похожие',
      similarText: 'Проверь, что это не та же игра под другим названием.',
      same: (title: string) => `«${title}» уже в пуле — второй раз её не добавить.`,
      submit: 'Добавить в пул',
      submitAnyway: 'Всё равно добавить',
      failed: 'Игра не добавилась. Попробуй ещё раз.',
      tooOften: 'Слишком много игр за минуту. Подожди немного.',
    },
  },
  // H7: the rules page, built from the season's current ruleset (SPEC «Правила на сайте»)
  rules: {
    title: 'Правила сезона',
    lead: 'Все числа здесь — из текущих правил сезона. Поменяет админ — поменяются и тут; уже выпавшая игра живёт по правилам на момент своего ролла.',
    version: (n: number) => `Версия правил ${n}`,
    loadErrorTitle: 'Правила не загрузились',
    noSeasonTitle: 'Правил пока нет',
    noSeasonText: 'Правила появятся вместе с сезоном.',
    contents: 'На этой странице',
    sections: {
      win: 'Как победить',
      roll: 'Ролл игры',
      reward: 'Награда за прохождение',
      drop: 'Дроп и тех-реролл',
      finish: 'Финиш',
      deadline: 'Сроки',
      history: 'История изменений',
    },
    win: {
      twoScores:
        'У тебя два показателя: позиция на карте решает первое место, очки — все остальные.',
      map: (cells: number) => `От старта до финиша — ${cells} клеток.`,
      first: 'Первое место — первый, кто дошёл до финиша.',
      firstApproval:
        'Пока админ не проверил пруфы прохождений, которые довели до финиша, первое место предварительное.',
      firstFrozen:
        'Первый сразу после финиша замораживается: очки больше не растут, дальше он играет без зачёта и не влияет на других.',
      firstFrozenApproved:
        'Когда его пруфы проверены, первый замораживается: очки больше не растут, дальше он играет без зачёта и не влияет на других.',
      others: 'Остальные места — по очкам на момент дедлайна.',
      nobody: 'Если до финиша никто не дошёл, все места распределяются по очкам.',
      tiebreakers: (list: string) => `При равных очках выше тот, у кого ${list}.`,
      tiebreaker: {
        completedRuns: 'больше пройденных игр',
        earliestFinalScore: 'раньше набраны итоговые очки',
      } as Readonly<Record<string, string>>,
      then: ', затем — ',
    },
    roll: {
      wheel: 'Колесо выбирает категорию, потом сервер — игру из пула с этим тегом.',
      choice: (n: number) => `После ролла выбираешь одну из ${n} выпавших игр.`,
      free: (n: number) =>
        n === 1
          ? 'Первый реролл после каждого ролла — бесплатно.'
          : `Бесплатных рероллов после каждого ролла: ${n}.`,
      noFree: 'Бесплатных рероллов нет.',
      costCoins: (n: number) => `Дальше реролл стоит ${coins(n)}.`,
      costBadEvent: 'Дальше реролл — за плохой ивент.',
      alreadyPlayed:
        'Проходил игру до ивента — жми «Уже проходил»: новый ролл бесплатный, игра тебе больше не выпадет.',
      busy: 'Занятые и пройденные в сезоне игры не выпадают: колесо покажет промах и выберет другую.',
      unchecked: (n: number) =>
        `Если проверки админом ждут ${runs(n)} или больше, новый ролл закрыт, пока их не станет меньше.`,
      active: (n: number) =>
        n === 1 ? 'Одновременно идёт одно прохождение.' : `Прохождений одновременно: до ${n}.`,
    },
    reward: {
      dice: (hoursPerDie: number, rounding: 'nearest' | 'floor' | 'ceil') =>
        `Один кубик за каждые ${hoursPerDie.toLocaleString('ru-RU')} ч по HowLongToBeat, ${
          rounding === 'nearest'
            ? 'с округлением до ближайшего'
            : rounding === 'floor'
              ? 'с округлением вниз'
              : 'с округлением вверх'
        }.`,
      limits: (min: number, max: number) => `Не меньше ${min} и не больше ${max} кубиков.`,
      example: (hours: number, count: number, sides: number) =>
        `Например, игра на ${hoursText(hours)} на нормальной сложности — ${count}d${sides}.`,
      byDifficulty: 'Кубик зависит от сложности',
      difficulty: 'Сложность',
      die: 'Кубик',
      withEvent: (sides: number, kind: 'good' | 'bad') =>
        `d${sides} и ${kind === 'good' ? 'хороший' : 'плохой'} ивент`,
      normalOrNone: 'Нормальная или сложностей нет',
      points: 'Сумма кубиков идёт и в очки, и в клетки: фишка двигается на столько же.',
      proofFirst: 'Кубы кидаются сразу, пруф проверяется позже. Сложность засчитывается по пруфу.',
      reject: 'Если пруф отклонят, снимутся очки, клетки и монетки за это прохождение.',
      challenge: (n: number) => `Выполнил челлендж из заметки к игре — кубиков сверху: +${n}.`,
      coins: (perHour: number, min: number, capHours: number) =>
        `Монетки: ${coins(perHour)} за час по HowLongToBeat (считается не больше ${capHours.toLocaleString('ru-RU')} ч), минимум — ${coins(min)} за прохождение.`,
    },
    drop: {
      after: (minutes: number) =>
        minutes % 60 === 0
          ? `Дропнуть можно не раньше чем через ${minutes / 60} ч игры — на совести.`
          : `Дропнуть можно не раньше чем через ${minutes} мин игры — на совести.`,
      penalty: (count: number, sides: number, points: boolean, position: boolean) =>
        `Штраф: −${count}d${sides} ${
          points && position
            ? 'очков и клеток'
            : points
              ? 'очков'
              : position
                ? 'клеток'
                : '— ничего не отнимает'
        }.`,
      badEvent: 'И обязательный плохой ивент.',
      floor: 'Назад дальше старта не откатывает. Монеток за дроп нет.',
      tech: (hours: number) =>
        `Тех-реролл — бесплатно, с причиной: слабый ПК, игра платная, не запускается, эмулятор не тянет или другое. Доступен ${hoursText(hours)} после ролла, позже — через админа.`,
    },
    finish: {
      bonus: 'Финишировавшие не первыми один раз получают бонус:',
      place: (place: number) => `${place}-е место`,
      points: (n: number) => `+${n} очк.`,
      after: (place: number) => `${place}-е и дальше`,
      noBonus: 'Бонуса за финиш не первым нет.',
      later: 'Финишировавшие не первыми продолжают набирать очки, но их фишка больше не двигается.',
      surplus: 'Лишние шаги после финиша сгорают.',
    },
    deadline: {
      at: (text: string) => `Дедлайн: ${text}.`,
      none: 'Дедлайн админ ещё не назначил.',
      after:
        'После дедлайна броски кубов закрыты, пруфы принимаются. Засчитывается бросок кубов, сделанный до дедлайна; недопройденная игра не засчитывается.',
      results: 'Итоги — когда админ проверит все пруфы.',
    },
    history: {
      empty: 'Правила ещё не менялись с начала сезона.',
      created: 'Сезон начался с этими правилами',
      unchanged: 'Сохранена без изменений',
      by: (name: string) => `Поменял ${name}`,
      byAdmin: 'Поменял админ',
      version: (n: number) => `Версия ${n}`,
      was: 'Было',
      now: 'Стало',
      yes: 'да',
    },
    fields: {
      'season.maxUncheckedRuns': 'Лимит прохождений на проверке',
      'season.maxActiveRunsPerPlayer': 'Прохождений одновременно',
      'season.timezone': 'Часовой пояс сезона',
      'season.inactiveHintDays': 'Дней без действий до подсказки админу',
      'roll.choiceCount': 'Игр на выбор после ролла',
      'roll.freeRerollsPerRoll': 'Бесплатных рероллов после ролла',
      'roll.rerollCost.kind': 'Чем платить за реролл',
      'roll.rerollCost.amount': 'Цена реролла, монеток',
      'roll.techRerollWindowHours': 'Окно тех-реролла, часов',
      'roll.minPlayMinutesBeforeDrop': 'Минут игры до дропа',
      'reward.diceCount.hoursPerDie': 'Часов на один кубик',
      'reward.diceCount.rounding': 'Округление числа кубиков',
      'reward.diceCount.min': 'Минимум кубиков',
      'reward.diceCount.max': 'Максимум кубиков',
      'reward.dieByDifficulty.easy.sides': 'Кубик на лёгкой',
      'reward.dieByDifficulty.normal.sides': 'Кубик на нормальной',
      'reward.dieByDifficulty.hard.sides': 'Кубик на сложной',
      'reward.dieByDifficulty.extreme.sides': 'Кубик выше сложной',
      'reward.dieByDifficulty.extreme.grantEvent': 'Ивент выше сложной',
      'reward.challengeBonus.extraDice': 'Кубиков за челлендж',
      'reward.coins.perHour': 'Монеток за час',
      'reward.coins.min': 'Минимум монеток за прохождение',
      'drop.penaltyDice.count': 'Штрафных кубиков за дроп',
      'drop.penaltyDice.sides': 'Грани штрафного кубика',
      'drop.affectsPoints': 'Дроп отнимает очки',
      'drop.affectsPosition': 'Дроп отнимает клетки',
      'drop.mandatoryEvent': 'Ивент за дроп',
      'finish.requireApprovalForFirst': 'Первое место — после проверки пруфов',
      'finish.bonusAfterList': 'Бонус за финиш после списка',
      'map.linearLength': 'Клеток до финиша',
    } as Readonly<Record<string, string>>,
    bonusField: (place: number) => `Бонус за финиш: ${place}-е место`,
    values: {
      coins: 'монетки',
      badEvent: 'плохой ивент',
      bad: 'плохой',
      good: 'хороший',
      none: 'нет',
      nearest: 'до ближайшего',
      floor: 'вниз',
      ceil: 'вверх',
      linear: 'линейная',
      graph: 'граф',
    } as Readonly<Record<string, string>>,
  },
  // H8: the admin's pages (/admin/…)
  admin: {
    title: 'Админка',
    nav: 'Разделы админки',
    sectionsButton: 'Разделы',
    waiting: (n: number) => `ждут: ${n}`,
    sections: {
      proofs: 'Пруфы',
      players: 'Игроки',
      log: 'Лог',
      effects: 'Ручные эффекты',
      pool: 'Пул и категории',
      rules: 'Правила',
      season: 'Сезон',
      accounts: 'Аккаунты',
      bugs: 'Отчёты о багах',
      errors: 'Ошибки',
      site: 'Обслуживание',
    },
    leads: {
      proofs: 'Финиши сверху, дальше по времени завершения. Проверь пруф и одобри или отклони.',
      players: 'Очки, монетки, клетка и ход каждого игрока. Правка пишется в лог с комментарием.',
      log: 'Все действия сезона, новые сверху. Откат отменяет действие целиком.',
      effects: 'Ивенты, которые игроки ещё не разыграли. Разыграй за игрока, если он забыл.',
      pool: 'Веса категорий колеса и игры пула.',
      rules: 'Весь конфиг сезона в JSON. Поля проверяются по схеме, пока печатаешь.',
      season: 'Статус и дедлайн сезона, архив, проверка целостности, новый сезон.',
      accounts: 'Логины, имена и роли. Временный пароль показывается один раз.',
      bugs: 'Отчёты с кнопки «Сообщить о баге». Возьми в работу и выгрузи файлом для агента.',
      errors: 'Последние необработанные ошибки сервера с запросом и пользователем.',
      site: 'На время выкладки сайт можно перевести в режим только для чтения.',
    },
    noSeasonTitle: 'Сезона пока нет',
    noSeasonText: 'Создай сезон в разделе «Сезон», и здесь появятся игроки и пруфы.',
    toSeason: 'Открыть «Сезон»',
    loadErrorTitle: 'Не загрузилось',
    failed: 'Не получилось. Попробуй ещё раз.',
    invalid: 'Сервер не принял значения: проверь поля.',
    forbidden: 'Это может только админ. Войди под админским аккаунтом.',
    notFound: 'Этого уже нет. Обнови страницу.',
    comment: 'Комментарий',
    commentHint: 'Попадёт в лог, до 500 символов',
    commentRequired: 'Напиши комментарий: он попадёт в лог.',
    commentTooLong: 'Комментарий — не длиннее 500 символов.',
    system: 'Система',
    save: 'Сохранить',
    edit: 'Изменить',
    // Refusals only the admin meets; the rest are in ru.rejection
    rejection: {
      'account.adminExists': 'Админ уже есть.',
      'account.unknown': 'Аккаунт не найден. Обнови страницу.',
      'map.transferToFinish': 'На финиш переносом нельзя: до него доходят прохождениями.',
      'map.unknownCell': 'Такой клетки нет на карте.',
      'player.alreadyAdded': 'Этот игрок уже в сезоне.',
      'player.busy': 'Игрок сейчас проходит игру: для неё есть дроп и тех-реролл.',
      'player.deltaTooLarge': 'Слишком большое число: правка — не больше ±1 000 000.',
      'player.invalidResource': 'Такого ресурса нет.',
      'player.nothingToChange': 'Правка ничего не меняет.',
      'pool.categoryInvalid': 'Вес — целое число от 1 до 1000, тег — до 50 символов.',
      'account.notAPlayer': 'В сезоне играют только аккаунты с ролью «Игрок».',
      'pool.categoryUnknown': 'Такой категории нет на колесе.',
      'pool.deleted': 'Игра удалена: сначала верни её в пул.',
      'pool.notDeleted': 'Игра и так в пуле.',
      'pool.nothingToChange': 'Ничего не изменилось.',
      'pool.unknown': 'Игра не найдена. Обнови страницу.',
      'run.notTechRerolled': 'У этого прохождения не было тех-реролла.',
      'season.alreadyCreated': 'Такой сезон уже создан.',
      'season.invalidName': 'Название сезона — от 1 до 100 символов.',
      'season.invalidTransition': 'Из этого статуса так перевести сезон нельзя.',
      'season.nothingToChange': 'Ничего не изменилось.',
    } as Readonly<Record<string, string>>,
    proofs: {
      emptyTitle: 'Проверять нечего',
      emptyText: 'Завершённые прохождения появятся здесь сами.',
      count: (n: number) => `Ждут проверки: ${runs(n)}`,
      decidesFinish: 'Решает финиш',
      rollClosed: 'Ролл закрыт лимитом',
      rollClosedSummary: (names: string) =>
        `Ролл закрыт лимитом непроверенных у: ${names}. Проверь их прохождения, чтобы они могли ходить дальше.`,
      noProof: 'Пруф не прислан',
      proofSent: 'Пруф прислан',
      completed: (time: string) => `Завершено ${time}`,
      claimed: (difficulty: string) => `Заявлено: ${difficulty}`,
      noHours: 'часы не указаны',
      dice: (total: number) => `кубы +${total}`,
      witness: (name: string) => `Свидетель: ${name}`,
      note: (text: string) => `Заметка: ${text}`,
      link: (n: number) => `Ссылка ${n}`,
      difficulty: 'Сложность по пруфу',
      difficultyHint: 'Ниже заявленной — кубики пересчитаются',
      approve: 'Одобрить',
      approveWithoutShot: 'Одобрить без скрина',
      withoutShotHint: 'Почему одобряешь без пруфа: попадёт в лог',
      reject: 'Отклонить',
      rejectTitle: (game: string, player: string) => `Отклонить «${game}» (${player})?`,
      // D-98, D-99: what the run really gave is taken back (the frozen first loses nothing); a finish may go
      rejectConsequences: (total: number, decidesFinish: boolean) => [
        `Снимутся очки, клетки и монетки, которые дало это прохождение (кубы: +${total})`,
        ...(decidesFinish
          ? [
              'Если без него игрок не дотягивает до финиша, финиш и место снимутся, места пересчитаются',
            ]
          : []),
        'Игра снова станет доступна для ролла',
      ],
      rejectConfirm: 'Отклонить прохождение',
      rejectCommentHint: 'Что не так с пруфом: игрок увидит этот комментарий',
      approved: (game: string) => `Одобрено: ${game}`,
      rejected: (game: string) => `Отклонено: ${game}`,
    },
    players: {
      emptyTitle: 'В сезоне пока нет игроков',
      emptyText: 'Добавь игрока из аккаунтов — кнопка ниже.',
      hintTitle: 'Давно не ходили',
      hintText:
        'У этих игроков нет своих действий дольше срока из правил. Если игрок выбыл, отметь его неактивным.',
      lastAction: (time: string | null) =>
        time ? `Последнее действие ${time}` : 'Своих действий ещё не было',
      phase: {
        idle: 'Ждёт ролла',
        rolling: 'Выбирает игру',
        playing: 'Проходит игру',
      },
      points: (n: number) => `${n.toLocaleString('ru-RU')} очк.`,
      coins: (n: number) => coins(n),
      cell: (n: number | null) => (n === null ? 'клетка не найдена' : `клетка ${n}`),
      inactive: 'Неактивен',
      markInactive: 'Отметить неактивным',
      markActive: 'Вернуть в игру',
      inactiveTitle: (name: string) => `Отметить ${name} неактивным?`,
      inactiveConsequences: [
        'Игрок выбывает из активной игры: в лидерборде у него пометка «не в игре»',
        'Подсказка о неактивности у него пропадёт',
        'Флаг можно снять в любой момент',
      ],
      inactiveConfirm: 'Отметить неактивным',
      markedInactive: (name: string) => `${name}: отмечен неактивным`,
      markedActive: (name: string) => `${name}: снова в игре`,
      adjust: 'Поправить',
      adjustTitle: (name: string) => `Правка: ${name}`,
      moveTo: 'Перенести на клетку',
      noMove: 'Не переносить',
      cellOption: (n: number, kind: string) => (kind ? `Клетка ${n} — ${kind}` : `Клетка ${n}`),
      cellKinds: { start: 'старт', finish: 'финиш' } as Readonly<Record<string, string>>,
      pointsDelta: 'Очки: добавить или снять',
      coinsDelta: 'Монетки: добавить или снять',
      deltaHint: 'Целое число: 5 добавит, −3 снимет',
      numberInvalid: 'Нужно целое число, например 5 или −3.',
      discardOffer: 'Снять выпавшую игру или выбор: игрок снова ждёт ролла',
      adjustSave: 'Сохранить правку',
      adjusted: (name: string) => `Правка сохранена: ${name}`,
      nothing: 'Правка ничего не меняет: заполни хотя бы одно поле.',
      add: 'Добавить в сезон',
      addTitle: 'Новый игрок в сезоне',
      account: 'Аккаунт',
      accountPick: 'Выбери аккаунт',
      startCell: 'Стартовая клетка',
      startAtStart: 'Старт',
      startPoints: 'Стартовые очки',
      startCoins: 'Стартовые монетки',
      startHint: 'Целое число, 0 — ничего',
      addSubmit: 'Добавить игрока',
      added: (name: string) => `${name} теперь в сезоне`,
      nobodyToAdd: 'Все игроки уже в сезоне. Новый аккаунт — в разделе «Аккаунты».',
      accountRequired: 'Выбери аккаунт.',
      techReroll,
      techRerollTitle: (name: string) => `Тех-реролл за ${name}`,
      techRerollLead:
        'Игра снимается без штрафа, игрок сразу получает новый ролл. Для случаев после окна тех-реролла.',
      techRerollDone: (name: string) => `${name}: тех-реролл сделан`,
    },
    log: {
      emptyTitle: 'Лог пуст',
      emptyText: 'Действия сезона появятся здесь.',
      undone: 'Откатано',
      undo: 'Откатить',
      undoTitle: (label: string) => `Откатить «${label}»?`,
      undoConsequences: (events: number) => [
        `Отменится всё действие целиком: событий — ${events}`,
        'Отмена запишется в лог компенсирующими событиями',
        'Если от действия зависят более поздние, откат откажет и покажет их',
      ],
      undoConfirm: 'Откатить действие',
      undoCommentHint: 'Почему откатываешь: попадёт в лог',
      undoneOk: (label: string) => `Откатано: ${label}`,
      dependents: 'От этого действия зависят более поздние. Сначала откати их:',
      dependentUnknown: 'действие за пределами показанного лога',
      events: (n: number) => `событий: ${n}`,
      commands: {
        CreateSeason: 'Создание сезона',
        ChangeSeasonStatus: 'Смена статуса сезона',
        SetSeasonDeadline: 'Дедлайн',
        ChangeRuleset: 'Правка правил',
        AddSeasonPlayer: 'Игрок добавлен',
        AdjustPlayer: 'Правка игрока',
        SetPlayerInactive: 'Флаг неактивности',
        RollGame: 'Ролл',
        Reroll: 'Реролл',
        MakeChoice: 'Выбор игры',
        DeclareAlreadyPlayed: 'Уже проходил',
        StartRun: 'Начало прохождения',
        CompleteRun: 'Завершение прохождения',
        ReviewRun: 'Отзыв',
        SubmitProof: 'Пруф',
        ApproveProof: 'Одобрение',
        RejectProof: 'Реджект',
        DropRun: 'Дроп',
        TechReroll: techReroll,
        ConvertTechRerollToDrop: 'Тех-реролл в дроп',
        CorrectRunHours: 'Правка часов',
        ChangeRunDifficulty: 'Смена сложности',
        ResolveManualEffect: 'Ручной эффект',
        RecalculateFinishBonuses: 'Пересчёт бонусов',
        UndoCommand: 'Откат',
      } as Readonly<Record<string, string>>,
    },
    effects: {
      emptyTitle: 'Разыгрывать нечего',
      emptyText: 'Ручные эффекты игроков появятся здесь.',
      resolved: (name: string) => `Эффект разыгран: ${name}`,
    },
    pool: {
      categories: 'Категории колеса',
      categoriesLead:
        'Вес — как часто категория выпадает на колесе. «Доступно» — сколько её игр можно выкинуть сейчас.',
      weight: (name: string) => `Вес категории ${name}`,
      available: (n: number) => `доступно ${games(n)}`,
      inPool: (n: number) => `в пуле ${games(n)}`,
      noneAvailable: 'нет доступных игр',
      weightInvalid: 'Вес — целое число от 1 до 1000.',
      weightSaved: (name: string) => `Вес сохранён: ${name}`,
      remove: 'Убрать',
      removeTitle: (name: string) => `Убрать категорию «${name}» с колеса?`,
      removeConsequences: [
        'Колесо перестанет выпадать на эту категорию',
        'Игры и их теги останутся в пуле',
        'Категорию можно добавить снова',
      ],
      removeConfirm: 'Убрать категорию',
      removed: (name: string) => `Категория убрана: ${name}`,
      addCategory: 'Добавить категорию',
      categoryTag: 'Тег',
      categoryTagHint: 'Тег игр из пула, например roguelike',
      categoryWeight: 'Вес',
      tagRequired: 'Напиши тег.',
      categoriesEmptyTitle: 'На колесе нет категорий',
      categoriesEmptyText: 'Добавь категорию, и колесо сможет крутиться.',
      starving: (names: string) =>
        `Следующий ролл не найдёт игр у: ${names}. Добавь игры или категории.`,
      games: 'Игры',
      showDeleted: 'Показать удалённые',
      gamesEmptyText:
        'Загрузи таблицу командой npm run import:xlsx или добавь игры на странице пула.',
      editTitle: (title: string) => `Изменить «${title}»`,
      saved: (title: string) => `Сохранено: ${title}`,
      delete: 'Удалить',
      deleteTitle: (title: string) => `Удалить «${title}» из пула?`,
      deleteConsequences: [
        'Игра больше не выпадет в ролле',
        'Прохождения и отзывы останутся',
        'Игру можно вернуть в пул',
      ],
      deleteConfirm: 'Удалить игру',
      deletedOk: (title: string) => `Удалена: ${title}`,
      restore: 'Вернуть в пул',
      restored: (title: string) => `Снова в пуле: ${title}`,
    },
    rules: {
      editor: 'Конфиг правил (JSON)',
      editorHint: 'Меняй числа и флаги; поля проверяются по схеме',
      save: 'Сохранить правила',
      reset: 'Вернуть как было',
      parseError: (message: string) => `Это не JSON: ${message}`,
      schemaErrors: 'Не проходит схему:',
      serverErrors: 'Сервер не принял правила:',
      required: (path: string) => `${path}: обязательное поле`,
      unknownField: (path: string) => `${path}: такого поля нет`,
      wrongType: (path: string, expected: string) => `${path}: нужно ${expected}`,
      notInList: (path: string, values: string) => `${path}: одно из ${values}`,
      types: {
        integer: 'целое число',
        number: 'число',
        string: 'строка',
        boolean: 'true или false',
        object: 'объект',
        array: 'список',
        null: 'null',
      } as Readonly<Record<string, string>>,
      or: ' или ',
      schemaFailed: 'Схема не загрузилась: правила проверит только сервер при сохранении.',
      saved: (version: number) => `Правила сохранены: версия ${version}.`,
      warnings: {
        'finish.bonusesKept': 'Уже выданные бонусы за финиш не изменятся.',
      } as Readonly<Record<string, string>>,
      unknownWarning: (code: string) => `Предупреждение сервера: ${code}`,
      recalc: 'Пересчитать бонусы по текущим правилам',
      recalcTitle: 'Пересчитать бонусы за финиш?',
      recalcConsequences: [
        'Все финишировавшие перейдут на текущую таблицу бонусов',
        'Очки изменятся на разницу бонусов, пересчёт запишется в лог',
        'Отменить можно откатом в логе',
      ],
      recalcConfirm: 'Пересчитать бонусы',
      bonuses: 'Бонусы за финиш',
      recalcLead:
        'После правки таблицы бонусов финишировавшие остаются на своей. Пересчёт переводит их на текущую.',
      recalculated: 'Бонусы за финиш пересчитаны.',
    },
    season: {
      status: 'Статус',
      current: (name: string) => `Сезон «${name}»`,
      next: {
        draft: {
          label: 'Начать сезон',
          consequences: ['Игроки смогут крутить колесо', 'Правила и карта остаются редактируемыми'],
        },
        active: {
          label: 'Закрыть досрочно',
          consequences: [
            'Броски и роллы закроются сразу, до дедлайна',
            'Пруфы ещё можно присылать и проверять',
            'Итоги — после проверки всех пруфов',
          ],
        },
        closing: {
          label: 'Подвести итоги',
          consequences: [
            'Места станут окончательными',
            'Нужно, чтобы все пруфы были проверены',
            'Ходы больше не принимаются',
          ],
        },
        finished: {
          label: 'Отправить в архив',
          consequences: ['Сезон уйдёт в архив с историей', 'Изменить в нём ничего будет нельзя'],
        },
      },
      nextTitle: (label: string) => `${label}?`,
      moved: (status: string) => `Статус сезона: ${status}`,
      deadline: 'Дедлайн',
      noDeadline: 'Дедлайна нет',
      deadlineField: 'Новый дедлайн (по Москве)',
      deadlineHint: 'Дата и время по Москве',
      deadlineRequired: 'Выбери дату и время.',
      setDeadline: 'Сохранить дедлайн',
      removeDeadline: 'Убрать дедлайн',
      deadlineSaved: 'Дедлайн сохранён.',
      deadlineRemoved: 'Дедлайн убран.',
      transfer: 'Экспорт и импорт',
      exportLead: 'Архив сезона: лог, аккаунты по логинам и пул. Храни его вместе с бэкапами.',
      export: 'Скачать архив сезона',
      importLead:
        'Архив загружается на сервере командой npm run season:import -- <архив>. Из браузера нельзя: так сезон не перезапишется случайно.',
      integrity: 'Проверка целостности',
      integrityLead: 'Сверяет состояние сезона с тем, что получается из лога.',
      check: 'Проверить',
      intact: (sequence: number) =>
        `Всё сходится: состояние совпадает с логом (событий: ${sequence}).`,
      notIntact: 'Состояние расходится с логом:',
      unsettled: 'Очередь ещё обрабатывает команды: проверь через минуту.',
      seasons: 'Все сезоны',
      viewing: 'Открыт в админке',
      openHere: 'Открыть',
      create: 'Новый сезон',
      createLead:
        'Сезон создаётся черновиком с правилами по умолчанию; начнёшь его кнопкой «Начать сезон».',
      name: 'Название',
      createSubmit: 'Создать сезон',
      created: (name: string) => `Сезон создан: ${name}`,
    },
    accounts: {
      emptyTitle: 'Аккаунтов нет',
      emptyText: 'Создай первый аккаунт.',
      create: 'Создать аккаунт',
      loginHint: '2–32 латинские буквы, цифры, точки, дефисы',
      name: 'Имя',
      nameHint: 'Так игрока видят все',
      role: 'Роль',
      roles: { player: 'Игрок', admin: 'Админ', spectator: 'Зритель' },
      created: (login: string) => `Аккаунт ${login} создан.`,
      tempPassword: (login: string) =>
        `Временный пароль для ${login}. Он показывается один раз — передай его игроку:`,
      hidePassword: 'Я передал пароль',
      mustChange: 'Ещё не сменил временный пароль',
      deleted: 'Удалён',
      saved: (name: string) => `Сохранено: ${name}`,
      required: 'Заполни поле.',
      reset: 'Сбросить пароль',
      resetTitle: (name: string) => `Сбросить пароль у ${name}?`,
      resetConsequences: [
        'Текущий пароль перестанет работать',
        'Новый временный пароль покажется один раз',
        'При входе игрок сменит его на свой',
      ],
      resetConfirm: 'Сбросить пароль',
      resetOk: (name: string) => 'Пароль сброшен: ' + name,
      delete: 'Удалить',
      deleteTitle: (name: string) => `Удалить аккаунт ${name}?`,
      deleteConsequences: [
        'Войти под ним больше не получится',
        'Прохождения и история останутся',
        'Аккаунт можно вернуть',
      ],
      deleteConfirm: 'Удалить аккаунт',
      deletedOk: (name: string) => `Аккаунт удалён: ${name}`,
      restore: 'Вернуть',
      restored: (name: string) => `Аккаунт возвращён: ${name}`,
    },
    bugs: {
      filter: 'Показать',
      filters: { new: 'Новые', inWork: 'В работе', closed: 'Закрытые', all: 'Все' },
      statuses: { new: 'Новый', inWork: 'В работе', closed: 'Закрыт' },
      emptyTitle: 'Отчётов нет',
      emptyText: 'Отчёты с кнопки «Сообщить о баге» появятся здесь.',
      from: (author: string, time: string) => `${author}, ${time}`,
      page: (page: string) => `Страница: ${page}`,
      context: 'Контекст',
      actions: 'Последние действия',
      errors: 'Ошибки браузера',
      browser: (agent: string) => `Браузер: ${agent}`,
      viewport: (size: string) => `Окно: ${size}`,
      screenshot: 'Скриншот',
      toWork: 'Взять в работу',
      reopen: 'Открыть снова',
      moved: (status: string) => `Отчёт: ${status}`,
      export: 'Скачать для агента',
    },
    errors: {
      emptyTitle: 'Ошибок нет',
      emptyText: 'Необработанные ошибки сервера появятся здесь.',
      user: (login: string | null) => `Пользователь: ${login ?? 'не вошёл'}`,
      trace: 'Стек вызовов',
      traceId: (id: string) => `Трасса: ${id}`,
    },
    site: {
      maintenance: 'Режим обслуживания',
      on: 'Сейчас сайт только читает: изменения не сохраняются.',
      off: 'Сайт работает как обычно.',
      turnOn: 'Включить обслуживание',
      turnOff: 'Выключить обслуживание',
      onTitle: 'Включить режим обслуживания?',
      onConsequences: [
        'Сайт перестанет принимать изменения: ходы, пруфы, правки',
        'У всех появится баннер обслуживания',
        'Не забудь выключить после выкладки',
      ],
      turnedOn: 'Обслуживание включено.',
      turnedOff: 'Обслуживание выключено.',
    },
  },
  rejection: rejection as Readonly<Record<string, string>> & typeof rejection,
} as const;
