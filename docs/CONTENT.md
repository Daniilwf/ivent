# Гайд по контенту

Как описывать предметы, эффекты, спецроллы, ивенты, достижения, зоны, клетки, голосования и челленджи. Формат — ориентир для движка. Агент может уточнить его через `docs/DECISIONS.md`, но **каждый пример из этого файла должен загружаться и работать ровно как описано: это приёмочные тесты движка**.

## Определение объекта

```json
{
  "id": "orange",
  "kind": "item",
  "name": "Апельсин",
  "description": "Иди на 1 клетку вперёд.",
  "rarity": "common",
  "price": 10,
  "window": "anytime",
  "hostile": false,
  "stackable": true,
  "effect": {
    "target": { "selector": "self" },
    "actions": [{ "type": "move", "steps": 1 }]
  }
}
```

| Поле | Значения |
| --- | --- |
| `kind` | `item`, `effect`, `specialRoll`, `event`, `achievement` |
| `rarity` | `common`, `epic`, `legendary` |
| `price` | цена в магазине; `null` — не продаётся |
| `window` | для предметов: `beforeRoll`, `afterRoll`, `beforeDice`, `afterDice`, `anytime` |
| `hostile` | эффект против другого игрока: учитывается перехватом, лимитами и статистикой атак |
| `stackable` | можно ли применить несколько одинаковых за раз |
| `manual` | `true` — автоматики нет, эффект уходит в ручное разрешение |
| `proof` | `media` — для IRL-ивентов нужен фото- или видеопруф |
| `useCondition` | только у предметов: условие, при котором предмет можно использовать (см. «Условия»), иначе отказ `item.conditionNotMet` (D-483) |
| `unstealable`, `undestroyable` | эффекты не забирают, не крадут и не передают объект / не уничтожают и не превращают его; правка админа действует (D-478) |
| `tags` | теги самого объекта: `edible`, `rat`, `gremlin`, `pig`, `weapon`, `potion`…; строчные слова через дефис, каждое один раз. Нужны фильтрам вроде «не крадёт крысиные» (`excludeTags`) и лутбоксам; это не теги игр (D-470) |
| `effect` | что происходит; у объектов с `manual: true` может отсутствовать |

## Пакет контента и файлы

Контент сезона — пакет: объекты и колёса лутбоксов. В репозитории объект лежит файлом `content/items/<id>.json`, колесо — `content/wheels/<id>.json`; админ загружает пакет в сезон целиком (D-400). Пакет проверяется весь сразу, каждая ошибка — с путём: формат объектов (ниже), ссылки `giveObject`, `transformObject` и `spinWheel` на объекты и колёса того же пакета, и то, чего ещё нет в сборке (ивенты, достижения и условия прохождения — со своими этапами, D-402). JSON-схема пакета — `docs/content.schema.json`, генерируется из типов движка. Колесо лутбокса — `id`, `name` и `entries`: записи `{ "objectId": "orange", "weight": 30 }`; действие `spinWheel` выбирает запись по весу и выдаёт объект (пример — `content/wheels/lootbox.json`). **Колода ивентов** (этап 5, D-501) — то же колесо с полем `deck`: `good`, `bad`, `special` или `risky` — вид её ивентов; записи — только объекты `kind: event`, а в колесе лутбокса ивентов нет. `drawEvent` с `deck` = `good`/`bad`/`special`/`risky` тянет из стандартной колоды с таким `id` (колесо с таким `id` обязано быть колодой этого вида), `zone` — из колоды зоны клетки игрока или `events.defaultDeck` (D-502), иначе — из колоды пакета по `id`. Вытянутый ивент разыгрывается сразу на вытянувшего (D-500); колоды нет или в ней нет живых записей — ручной эффект «разыграй ивент» этого вида. Ивент не вытягивает другой ивент (D-531). Ивенты лежат файлами `content/events/<id>.json`, колоды — `content/decks/<id>.json` (D-533); пакет с ивентами и колодами принимается только при `features.events` (D-532).

Объект, которого нет в новой версии пакета, не продаётся и не выдаётся, но у игроков продолжает работать. Предмет с автоматикой используется командой «Использовать» с целью (для `chosen`) и ответами на `requestChoice` сразу: сервер проверяет их по своим вариантам (D-407).

