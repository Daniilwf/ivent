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
        penalty.badEvent ? 'тебе достанется плохой ивент' : null,
      ].filter((part) => part !== null);
      return `Дроп: ${parts.join(', ')}. Дропнуть игру?`;
    },
    dropHint: (minutes: number) =>
      `По правилам дропать стоит не раньше чем через ${minutes} мин. игры.`,
    dropConfirmYes: 'Да, дропнуть',
    dropConfirmNo: 'Отмена',
    techReroll: 'Тех-реролл',
    techRerollReason: 'Причина',
    techRerollReasonPlaceholder: 'Выбери причину',
    techRerollReasons: {
      weakPc: 'Слабый ПК',
      paidUnavailable: 'Игра платная, её нет',
      doesNotLaunch: 'Не запускается',
      emulatorTooSlow: 'Эмулятор не тянет',
      other: 'Другое',
    },
    techRerollComment: 'Комментарий',
    techRerollCommentRequired: 'Для причины «Другое» напиши комментарий.',
    techRerollSubmit: 'Тех-реролл',
    techRerollCancel: 'Отмена',
    techRerollClosed: 'Окно тех-реролла закрылось. Если игра не запускается, напиши админу.',
    gameMark: (player: string, kind: 'dropped' | 'techRerolled') =>
      kind === 'dropped' ? `Дропнул ${player}` : `Тех-реролл у ${player}`,
    alreadyPlayedGame: (title: string) => `Уже проходил: ${title}`,
    choose: 'Выбери одну из выпавших игр',
    pick: 'Выбрать:',
    pickShort: 'Выбрать',
    rolled: 'Выпала игра',
    playing: (title: string) => `Сейчас играешь: ${title}`,
    complete: 'Завершить',
    difficulty: 'Сложность',
    hours: 'Часы (оценка)',
    hoursHint: 'У игры нет данных о длине: укажи оценку.',
    hoursInvalid: 'Укажи число часов больше нуля.',
    hoursSource: 'Источник оценки',
    hoursSourceHint: 'Ссылка, например на HowLongToBeat, или короткая пометка.',
    hoursSourceRequired: 'Укажи источник оценки: ссылку или пометку.',
    challengeDone: 'Челлендж выполнен',
    reviewRating: 'Оценка игры',
    reviewNoRating: 'Без отзыва',
    reviewText: 'Отзыв',
    reviewRatingRequired: 'Чтобы оставить отзыв, поставь оценку от 1 до 10.',
    lastRejected: (title: string) => `Прохождение отклонено (${title}): очки и клетки сняты.`,
    lastChallengeDice: (dice: number[]) => `Кубы за челлендж: ${dice.join(' + ')}`,
    lastReview: (rating: number, text: string | null) =>
      text
        ? `Твой отзыв: ${rating.toString()}/10 — ${text}`
        : `Ваша оценка: ${rating.toString()}/10`,
    lastDice: (title: string, dice: number[], total: number) =>
      `Кубы за прохождение (${title}): ${dice.join(' + ')} — итого ${total.toLocaleString('ru-RU')}`,
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
    link: 'Ссылка на скрин или видео',
    addLink: 'Ещё ссылка',
    note: 'Заметка',
    submit: 'Отправить пруф',
    linkRequired: 'Добавь ссылку или скрин либо выбери свидетеля.',
    shot: 'Скрин (JPEG, PNG, WebP или GIF)',
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
  avatar: {
    title: 'Моя аватарка',
    current: 'Текущая аватарка',
    none: 'Аватарки пока нет.',
    file: 'Картинка с устройства',
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
      result: (plain: number[], challenge: number | null, total: number) =>
        `${plain.join(' + ')}${challenge === null ? '' : ` + ${challenge} за челлендж`}. Итого +${total}: столько клеток вперёд и очков`,
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
      ['dialogs', 'Окна'],
      ['map', 'Карта'],
      ['wheel', 'Колесо'],
      ['dice', 'Кубики'],
      ['move', 'Ход фишки'],
      ['finish', 'Финиш'],
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
      dropConsequences: [
        '−2d4 очков и клеток, но не ниже чекпоинта',
        'Плохой ивент',
        'Игра станет недоступна тебе в этом сезоне',
      ],
    },
    dialogs: {
      title: 'Окна',
      lead: 'Подтверждение опасного действия показывает последствия. Лидерборд на телефоне выезжает снизу.',
      form: 'Форма поверх страницы',
    },
    // H6, H7: new sections of the styleguide
    choices: {
      title: 'Выбор',
      lead: 'Список — системный выбор телефона; несколько коротких вариантов — ряд пилюль-радиокнопок. Второй ряд — с фокусом.',
      list: 'Категория',
      pills: 'Длина по HowLongToBeat',
    },
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
  // H6, H7: the sections of the site in the header
  nav: {
    label: 'Разделы',
    home: 'Главная',
    pool: 'Пул игр',
    rules: 'Правила',
  },
  // H6: the pool of games (SPEC «Пул игр», «Статусы игры в сезоне», «Дубли»)
  pool: {
    title: 'Пул игр',
    found: (n: number, total: number) =>
      n === total ? `В пуле ${games(total)}` : `Нашлось ${games(n)} из ${total}`,
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
    hours: (h: number) => `${h.toLocaleString('ru-RU')} ч`,
    noHours: 'Часов нет',
    coop: 'Кооп',
    author: (name: string) => `Добавил ${name}`,
    free: 'Свободна',
    offWheel: 'Не выпадет: ни одной её категории нет на колесе',
    listLabel: 'Игры пула',
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
    loadErrorText: 'Сервер не ответил. Проверь интернет и попробуй ещё раз.',
    statusError: 'Статусы игр в сезоне не загрузились: видно только сам пул.',
    added: (title: string) => `«${title}» теперь в пуле.`,
    form: {
      title: 'Новая игра',
      name: 'Название',
      nameHint: 'Как в Steam или на HowLongToBeat',
      nameRequired: 'Напиши название.',
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
    rejection: {
      'pool.duplicate': 'Такая игра уже есть в пуле.',
      'pool.similar': 'В пуле есть похожее название: проверь и подтверди.',
      'pool.cardInvalid': 'Проверь поля: что-то заполнено не так.',
      'pool.coverUnknown': 'Обложка не найдена. Загрузи её ещё раз.',
    } as Readonly<Record<string, string>>,
  },
  // H7: the rules page, built from the season's current ruleset (SPEC «Правила на сайте»)
  rules: {
    title: 'Правила сезона',
    lead: 'Все числа здесь — из текущих правил сезона. Поменяет админ — поменяются и тут; уже выпавшая игра живёт по правилам на момент своего ролла.',
    version: (n: number) => `Версия правил ${n}`,
    loadErrorTitle: 'Правила не загрузились',
    loadErrorText: 'Сервер не ответил. Проверь интернет и попробуй ещё раз.',
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
        `Например, игра на ${hours} ч на нормальной сложности — ${count}d${sides}.`,
      byDifficulty: 'Кубик зависит от сложности',
      difficulty: 'Сложность',
      die: 'Кубик',
      withEvent: (sides: number, kind: 'good' | 'bad') =>
        `d${sides} и ${kind === 'good' ? 'хороший' : 'плохой'} ивент`,
      difficulties: {
        easy: 'Лёгкая',
        normal: 'Нормальная или сложностей нет',
        hard: 'Сложная',
        extreme: 'Выше сложной',
      },
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
        `Тех-реролл — бесплатно, с причиной: слабый ПК, игра платная, не запускается, эмулятор не тянет или другое. Доступен ${hours} ч после ролла, позже — через админа.`,
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
      none: 'нет',
      yes: 'да',
      no: 'нет',
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
  rejection: rejection as Readonly<Record<string, string>> & typeof rejection,
} as const;
