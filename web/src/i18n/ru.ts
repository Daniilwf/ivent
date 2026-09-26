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
  'turn.wrongPhase': 'Это действие сейчас недоступно: обнови страницу.',
  'turn.choicePending': 'Сначала сделай выбор.',
  'turn.noPendingChoice': 'Этот выбор уже сделан или снят. Обнови страницу.',
  'turn.unknownOption': 'Такого варианта нет. Обнови страницу.',
  'roll.noAvailableGames': 'Нет доступных игр для ролла. Сообщи админу.',
  'roll.notEnoughCoins': 'Не хватает монеток на реролл.',
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
    title: 'Твой ход',
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
    option: (title: string, gameHours: number | null) =>
      gameHours == null ? title : `${title} (${hours(gameHours)})`,
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
    inactive: 'не в игре',
    place: (place: number) => `${place} место`,
    points: (points: number) => `${points} очк.`,
    sheet: (place: number, points: number) => `Ты на ${place} месте, ${points} очк.`,
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
      lead: 'Полоса пути до финиша в моём цвете, полосы очков в цвете фишек относительно лидера.',
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
    cell: '·',
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
  rejection: rejection as Readonly<Record<string, string>> & typeof rejection,
} as const;