## Блок `effect`

| Поле | Значения |
| --- | --- |
| `target` | `selector`: `self`, `chosen`, `randomActive`, `all`, `leader`, `higherPoints`, `lowerPoints`, `withinCells`, `nearestBehind`; `among` — ограничить выбор для `chosen` и `randomActive`: `higherPoints`, `lowerPoints`; `excludeSelf` — исключить себя; у `withinCells` — `cells`: все в пределах стольких клеток по рёбрам карты в обе стороны, стоящие на клетке использующего — при `includeSameCell`; `nearestBehind` — ближайший позади по расстоянию до финиша, при равенстве — все (D-480) |
| `trigger` | для эффектов: `beforeRoll`, `afterRoll`, `runCompleted`, `beforeDice`, `afterDice`, `moveStep`, `stop`, `pass`, `drop`, `techReroll`, `time`, `hostileIncoming`, `runEnded` (прохождение или дроп, D-479) |
| `duration` | `{ "uses": N }`, `{ "hours": N }`, `{ "runs": N }` или `{ "untilTriggered": true }` |
| `condition` | условие срабатывания, см. «Условия» |
| `intercept` | `hostile` — эффект гасит входящий враждебный эффект (пассивная защита) |
| `actions` | список базовых действий, выполняются по порядку |
| `counter` | `{ "start": 3 }` — обратный отсчёт эффекта или спецролла: каждое срабатывание триггера уменьшает счётчик экземпляра на 1 вместо действий, на нуле действия выполняются (D-479) |
| `outcomes` | случайное ветвление: `{ "roll": "1d6", "cases": [{ "from": 1, "to": 3, "actions": [...] }] }`; или по выбору игрока (D-482): `{ "choice": { "prompt": "…", "options": { "list": ["а", "б"] } }, "cases": [{ "option": "а", "actions": [...] }] }` — по случаю на каждый вариант, ответ даётся с использованием |

**Значения** в действиях: целое число, строка с кубиками (`"1d6"`, `"2d4"`, `"-1d6"`) или ссылка: `"$roll"` — результат последнего броска в этом эффекте, `"$choice"` — последний выбор игрока, `"$result"` — итог голосования, `"$имя"` — параметр, переданный через `params` в `giveObject`, `"-$roll"` — со знаком минус.

**Цель действия**: по умолчанию цель эффекта; поле `"target": "self"` у действия переопределяет её. При нескольких целях действие на себя выполняется один раз за использование (D-480).

## Базовые действия

| `type` | Параметры |
| --- | --- |
| `move` | `steps`: число, кубики или ссылка; минус — назад; `direction` (D-477): `chosen` — игрок отвечает `forward` или `back` (интерфейс называет их своими словами), `away` — от использующего: вперёд, если цель ближе к финишу, назад, если дальше, на равном расстоянии — на месте |
| `changeResource` | `resource`: `points`, `coins` или свой ключ; `amount`; `reflect: true` — монетки, снятые с цели, у которой их не больше `effects.reflect.coinsAtMost`, снимаются с использующего, умноженные на `effects.reflect.multiplier` (D-474) |
| `roll` | `dice`: результат доступен как `$roll` |
| `giveObject` | `objectId`, `params` для параметризованных объектов |
| `takeObject` | `mode`: `take`, `steal`, `destroy`, `give` (отдать свой объект цели; нечего отдать — отказ `item.nothingToGive`, D-478); `filter`; `pick`: `random` или `chosen` (ответ — id экземпляра); `count`: сколько объектов — число от 1 или ссылка (`$roll`), `all: true` — все подходящие (D-476). При `chosen` число просит ответ на каждый объект, а ссылка забирает все оставшиеся ответы как список по порядку, недостающие берутся случайно |
| `transformObject` | `mode`: `transform` (`into`), `annotate` (`note`), `addCounter` (`value`: на сколько меняются счётчики всех подходящих; дошедший до нуля срабатывает сразу) или `resetCounters` (счётчики всех подходящих — к началу), D-479 |
| `drawEvent` | `deck`: `good`, `bad`, `special`, `risky`, `zone` или id колоды |
| `spinWheel` | `wheel`: id колеса |
| `modifyNextRoll` | `filter`, `choiceCount`, `runCondition` |
| `modifyDice` | `when`: `current` или `next`; `stage`: `count`, `sides`, `add`, `multiply`, `reroll`, `min`, `max`, `singleDie`, `setOne`, `replaceValue`, `fixAverage`, `bonusPerEven`, `percent`; `value`; у `count` — `sides`: дополнительные кубики своего вида (d6 рядом с d4 прохождения, D-475). Редкие этапы (D-481): `singleDie` — вместо кубиков по часам один кубик на `value` граней; `setOne` — первый кубик показывает `value` (число или `$choice`), не больше своих граней; `replaceValue` — каждый кубик с `from` считается за `value`; `fixAverage` — каждый кубик показывает половину граней; `bonusPerEven` — `value` за каждый чётный кубик; `percent` — сумма с модификаторами × `value`/100 с округлением к нулю; у `reroll` — `filter: belowAverage`: перебросить только кубики ниже половины граней |
| `teleport` | `cell`: id клетки или `nearestShortcut` |
| `requestChoice` | `prompt`, `options`: `{ "from": "categories" }`, `players`, `games` или `{ "list": [...] }`; результат — `$choice` |

