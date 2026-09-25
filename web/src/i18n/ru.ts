// The single dictionary of interface texts. Terms come from docs/GLOSSARY.md.

const hours = (value: number) => `${value.toLocaleString('ru-RU')} ч`;

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
  'turn.wrongPhase': 'Это действие сейчас недоступно: обновите страницу.',
  'turn.choicePending': 'Сначала сделайте выбор.',
  'turn.noPendingChoice': 'Этот выбор уже сделан или снят. Обновите страницу.',
  'turn.unknownOption': 'Такого варианта нет. Обновите страницу.',
  'roll.noAvailableGames': 'Нет доступных игр для ролла. Сообщите админу.',
  'roll.notEnoughCoins': 'Не хватает монеток на реролл.',
  'roll.gameNotOffered': 'Эта игра вам сейчас не предложена. Обновите страницу.',
  'proof.invalidLink': 'Ссылка должна начинаться с http:// или https://, ссылок не больше пяти.',
  'proof.empty': 'Добавьте ссылку на пруф или выберите свидетеля.',
  'proof.witnessInvalid': 'Свидетелем может быть только другой игрок сезона.',
  'proof.alreadyReviewed': 'Пруф уже проверен.',
  'proof.invalidFile': 'Можно прикрепить до 5 своих разных скринов.',
  'finish.nothingToRecalculate':
    'Пересчитывать нечего: у финишировавших уже бонусы по текущим правилам.',
  'proof.difficultyAboveClaimed': 'По пруфу нельзя поднять заявленную сложность.',
  'run.techRerollWindowClosed': 'Окно тех-реролла после ролла закрылось. Обратитесь к админу.',
  'run.reasonCommentRequired': 'Для причины «Другое» нужен комментарий.',
  'run.hoursRequired': 'У игры нет данных о длине: укажите оценку часов.',
  'run.invalidHours': 'Часы должны быть больше нуля.',
  'run.hoursSourceTooLong': 'Источник оценки не длиннее 300 символов.',
  'feature.disabled': 'Эта возможность выключена в правилах сезона.',
  'run.hoursSourceRequired': 'Укажите, откуда оценка часов: ссылку или короткую пометку.',
  'run.notYours': 'Это не ваше прохождение.',
  'run.notCompleted': 'Это прохождение ещё не завершено.',
  'run.nothingToChange': 'Значение уже такое: менять нечего.',
  'run.unknown': 'Прохождение не найдено. Обновите страницу.',
  'review.invalidRating': 'Оценка — от 1 до 10.',
  'review.tooLong': 'Отзыв слишком длинный: не больше 2000 символов.',
  'season.closed': 'Сезон закрыт для этого действия.',
  'effect.notPending': 'Этот эффект уже разрешён.',
  'account.mustChangePassword': 'Сначала смените временный пароль.',
  'account.currentPasswordWrong': 'Текущий пароль введён неверно.',
  'account.repeat':
    'Этот запрос уже выполнен. Если пароль ещё нужно сменить — отправьте форму заново.',
  'account.stale': 'Аккаунт изменился, пока шёл запрос. Войдите заново.',
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
  'undo.notUndoable': 'Создание сезона и сам откат не откатываются — сделайте действие заново.',
  'undo.alreadyUndone': 'Это действие уже откатано.',
  'undo.dependents':
    'От этого действия зависят более поздние: сначала откатите их или поправьте вручную.',
  'undo.noHistory': 'Откат сейчас недоступен. Попробуйте ещё раз.',
  'command.undone': 'Это действие откатили. Сделайте его заново.',
  'season.deadlineInPast': 'Дедлайн должен быть в будущем.',
  'player.commentRequired': 'Нужен комментарий.',
  'player.commentTooLong': 'Комментарий слишком длинный: не больше 500 символов.',
  'effect.notYours': 'Это не ваш эффект.',
  'effect.unknownOutcome': 'Неизвестный исход.',
  'season.deadlinePassed': 'Дедлайн прошёл: броски закрыты, пруфы принимаются.',
  'season.deadlineNotReached': 'Дедлайн ещё не наступил.',
  'season.proofsPending': 'Сначала проверьте все пруфы: итоги — только после проверки.',
  'season.notActive': 'Сезон сейчас не идёт.',
  'player.finished': 'Позиция финишировавшего меняется только через его прохождения.',
  'player.unknown': 'Вы не участвуете в этом сезоне.',
  'season.notCreated': 'Сезон ещё не создан.',
  'season.mismatch': 'Действие отправлено не в тот сезон. Обновите страницу.',
  'command.idReused': 'Действие уже выполнялось. Обновите страницу.',
  'command.invalid': 'Действие не удалось разобрать. Обновите страницу и повторите.',
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
  password: {
    title: 'Смена пароля',
    why: 'Вы вошли с временным паролем. Придумайте свой — дальше вход будет с ним.',
    current: 'Временный пароль',
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
        ? 'За этот реролл вам достанется плохой ивент. Продолжить?'
        : `Реролл стоит ${coins(price)}. Потратить?`,
    rerollConfirmYes: 'Да, реролл',
    rerollConfirmNo: 'Отмена',
    drop: 'Дроп',
    dropConfirm: (penalty: DropPenalty | null) => {
      if (!penalty) return 'Дропнуть игру?';
      const takes = [
        penalty.affectsPoints ? 'очки' : null,
        penalty.affectsPosition ? 'клетки' : null,
      ]
        .filter((part) => part !== null)
        .join(' и ');
      const parts = [
        takes === ''
          ? 'штрафа кубами нет'
          : `штраф — кубы за дроп (${String(penalty.count)}d${String(penalty.sides)}) отнимут ${takes}`,
        penalty.badEvent ? 'вам достанется плохой ивент' : null,
      ].filter((part) => part !== null);
      return `Дроп: ${parts.join(', ')}. Дропнуть игру?`;
    },
    dropHint: (minutes: number) =>
      `По правилам дропать стоит не раньше чем через ${minutes} мин. игры.`,
    dropConfirmYes: 'Да, дропнуть',
    dropConfirmNo: 'Отмена',
    techReroll: 'Тех-реролл',
    techRerollReason: 'Причина',
    techRerollReasonPlaceholder: 'Выберите причину',
    techRerollReasons: {
      weakPc: 'Слабый ПК',
      paidUnavailable: 'Игра платная, её нет',
      doesNotLaunch: 'Не запускается',
      emulatorTooSlow: 'Эмулятор не тянет',
      other: 'Другое',
    },
    techRerollComment: 'Комментарий',
    techRerollCommentRequired: 'Для причины «Другое» напишите комментарий.',
    techRerollSubmit: 'Тех-реролл',
    techRerollCancel: 'Отмена',
    techRerollClosed: 'Окно тех-реролла закрылось. Если игра не запускается, напишите админу.',
    gameMark: (player: string, kind: 'dropped' | 'techRerolled') =>
      kind === 'dropped' ? `Дропнул ${player}` : `Тех-реролл у ${player}`,
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
    hoursSource: 'Источник оценки',
    hoursSourceHint: 'Ссылка, например на HowLongToBeat, или короткая пометка.',
    hoursSourceRequired: 'Укажите источник оценки: ссылку или пометку.',
    challengeDone: 'Челлендж выполнен',
    reviewRating: 'Оценка игры',
    reviewNoRating: 'Без отзыва',
    reviewText: 'Отзыв',
    reviewRatingRequired: 'Чтобы оставить отзыв, поставьте оценку от 1 до 10.',
    lastRejected: (title: string) => `Прохождение отклонено (${title}): очки и клетки сняты.`,
    lastChallengeDice: (dice: number[]) => `Кубы за челлендж: ${dice.join(' + ')}`,
    lastReview: (rating: number, text: string | null) =>
      text
        ? `Ваш отзыв: ${rating.toString()}/10 — ${text}`
        : `Ваша оценка: ${rating.toString()}/10`,
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
  proof: {
    title: 'Пруф',
    link: 'Ссылка на скрин или видео',
    addLink: 'Ещё ссылка',
    note: 'Заметка',
    submit: 'Отправить пруф',
    linkRequired: 'Добавьте ссылку или скрин либо выберите свидетеля.',
    shot: 'Скрин (картинка до 15 МБ, GIF до 8 МБ)',
    shotAlt: (n: number) => `Скрин ${n}`,
    removeShot: (n: number) => `Убрать скрин ${n}`,
    remove: 'Убрать',
    witness: 'Свидетель (видел прохождение)',
    noWitness: 'Без свидетеля',
    linkInvalid: 'Ссылка должна начинаться с http:// или https://.',
    status: {
      pending: 'Пруф ждёт проверки админом.',
      approved: 'Пруф одобрен.',
      rejected: 'Пруф отклонён.',
    },
    reviewComment: (comment: string) => `Комментарий админа: ${comment}`,
  },
  upload: {
    uploading: 'Загружаем…',
    failed: 'Не удалось загрузить файл. Попробуйте ещё раз.',
    errors: {
      'file.typeInvalid': 'Это не картинка: подходят JPEG, PNG, WebP и GIF.',
      'file.broken': 'Картинка повреждена или обрезана.',
      'file.tooLarge': 'Файл слишком большой: картинка до 15 МБ, GIF до 8 МБ.',
      'file.tooManyPixels': 'Картинка слишком большого разрешения.',
      'file.tooManyFrames': 'В GIF слишком много кадров.',
      'file.dailyLimit': 'На сегодня лимит загрузок исчерпан.',
      'file.busy': 'Сервер занят другими картинками. Попробуйте через минуту.',
    },
  },
  effects: {
    title: 'Нужно разыграть',
    drawEvent: (kind: 'good' | 'bad', source: keyof typeof effectSources) =>
      `${kind === 'bad' ? 'Плохой ивент' : 'Хороший ивент'} ${effectSources[source]}`,
    comment: 'Комментарий',
    applied: 'Применено',
    notApplicable: 'Не применимо',
    commentNeeded: 'Для «не применимо» напишите комментарий',
  },
  map: {
    title: 'Карта',
    start: 'Старт',
    finish: 'Финиш',
    cell: '·',
  },
  season: {
    deadline: (text: string) => `Дедлайн: ${text} МСК`,
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
  rejection: rejection as Readonly<Record<string, string>> & typeof rejection,
} as const;
