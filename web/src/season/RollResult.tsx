import type { Schemas } from '../api/client';
import { moscowDay } from '../app/time';
import { Cover } from '../board/GameCards';
import { ru } from '../i18n/ru';
import { Button } from '../ui/Button';
import { cx } from '../ui/cx';
import { Tag } from '../ui/Marks';
import { GameMarks } from './GameMarks';
import { RerollButton } from './RerollButton';

type Offered = Schemas['OfferedGameView'];
type Roll = Schemas['WheelRollView'];

const w = ru.moments.wheel;

/** The result's heading takes the focus when the wheel stops */
export const rollResultTitle = 'roll-result-title';

/**
 * What the wheel passed on the way, told as done: one folded line under the actions, the list inside —
 * «Hollow Knight — уже прошёл Вася, 12.10», «Dead Cells — сейчас играет Петя»
 */
function Misses({ roll }: { roll: Roll | null }) {
  if (!roll || roll.misses.length === 0) return null;
  return (
    <details data-testid="roll-misses" className="text-sm text-ink-soft">
      <summary className="min-h-11 cursor-pointer content-center rounded-md is-focus:focus-ring">
        {w.missed(roll.misses.length)}
      </summary>
      <ul className="grid gap-1 pt-1">
        {roll.misses.map((miss, i) => (
          <li key={i}>
            {miss.reason === 'beingPlayed'
              ? w.missedPlaying(miss.game, miss.player)
              : w.missedCompleted(miss.game, miss.player, miss.at ? moscowDay(miss.at) : null)}
          </li>
        ))}
      </ul>
    </details>
  );
}

/** A choice card's name says all it shows: the action, the game, its hours and the other players' marks */
function optionName(game: Offered) {
  return [
    `${ru.turn.pick} ${game.title}`,
    ru.hours.estimate(game.hours ?? null),
    ...game.marks.map((mark) => ru.turn.gameMark(mark.playerName, mark.kind)),
  ].join('. ');
}

/** The rolled game: start it (the one main action), say it was played already, or reroll */
export function OfferCard({
  offer,
  roll,
  price,
  pending,
  onStart,
  onAlreadyPlayed,
  onReroll,
}: {
  offer: Offered;
  roll: Roll | null;
  price: Schemas['RerollPriceView'] | null;
  pending: boolean;
  onStart: () => void;
  onAlreadyPlayed: () => void;
  onReroll: () => void;
}) {
  return (
    <div data-testid="offer" className="grid grid-cols-[auto_minmax(0,1fr)] gap-x-4 gap-y-3">
      <Cover game={offer} />
      <div className="grid content-start gap-1">
        <h3
          id={rollResultTitle}
          tabIndex={-1}
          className="font-display text-lg font-heavy wrap-anywhere focus:outline-none"
        >
          {offer.title}
        </h3>
        <p className="text-sm text-ink-soft">{ru.hours.estimate(offer.hours ?? null)}</p>
        {roll ? (
          <div className="flex flex-wrap gap-1">
            <Tag>{roll.category}</Tag>
          </div>
        ) : null}
      </div>
      {offer.marks.length > 0 ? (
        <div className="col-span-2">
          <GameMarks marks={offer.marks} />
        </div>
      ) : null}
      <div className="col-span-2 grid gap-3">
        <Button variant="main" data-testid="start" loading={pending} onClick={onStart}>
          {ru.turn.start}
        </Button>
        <div className="flex flex-wrap gap-3">
          <Button data-testid="already-played" disabled={pending} onClick={onAlreadyPlayed}>
            {ru.turn.alreadyPlayed}
          </Button>
          <RerollButton price={price} pending={pending} onReroll={onReroll} />
        </div>
        <Misses roll={roll} />
      </div>
    </div>
  );
}

/** Several games of one category (D-06): each is a card to pick, its «Уже проходил» inside the same frame */
export function ChoiceCard({
  choice,
  roll,
  price,
  pending,
  onChoose,
  onAlreadyPlayed,
  onReroll,
}: {
  choice: Schemas['ChoiceView'];
  roll: Roll | null;
  price: Schemas['RerollPriceView'] | null;
  pending: boolean;
  onChoose: (optionId: string) => void;
  onAlreadyPlayed: (gameId: string) => void;
  onReroll: () => void;
}) {
  const options = choice.options.flatMap(({ id, game }) => (game ? [{ id, game }] : []));
  // Four games and more: a denser card, so the choice fits a phone's screen or two
  const dense = options.length >= 4;
  return (
    <fieldset data-testid="choice" className="grid min-w-0 gap-3">
      <legend className="mb-2 font-display font-heavy">
        <span id={rollResultTitle} tabIndex={-1} className="focus:outline-none">
          {ru.turn.choose}
        </span>
      </legend>
      {roll ? (
        <div className="flex flex-wrap gap-1">
          <Tag>{roll.category}</Tag>
        </div>
      ) : null}
      <ul className="grid gap-4">
        {options.map(({ id, game }) => (
          <li
            key={id}
            // The ring goes round the whole card, «Уже проходил» included, not the button's half of it
            className="grid rounded-md border-2 border-ink bg-card has-[button[data-pick]:focus-visible]:focus-ring"
          >
            {/* The card picks the game: a big target on a phone */}
            <button
              type="button"
              data-testid={`option-${id}`}
              data-pick
              aria-label={optionName(game)}
              disabled={pending}
              onClick={() => {
                onChoose(id);
              }}
              className={cx(
                'grid w-full cursor-pointer grid-cols-[auto_minmax(0,1fr)] items-center gap-x-4 rounded-md text-left transition duration-(--duration-fast) ease-out is-hover:bg-page focus-visible:outline-none disabled:cursor-default disabled:opacity-50',
                dense ? 'p-2' : 'p-3',
              )}
            >
              <Cover game={game} width={dense ? 40 : 64} />
              <span className="grid content-start gap-1">
                <strong className="font-display wrap-anywhere">{game.title}</strong>
                <span className="text-sm text-ink-soft">
                  {ru.hours.estimate(game.hours ?? null)}
                </span>
                <GameMarks marks={game.marks} />
                {/* A label, not a link: the whole card is the button */}
                <span
                  className="justify-self-start rounded-full border-2 border-ink px-3 py-1 text-sm font-bold"
                  aria-hidden
                >
                  {ru.turn.pickShort}
                </span>
              </span>
            </button>
            <div className="border-t-2 border-muted px-3">
              <Button
                variant="link"
                data-testid={`already-played-${id}`}
                aria-label={ru.turn.alreadyPlayedGame(game.title)}
                disabled={pending}
                onClick={() => {
                  onAlreadyPlayed(game.id);
                }}
              >
                {ru.turn.alreadyPlayed}
              </Button>
            </div>
          </li>
        ))}
      </ul>
      <div>
        <RerollButton price={price} pending={pending} onReroll={onReroll} />
      </div>
      <Misses roll={roll} />
    </fieldset>
  );
}