## Условия и фильтры

| Условие | Пример |
| --- | --- |
| Фильтр игры | `{ "tags": ["Horror"], "maxHours": 10, "minHours": 2, "releaseYearBefore": 2005 }` |
| Фильтр объектов (`takeObject`, `transformObject`) | `{ "kind": "item", "rarity": "epic", "objectId": "orange", "hostile": true, "hasCounter": true, "excludeTags": ["rat"] }`; `excludeTags` пропускает объекты с любым из этих тегов (D-470), `hasCounter` — только объекты со счётчиком или без (D-479) |
| Условие на прохождение | `{ "difficultyAtLeast": "hard" }` |
| Статистика игрока | `{ "stat": "hostileReceived", "gte": 5 }`, `{ "stat": "completedStreakWithTag", "tag": "Horror", "gte": 3 }`, `{ "stat": "allDiceMax", "minDice": 3 }`, `{ "stat": "runHours", "gte": 30 }`, `{ "stat": "coins", "gte": 0, "lte": 0 }` (монетки владельца: `gte` и/или `lte`, D-483) |

Набор статистик расширяется по мере надобности; каждая новая статистика — отдельная задача с тестом.

## Примеры предметов

### 1. Апельсин — шаг вперёд

```json
{
  "id": "orange", "kind": "item", "name": "Апельсин", "description": "Иди на 1 клетку вперёд.",
  "rarity": "common", "price": 10, "window": "anytime", "hostile": false, "stackable": true,
  "effect": { "target": { "selector": "self" }, "actions": [{ "type": "move", "steps": 1 }] }
}
```

### 2. Счастливый кубик — +1d6 к следующему броску

```json
{
  "id": "lucky-die", "kind": "item", "name": "Счастливый кубик",
  "description": "Добавь 1d6 к следующему броску за прохождение.",
  "rarity": "common", "price": 15, "window": "beforeDice", "hostile": false, "stackable": false,
  "effect": {
    "target": { "selector": "self" },
    "actions": [{ "type": "modifyDice", "when": "next", "stage": "add", "value": "1d6" }]
  }
}
```

### 3. Переброс — перебросить только что выпавшие кубы

```json
{
  "id": "reroll-dice", "kind": "item", "name": "Переброс",
  "description": "Перебрось кубы за прохождение, результат нового броска окончательный.",
  "rarity": "epic", "price": 30, "window": "afterDice", "hostile": false, "stackable": false,
  "effect": {
    "target": { "selector": "self" },
    "actions": [{ "type": "modifyDice", "when": "current", "stage": "reroll", "value": 1 }]
  }
}
```

### 4. Щит — предмет выдаёт эффект-перехват

