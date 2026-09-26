import type { Schemas } from '../api/client';
import { Cover } from '../board/GameCards';
import { ru } from '../i18n/ru';
import { Button } from '../ui/Button';
import { Chip } from '../ui/Marks';
import { GameMarks } from './GameMarks';
import { RerollButton } from './RerollButton';

type Offered = Schemas['OfferedGameView'];
type Roll = Schemas['WheelRollView'];

const w = ru.moments.wheel;

/** The result's heading takes the focus when the wheel stops */
export const rollResultTitle = 'roll-result-title';

/** What the wheel passed on the way, in words: «Промах: Hollow Knight. Уже прошёл Вася, крутим дальше» */
function Misses({ roll }: { roll: Roll | null }) {
  if (!roll || roll.misses.length === 0) return null;
  return (
    <ul data-testid="roll-misses" className="grid gap-1 text-sm text-ink-soft">
      {roll.misses.map((miss, i) => (
        <li key={i}>
          {w.missNote(miss.game)}{' '}
          {miss.reason === 'beingPlayed' ? w.missPlaying(miss.player) : w.miss(miss.player)}
        </li>
      ))}
    </ul>
  );
}

/** A choice card's name says all it shows: the action, the game, its hours and the other players' marks */
function optionName(game: Offered) {
  return [
    `${ru.turn.pick} ${game.title}`,
    ru.board.hours(game.hours ?? null),
    ...game.marks.map((mark) => ru.turn.gameMark(mark.playerName, mark.kind)),
  ].join('. ');
}

function Category({ roll }: { roll: Roll | null }) {
  return roll ? <Chip>{w.category(roll.category)}</Chip> : null;
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
        <p className="text-sm text-ink-soft">{ru.turn.rolled}</p>
        <h3
          id={rollResultTitle}
          tabIndex={-1}
          className="font-display text-lg font-heavy wrap-anywhere focus:outline-none"
        >
          {offer.title}
        </h3>
        <p className="text-sm text-ink-soft">{ru.board.hours(offer.hours ?? null)}</p>
        <div className="flex flex-wrap gap-1">
          <Category roll={roll} />
        </div>
      </div>
      <div className="col-span-2 grid gap-2">
        <GameMarks marks={offer.marks} />
        <Misses roll={roll} />
      </div>
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
      </div>
    </div>
  );
}

/** Several games of one category (D-06): each is a card to pick, with its own «Уже проходил» */
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
  return (
    <fieldset data-testid="choice" className="grid min-w-0 gap-3">
      <legend className="mb-2 font-display font-heavy">
        <span id={rollResultTitle} tabIndex={-1} className="focus:outline-none">
          {ru.turn.choose}
        </span>
      </legend>
      <div className="flex flex-wrap gap-1">
        <Category roll={roll} />
      </div>
      <Misses roll={roll} />
      <ul className="grid gap-3">
        {options.map(({ id, game }) => (
          <li key={id} className="grid justify-items-start gap-1">
            {/* The whole card picks the game: a big target on a phone */}
            <button
              type="button"
              data-testid={`option-${id}`}
              aria-label={optionName(game)}
              disabled={pending}
              onClick={() => {
                onChoose(id);
              }}
              className="grid w-full cursor-pointer grid-cols-[auto_minmax(0,1fr)] gap-x-4 rounded-md border-2 border-ink bg-card p-3 text-left transition duration-(--duration-fast) ease-out is-hover:-translate-y-px is-hover:shadow-press-up is-focus:focus-ring is-active:translate-y-px disabled:cursor-default disabled:opacity-50"
            >
              <Cover game={game} width={64} />
              <span className="grid content-start gap-1">
                <strong className="font-display wrap-anywhere">{game.title}</strong>
                <span className="text-sm text-ink-soft">{ru.board.hours(game.hours ?? null)}</span>
                <GameMarks marks={game.marks} />
              </span>
            </button>
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
          </li>
        ))}
      </ul>
      <div>
        <RerollButton price={price} pending={pending} onReroll={onReroll} />
      </div>
    </fieldset>
  );
}