```json
{
  "id": "shield", "kind": "item", "name": "Щит",
  "description": "Следующий враждебный эффект против тебя не сработает.",
  "rarity": "epic", "price": 25, "window": "anytime", "hostile": false, "stackable": false,
  "effect": {
    "target": { "selector": "self" },
    "actions": [{ "type": "giveObject", "objectId": "shield-effect" }]
  }
}
```

```json
{
  "id": "shield-effect", "kind": "effect", "name": "Под щитом",
  "description": "Гасит следующий враждебный эффект.",
  "rarity": "epic", "price": null, "hostile": false,
  "effect": { "trigger": "hostileIncoming", "intercept": "hostile", "duration": { "uses": 1 }, "actions": [] }
}
```

### 5. Купон реролла — бесплатный реролл игры

```json
{
  "id": "reroll-coupon", "kind": "item", "name": "Купон реролла",
  "description": "Даёт один бесплатный реролл игры.",
  "rarity": "common", "price": 12, "window": "anytime", "hostile": false, "stackable": true,
  "effect": {
    "target": { "selector": "self" },
    "actions": [{ "type": "changeResource", "resource": "freeRerolls", "amount": 1 }]
  }
}
```

### 6. Выбор из трёх

```json
{
  "id": "pick-of-three", "kind": "item", "name": "Выбор из трёх",
  "description": "Следующий ролл покажет три игры, выберешь одну.",
  "rarity": "epic", "price": 25, "window": "beforeRoll", "hostile": false, "stackable": false,
  "effect": {
    "target": { "selector": "self" },
    "actions": [{ "type": "modifyNextRoll", "choiceCount": 3 }]
  }
}
```

### 7. Короткая игра

```json
{
  "id": "short-game", "kind": "item", "name": "Короткая игра",
  "description": "Следующий ролл — только игры короче 10 часов.",
  "rarity": "common", "price": 20, "window": "beforeRoll", "hostile": false, "stackable": false,
  "effect": {
    "target": { "selector": "self" },
    "actions": [{ "type": "modifyNextRoll", "filter": { "maxHours": 10 } }]
  }
}
```

### 8. Подлянка — выбрать жанр следующей игры тому, у кого больше очков

```json
{
  "id": "dirty-trick", "kind": "item", "name": "Подлянка",
  "description": "Выбери игрока с большим числом очков и жанр его следующей игры.",
  "rarity": "epic", "price": 30, "window": "anytime", "hostile": true, "stackable": false,
  "effect": {
    "target": { "selector": "chosen", "among": "higherPoints" },
    "actions": [
      { "type": "requestChoice", "prompt": "Какой жанр подкинуть?", "options": { "from": "categories" } },
      { "type": "giveObject", "objectId": "forced-genre", "params": { "tag": "$choice" } }
    ]
  }
}
```

```json
{
  "id": "forced-genre", "kind": "specialRoll", "name": "Навязанный жанр",
  "description": "Следующая игра будет из выбранного жанра.",
  "rarity": "common", "price": null, "hostile": true,
  "effect": { "trigger": "beforeRoll", "duration": { "uses": 1 }, "actions": [{ "type": "modifyNextRoll", "filter": { "tags": ["$tag"] } }] }
}
```

### 9. Проклятие — −1d6 к следующему броску цели

```json
{
  "id": "curse", "kind": "item", "name": "Проклятие",
  "description": "У выбранного игрока следующий бросок за прохождение уменьшится на 1d6, но не ниже 1.",
  "rarity": "legendary", "price": 45, "window": "anytime", "hostile": true, "stackable": false,
  "effect": {
    "target": { "selector": "chosen", "excludeSelf": true },
    "actions": [{ "type": "giveObject", "objectId": "curse-effect" }]
  }
}
```

```json
{
  "id": "curse-effect", "kind": "effect", "name": "Проклят",
  "description": "Следующий бросок за прохождение −1d6, минимум 1.",
  "rarity": "legendary", "price": null, "hostile": true,
  "effect": {
    "trigger": "beforeDice", "duration": { "uses": 1 },
    "actions": [
      { "type": "modifyDice", "when": "current", "stage": "add", "value": "-1d6" },
      { "type": "modifyDice", "when": "current", "stage": "min", "value": 1 }
    ]
  }
}
```

### 10. Птичкерс — украсть 1d4 монеток

```json
{
  "id": "bird-thief", "kind": "item", "name": "Птичкерс",
  "description": "Укради у выбранного игрока 1d4 монеток.",
  "rarity": "common", "price": 15, "window": "anytime", "hostile": true, "stackable": false,
  "effect": {
    "target": { "selector": "chosen", "excludeSelf": true },
    "actions": [
      { "type": "roll", "dice": "1d4" },
      { "type": "changeResource", "resource": "coins", "amount": "-$roll" },
      { "type": "changeResource", "resource": "coins", "amount": "$roll", "target": "self" }
    ]
  }
}
```

## Примеры расширений формата

Расширения, добавленные при импорте пакета предметов старой таблицы (`content/imports/old-items`, очередь в `docs/PROGRESS.md`). Каждый пример — файл `content/items` как есть.

### 11. Гремлин — уничтожить предмет на выбор, кроме гремлинских

```json
{
  "id": "gremlin", "kind": "item", "name": "Гремлин",
  "description": "Уничтожь предмет на выбор у любого игрока, хоть у себя. Плата — 1 монетка. Гремлинские предметы гремлину не по зубам.",
  "rarity": "common", "price": null, "window": "anytime", "hostile": true, "stackable": false, "tags": ["gremlin"],
  "effect": {
    "target": { "selector": "chosen" },
    "actions": [
      { "type": "takeObject", "mode": "destroy", "pick": "chosen", "filter": { "kind": "item", "excludeTags": ["gremlin"] } },
      { "type": "changeResource", "resource": "coins", "amount": -1, "target": "self" }
    ]
  }
}
```

### 12. Граната — урон монетками отражается от бедной цели

```json
{
  "id": "grenade", "kind": "item", "name": "Граната",
  "description": "Оружие. Выбранный другой игрок идёт на 1 клетку назад и теряет 2 монетки. Если у жертвы монеток не больше порога из правил сезона, урон монетками достаётся тебе, умноженный по тем же правилам.",
  "rarity": "common", "price": null, "window": "anytime", "hostile": true, "stackable": false, "tags": ["weapon"],
  "effect": {
    "target": { "selector": "chosen", "excludeSelf": true },
    "actions": [
      { "type": "move", "steps": -1 },
      { "type": "changeResource", "resource": "coins", "amount": -2, "reflect": true }
    ]
  }
}
```

### 13. Ездовой дракон — дополнительный d6 к следующему броску

```json
{
  "id": "riding-dragon", "kind": "item", "name": "Ездовой дракон",
  "description": "В следующем броске за прохождение есть дополнительный d6.",
  "rarity": "epic", "price": 45, "window": "beforeDice", "hostile": false, "stackable": true,
  "effect": { "target": { "selector": "self" }, "actions": [{ "type": "modifyDice", "when": "next", "stage": "count", "value": 1, "sides": 6 }] }
}
```

### 14. Плазмаган — уничтожить столько предметов, сколько выпало

```json
{
  "id": "plasma-gun", "kind": "item", "name": "Плазмаган",
  "description": "Оружие. Брось d4: у выбранного другого игрока уничтожается столько предметов. Назови их заранее по порядку; если назвал меньше — остальные случайные, если выпало больше, чем у него есть, — уничтожаются все.",
  "rarity": "common", "price": 20, "window": "anytime", "hostile": true, "stackable": false, "tags": ["weapon"],
  "effect": {
    "target": { "selector": "chosen", "excludeSelf": true },
    "actions": [
      { "type": "roll", "dice": "1d4" },
      { "type": "takeObject", "mode": "destroy", "pick": "chosen", "count": "$roll", "filter": { "kind": "item" } }
    ]
  }
}
```

### 15. Пистолет — толчок вперёд или назад на выбор

```json
{
  "id": "pistol", "kind": "item", "name": "Пистолет",
  "description": "Оружие. Выбранный другой игрок идёт на 1 клетку вперёд или назад — на твой выбор — и теряет 2 монетки. Если у жертвы монеток не больше порога из правил сезона, урон монетками достаётся тебе, умноженный по тем же правилам.",
  "rarity": "common", "price": null, "window": "anytime", "hostile": true, "stackable": false, "tags": ["weapon"],
  "effect": {
    "target": { "selector": "chosen", "excludeSelf": true },
    "actions": [
      { "type": "move", "steps": 1, "direction": "chosen" },
      { "type": "changeResource", "resource": "coins", "amount": -2, "reflect": true }
    ]
  }
}
```

### 16. Свинья — отдать свой эффект другому

```json
{
  "id": "pig", "kind": "item", "name": "Свинья",
  "description": "Отдай выбранному другому игроку один свой эффект на выбор.",
  "rarity": "common", "price": null, "window": "anytime", "hostile": true, "stackable": false, "tags": ["pig"],
  "effect": {
    "target": { "selector": "chosen", "excludeSelf": true },
    "actions": [{ "type": "takeObject", "mode": "give", "pick": "chosen", "filter": { "kind": "effect" } }]
  }
}
```

### 17. Часовая бомба — эффект со счётчиком

```json
{
  "id": "time-bomb", "kind": "effect", "name": "Часовая бомба",
  "description": "Счётчик 3: каждое твоё прохождение или дроп уменьшает его на 1. На нуле — назад на d6.",
  "rarity": "common", "price": null, "hostile": true,
  "effect": {
    "trigger": "runEnded", "duration": { "untilTriggered": true }, "counter": { "start": 3 },
    "actions": [{ "type": "roll", "dice": "1d6" }, { "type": "move", "steps": "-$roll" }]
  }
}
```

### 18. Копьё — все в пределах пяти клеток

```json
{
  "id": "spear", "kind": "item", "name": "Копье",
  "description": "Оружие. Все игроки в пределах пяти клеток от тебя по карте, включая стоящих с тобой на одной клетке, идут на 1 клетку назад и теряют 1 монетку. Если у жертвы монеток не больше порога из правил сезона, урон монетками достаётся тебе, умноженный по тем же правилам.",
  "rarity": "common", "price": null, "window": "anytime", "hostile": true, "stackable": false, "tags": ["weapon"],
  "effect": {
    "target": { "selector": "withinCells", "cells": 5, "includeSameCell": true, "excludeSelf": true },
    "actions": [
      { "type": "move", "steps": -1 },
      { "type": "changeResource", "resource": "coins", "amount": -1, "reflect": true }
    ]
  }
}
```

### 19. Средний кубик — каждый кубик показывает среднее

```json
{
  "id": "middle-die", "kind": "item", "name": "Средний кубик",
  "description": "В следующем броске за прохождение каждый кубик показывает своё среднее: d4 — 2, d6 — 3, d8 — 4, d10 — 5, d12 — 6, d20 — 10.",
  "rarity": "epic", "price": 50, "window": "beforeDice", "hostile": false, "stackable": false,
  "effect": { "target": { "selector": "self" }, "actions": [{ "type": "modifyDice", "when": "next", "stage": "fixAverage", "value": 1 }] }
}
```

### 20. Кубик сахара — ветвление по выбору игрока

```json
{
  "id": "sugar-cube", "kind": "item", "name": "Кубик сахара",
  "description": "На выбор: дополнительный d6 к следующему броску за прохождение или 3 монетки.",
  "rarity": "common", "price": null, "window": "beforeDice", "hostile": false, "stackable": false, "tags": ["edible"],
  "effect": {
    "target": { "selector": "self" },
    "outcomes": {
      "choice": { "prompt": "Что берёшь?", "options": { "list": ["+1d6 к броску", "+3 монетки"] } },
      "cases": [
        { "option": "+1d6 к броску", "actions": [{ "type": "modifyDice", "when": "next", "stage": "count", "value": 1, "sides": 6 }] },
        { "option": "+3 монетки", "actions": [{ "type": "changeResource", "resource": "coins", "amount": 3 }] }
      ]
    }
  }
}
```

### 21. Кексик — только при нуле монеток

```json
{
  "id": "muffin", "kind": "item", "name": "Кексик",
  "description": "Можно использовать, только если у тебя ноль монеток. Получаешь 5 монеток.",
  "rarity": "common", "price": 10, "window": "anytime", "hostile": false, "stackable": true, "tags": ["edible"],
  "useCondition": { "stat": "coins", "gte": 0, "lte": 0 },
  "effect": { "target": { "selector": "self" }, "actions": [{ "type": "changeResource", "resource": "coins", "amount": 5 }] }
}
```

## Примеры ивентов

### 1. Попутный ветер — хороший

```json
{
  "id": "tailwind", "kind": "event", "name": "Попутный ветер",
  "description": "В следующем броске за прохождение на один кубик больше.",
  "rarity": "common", "price": null, "hostile": false,
  "effect": { "target": { "selector": "self" }, "actions": [{ "type": "modifyDice", "when": "next", "stage": "count", "value": 1 }] }
}
```

### 2. Хоррор-неделя — плохой

```json
{
  "id": "horror-week", "kind": "event", "name": "Хоррор-неделя",
  "description": "Следующая игра будет хоррором.",
  "rarity": "common", "price": null, "hostile": false,
  "effect": { "target": { "selector": "self" }, "actions": [{ "type": "giveObject", "objectId": "forced-genre", "params": { "tag": "Horror" } }] }
}
```

### 3. Караоке — плохой, IRL

```json
{
  "id": "karaoke", "kind": "event", "name": "Караоке",
  "description": "Спой песню на выбор остальных и скинь видео в галерею.",
  "rarity": "common", "price": null, "hostile": false, "manual": true, "proof": "media"
}
```

### 4. Рискнёшь? — рискованный

```json
{
  "id": "gamble", "kind": "event", "name": "Рискнёшь?",
  "description": "Кинь d6: на 1–3 назад на 3 клетки, на 4–6 вперёд на 3.",
  "rarity": "common", "price": null, "hostile": false,
  "effect": {
    "target": { "selector": "self" },
    "outcomes": {
      "roll": "1d6",
      "cases": [
        { "from": 1, "to": 3, "actions": [{ "type": "move", "steps": -3 }] },
        { "from": 4, "to": 6, "actions": [{ "type": "move", "steps": 3 }] }
      ]
    }
  }
}
```

### 5. Даже не вспотел — плохой, условие

```json
{
  "id": "not-even-sweating", "kind": "event", "name": "Даже не вспотел",
  "description": "Следующую игру нужно пройти на сложной сложности или выше.",
  "rarity": "common", "price": null, "hostile": false,
  "effect": { "target": { "selector": "self" }, "actions": [{ "type": "modifyNextRoll", "runCondition": { "difficultyAtLeast": "hard" } }] }
}
```

## Примеры достижений

```json
{
  "id": "horror-marathon", "kind": "achievement", "name": "Хоррор-марафон",
  "description": "Пройди три хоррора подряд.", "scope": "season",
  "effect": { "trigger": "runCompleted", "condition": { "stat": "completedStreakWithTag", "tag": "Horror", "gte": 3 }, "actions": [{ "type": "changeResource", "resource": "coins", "amount": 5 }] }
}
```

```json
{
  "id": "jackpot", "kind": "achievement", "name": "Джекпот",
  "description": "Выбрось максимум на всех кубиках, когда их хотя бы три.", "scope": "allTime",
  "effect": { "trigger": "afterDice", "condition": { "stat": "allDiceMax", "minDice": 3 }, "actions": [{ "type": "changeResource", "resource": "coins", "amount": 10 }] }
}
```

```json
{
  "id": "unbreakable", "kind": "achievement", "name": "Непробиваемый",
  "description": "Переживи пять атак за сезон.", "scope": "season",
  "effect": { "trigger": "hostileIncoming", "condition": { "stat": "hostileReceived", "gte": 5 }, "actions": [{ "type": "changeResource", "resource": "coins", "amount": 5 }] }
}
```

## Зона, клетки, голосование, челлендж недели

```json
{
  "id": "horror-swamp", "name": "Болото ужаса",
  "rollFilter": { "tags": ["Horror"] },
  "diceModifier": { "stage": "add", "value": 1 },
  "dropPenaltyMultiplier": 1.5,
  "deck": "horror-bad",
  "shopPriceMultiplier": 1.0
}
```

```json
[
  { "id": "c12", "type": "shop", "grants": "shop-coupon" },
  { "id": "c17", "type": "event", "deck": "zone" },
  { "id": "c23", "type": "teleport", "to": "c41" },
  { "id": "c30", "type": "pointsBonus", "amount": 3 },
  { "id": "c40", "type": "checkpoint" }
]
```

Клетки — те же, что на карте сезона (этап 2, D-300…D-307): `type` — `start`, `empty`, `finish`, `fork`, `teleport` (`to`), `checkpoint`, `pointsBonus` (`amount`), `event` (`deck`), `shop` (`grants`); у любой клетки может быть `zone` и координаты редактора `x`, `y`. Как они работают:

- **Остановка** — конец своего хода кубами и штрафного хода дропа (позже — толчков эффектов). Телепорт переносит на `to`, клетка назначения не срабатывает; бонус очков даёт `amount`. Переносы, правки часов и реджект клетки не запускают.
- **Развилка** (`fork`) — у неё не меньше двух выходов и одна ветка по умолчанию. Свой ход останавливается на ней и ждёт выбора ветки, вынужденное движение идёт по ветке по умолчанию.
- **Чекпоинт** — толчок назад на нём останавливается. Реджект и правки часов снимают клетки прохождения без учёта чекпоинта.
- **Клетки `event` и `shop`** попадают на карту, только когда включены их механики (`features.events`, `features.shop`).
- **Зона** задаёт фильтр ролла для игрока, стоящего в ней; зона фиксируется при ролле вместе с множителем штрафа дропа и модификатором кубов (на этапе 2 — `count` и `add` целым числом, остальной конвейер броска — с предметами). Колода зоны (`deck`) работает с ивентами (этап 5, D-502): клетка `deck: zone` тянет из неё, даже если такой колоды нет в контенте — тогда ручной эффект; у зоны без колоды — `events.defaultDeck`. Цены магазина зоны оживут со своей механикой.

Карта целиком — клетки, стрелки и зоны. У стрелки `isDefaultForward` — ветка по умолчанию, `isPrimaryBackward` — основное входящее ребро (обязательно у клеток с несколькими входами):

```json
{
  "cells": [
    { "id": "start", "type": "start", "x": 40, "y": 300 },
    { "id": "f", "type": "fork" },
    { "id": "c1", "type": "empty", "zone": "horror-swamp" },
    { "id": "c2", "type": "teleport", "to": "j", "zone": "horror-swamp" },
    { "id": "b1", "type": "pointsBonus", "amount": 3 },
    { "id": "j", "type": "checkpoint" },
    { "id": "finish", "type": "finish" }
  ],
  "edges": [
    { "from": "start", "to": "f", "isDefaultForward": true, "isPrimaryBackward": true },
    { "from": "f", "to": "b1", "isDefaultForward": true, "isPrimaryBackward": true },
    { "from": "f", "to": "c1", "isDefaultForward": false, "isPrimaryBackward": true },
    { "from": "c1", "to": "c2", "isDefaultForward": true, "isPrimaryBackward": true },
    { "from": "c2", "to": "j", "isDefaultForward": true, "isPrimaryBackward": false },
    { "from": "b1", "to": "j", "isDefaultForward": true, "isPrimaryBackward": true },
    { "from": "j", "to": "finish", "isDefaultForward": true, "isPrimaryBackward": true }
  ],
  "zones": [
    { "id": "horror-swamp", "name": "Болото ужаса", "rollFilter": { "tags": ["Horror"] }, "dropPenaltyMultiplier": 1.5 }
  ]
}
```

```json
{
  "question": "Какой жанр подкинем лидеру?",
  "options": { "from": "categories" },
  "voters": "players", "anonymous": true, "closesInHours": 24,
  "onResult": {
    "target": { "selector": "leader" },
    "actions": [{ "type": "giveObject", "objectId": "forced-genre", "params": { "tag": "$result" } }]
  }
}
```

```json
{
  "id": "old-school", "name": "Олдскул",
  "description": "Пройди игру, вышедшую до 2005 года.",
  "condition": { "game": { "releaseYearBefore": 2005 } },
  "reward": { "coins": 10 }, "check": "auto"
}
```
