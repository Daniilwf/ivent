import type { Schemas } from '../api/client';
import { moscowDayKey, moscowDayLabel, moscowTime } from '../app/time';
import { ru } from '../i18n/ru';

// The season's feed as lines to read (H5, D-150): the server gives the log as it is — events with ids — and this
// module turns one command's events into one line: who did what with which game, a few facts (dice, points, cells)
// and a quote (a review, the admin's comment). Lines are grouped by the Moscow day, newest first.

type Entry = Schemas['FeedEntryView'];
type Page = Schemas['FeedView'];

export type FeedPlayer = {
  id: string;
  userId: string;
  name: string;
  /** The player's place in the season's list: the token colour, as on the map */
  token: number;
  avatar?: string | undefined;
  hasProfile: boolean;
};

export type FeedGame = { id: string; title: string; hasPage: boolean };

/** A name in a line: the screen makes it a link to the profile or the game page */
export type FeedRef = { kind: 'player'; player: FeedPlayer } | { kind: 'game'; game: FeedGame };

export type FeedIcon =
  | 'season'
  | 'roll'
  | 'start'
  | 'complete'
  | 'review'
  | 'drop'
  | 'proof'
  | 'finish'
  | 'effect'
  | 'admin'
  | 'undo';

export type FeedItem = {
  /** The command's id: one command, one line */
  id: string;
  /** The newest sequence of the command's events: the line's place in the feed */
  sequence: number;
  at: string;
  icon: FeedIcon;
  /** Whose line it is: their sticker stands next to it */
  actor: FeedPlayer | null;
  line: readonly (string | FeedRef)[];
  facts: string[];
  quote: { rating: number | null; text: string | null } | null;
  /** The admin undid the command: the line stays, crossed out */
  undone: boolean;
};

export type FeedDay = { key: string; label: string; items: FeedItem[] };

/** The names the pages of the feed point to by id; later pages add to them */
export type FeedRefs = {
  players: Map<string, FeedPlayer>;
  games: Map<string, FeedGame>;
  /** A run's game */
  runs: Map<string, string>;
};

export function emptyRefs(): FeedRefs {
  return { players: new Map(), games: new Map(), runs: new Map() };
}

/** The refs with a page's names added (players are the whole season each time: the latest list wins) */
export function withRefs(refs: FeedRefs, page: Pick<Page, 'players' | 'games' | 'runs'>): FeedRefs {
  const players = page.players.length
    ? new Map(
        page.players.map((p) => [
          p.id,
          {
            id: p.id,
            userId: p.userId,
            name: p.name,
            token: p.token,
            avatar: p.avatar?.thumbnailUrl,
            hasProfile: p.hasProfile,
          },
        ]),
      )
    : refs.players;
  const games = new Map(refs.games);
  for (const g of page.games) games.set(g.id, { id: g.id, title: g.title, hasPage: g.hasPage });
  const runs = new Map(refs.runs);
  for (const r of page.runs) runs.set(r.id, r.gameId);
  return { players, games, runs };
}

/** Entries of several pages as one list, newest first, each sequence once (a fresh page replaces what it repeats) */
export function mergeEntries(older: readonly Entry[], newer: readonly Entry[]): Entry[] {
  const bySequence = new Map<number, Entry>();
  for (const e of older) bySequence.set(e.sequence, e);
  for (const e of newer) bySequence.set(e.sequence, e);
  return [...bySequence.values()].sort((a, b) => b.sequence - a.sequence);
}

// ---- Reading the event data: the server's JSON is the current format of each event (older ones upcast) ----

type Data = Record<string, unknown>;

const record = (value: unknown): Data =>
  value !== null && typeof value === 'object' && !Array.isArray(value) ? (value as Data) : {};
const text = (d: Data, key: string): string | null => (typeof d[key] === 'string' ? d[key] : null);
const number = (d: Data, key: string): number | null =>
  typeof d[key] === 'number' ? d[key] : null;
const list = (d: Data, key: string): unknown[] =>
  Array.isArray(d[key]) ? (d[key] as unknown[]) : [];
const dieValues = (d: Data, key: string): number[] =>
  list(d, key)
    .map((die) => number(record(die), 'value'))
    .filter((v): v is number => v !== null);

type Event = { type: string; data: Data };

/** The line and its main facts for the command's leading event */
type Headline = {
  icon: FeedIcon;
  actor: FeedPlayer | null;
  line: readonly (string | FeedRef)[];
  facts?: string[];
  quote?: FeedItem['quote'];
};

type Context = {
  player: (d: Data) => FeedPlayer;
  playerRef: (d: Data) => FeedRef;
  gameRef: (d: Data) => FeedRef;
};

const L = ru.feed.lines;
const F = ru.feed.facts;
const quoteOf = (comment: string | null) => (comment ? { rating: null, text: comment } : null);

/**
 * The events that can lead a command's line, most telling first: a completion's line is «проходит», not the points it
 * gave; a reroll's is the new game, not the reroll. The rest of the command's events become facts.
 */
const headlines: [string, (d: Data, c: Context) => Headline][] = [
  [
    'command-undone',
    (d) => ({ icon: 'undo', actor: null, line: L.undone(), quote: quoteOf(text(d, 'comment')) }),
  ],
  [
    'season-created',
    (d) => ({ icon: 'season', actor: null, line: L.seasonCreated(text(d, 'name') ?? '') }),
  ],
  ['season-result-recorded', () => ({ icon: 'season', actor: null, line: L.results() })],
  [
    'season-status-changed',
    (d) => ({
      icon: 'season',
      actor: null,
      line: L.seasonStatus(
        (text(d, 'to') ?? 'active') as 'draft' | 'active' | 'closing' | 'finished' | 'archived',
      ),
    }),
  ],
  [
    'season-deadline-set',
    (d) => {
      const deadline = text(d, 'deadline');
      return {
        icon: 'season',
        actor: null,
        line: deadline ? L.deadlineSet(moscowTime(deadline)) : L.deadlineRemoved(),
      };
    },
  ],
  [
    'ruleset-changed',
    (d) => ({ icon: 'season', actor: null, line: L.rulesChanged(number(d, 'version') ?? 0) }),
  ],
  [
    'finish-bonus-rules-refreshed',
    () => ({ icon: 'season', actor: null, line: L.finishBonuses() }),
  ],
  [
    'season-player-added',
    (d, c) => ({ icon: 'start', actor: c.player(d), line: L.joined(c.playerRef(d)) }),
  ],
  [
    'run-completed',
    (d, c) => ({
      icon: 'complete',
      actor: c.player(d),
      line: L.completed(c.playerRef(d), c.gameRef(d)),
      facts: [
        ru.difficulty[(text(d, 'difficulty') ?? 'normal') as keyof typeof ru.difficulty],
        ru.hours.value(number(d, 'hours') ?? 0),
        ...(d['afterFinish'] === true ? [F.afterFinish] : []),
      ],
    }),
  ],
  [
    'proof-rejected',
    (d, c) => ({
      icon: 'proof',
      actor: c.player(d),
      line: L.proofRejected(c.playerRef(d), c.gameRef(d)),
      quote: quoteOf(text(d, 'comment')),
    }),
  ],
  [
    'proof-approved',
    (d, c) => ({
      icon: 'proof',
      actor: c.player(d),
      line: L.proofApproved(c.playerRef(d), c.gameRef(d), d['withoutProof'] === true),
      quote: quoteOf(text(d, 'comment')),
    }),
  ],
  [
    'tech-reroll-converted-to-drop',
    (d, c) => ({
      icon: 'drop',
      actor: c.player(d),
      line: L.techToDrop(c.playerRef(d), c.gameRef(d)),
      facts: [F.penalty(dieValues(d, 'penaltyDice'))],
      quote: quoteOf(text(d, 'comment')),
    }),
  ],
  [
    'run-dropped',
    (d, c) => ({
      icon: 'drop',
      actor: c.player(d),
      line: L.dropped(c.playerRef(d), c.gameRef(d)),
      facts: dieValues(d, 'penaltyDice').length ? [F.penalty(dieValues(d, 'penaltyDice'))] : [],
    }),
  ],
  [
    'run-tech-rerolled',
    (d, c) => ({
      icon: 'drop',
      actor: c.player(d),
      line: L.techRerolled(c.playerRef(d), c.gameRef(d)),
      facts: [
        (ru.turn.techRerollReasons as Record<string, string>)[text(d, 'reason') ?? ''] ??
          ru.turn.techRerollReasons.other,
      ],
      quote: quoteOf(text(d, 'comment')),
    }),
  ],
  [
    'run-hours-corrected',
    (d, c) => ({
      icon: 'admin',
      actor: c.player(d),
      line: L.hoursCorrected(
        c.playerRef(d),
        c.gameRef(d),
        number(d, 'oldHours') ?? 0,
        number(d, 'newHours') ?? 0,
      ),
      quote: quoteOf(text(d, 'comment')),
    }),
  ],
  [
    'run-difficulty-changed',
    (d, c) => ({
      icon: 'admin',
      actor: c.player(d),
      line: L.difficultyChanged(
        c.playerRef(d),
        c.gameRef(d),
        ru.difficulty[(text(d, 'oldDifficulty') ?? 'normal') as keyof typeof ru.difficulty],
        ru.difficulty[(text(d, 'newDifficulty') ?? 'normal') as keyof typeof ru.difficulty],
      ),
      quote: quoteOf(text(d, 'comment')),
    }),
  ],
  [
    'run-reviewed',
    (d, c) => ({
      icon: 'review',
      actor: c.player(d),
      line: L.reviewed(c.playerRef(d), c.gameRef(d)),
      quote: { rating: number(d, 'rating'), text: text(d, 'text') },
    }),
  ],
  [
    'proof-submitted',
    (d, c) => ({
      icon: 'proof',
      actor: c.player(d),
      line: L.proofSent(c.playerRef(d), c.gameRef(d)),
    }),
  ],
  [
    'run-started',
    (d, c) => ({
      icon: 'start',
      actor: c.player(d),
      line: L.started(c.playerRef(d), c.gameRef(d)),
    }),
  ],
  [
    'game-rolled',
    (d, c) => ({
      icon: 'roll',
      actor: c.player(d),
      line: L.rolled(c.playerRef(d), c.gameRef(d)),
      facts: [
        F.category(text(d, 'category') ?? ''),
        ...(list(d, 'misses').length ? [F.misses(list(d, 'misses').length)] : []),
      ],
    }),
  ],
  [
    'game-choice-rolled',
    (d, c) => ({
      icon: 'roll',
      actor: c.player(d),
      line: L.choiceRolled(c.playerRef(d), list(d, 'offers').length),
      facts: [
        F.category(text(d, 'category') ?? ''),
        ...(list(d, 'misses').length ? [F.misses(list(d, 'misses').length)] : []),
      ],
    }),
  ],
  [
    'game-rerolled',
    (d, c) => ({ icon: 'roll', actor: c.player(d), line: L.rerolled(c.playerRef(d)) }),
  ],
  [
    'game-excluded',
    (d, c) => ({
      icon: 'roll',
      actor: c.player(d),
      line: L.alreadyPlayed(c.playerRef(d), c.gameRef(d)),
    }),
  ],
  ['choice-made', (d, c) => ({ icon: 'roll', actor: c.player(d), line: L.chose(c.playerRef(d)) })],
  [
    'offer-discarded',
    (d, c) => ({
      icon: 'admin',
      actor: c.player(d),
      line: L.offerDiscarded(c.playerRef(d), c.gameRef(d)),
    }),
  ],
  [
    'choice-discarded',
    (d, c) => ({ icon: 'admin', actor: c.player(d), line: L.choiceDiscarded(c.playerRef(d)) }),
  ],
  [
    'player-finish-revoked',
    (d, c) => ({ icon: 'finish', actor: c.player(d), line: L.finishRevoked(c.playerRef(d)) }),
  ],
  [
    'player-finished',
    (d, c) => ({
      icon: 'finish',
      actor: c.player(d),
      line: L.finished(c.playerRef(d), number(d, 'order') ?? 1),
    }),
  ],
  [
    'player-adjusted',
    (d, c) => ({
      icon: 'admin',
      actor: c.player(d),
      line: L.adjusted(c.playerRef(d)),
      quote: quoteOf(text(d, 'comment')),
    }),
  ],
  [
    'player-inactivity-set',
    (d, c) => ({
      icon: 'admin',
      actor: c.player(d),
      line: d['isInactive'] === true ? L.paused(c.playerRef(d)) : L.back(c.playerRef(d)),
    }),
  ],
  [
    'manual-effect-resolved',
    (d, c) => ({
      icon: 'effect',
      actor: c.player(d),
      line: L.effectResolved(c.playerRef(d), text(d, 'outcome') !== 'notApplicable'),
      quote: quoteOf(text(d, 'comment')),
    }),
  ],
  [
    'manual-effect-created',
    (d, c) => ({
      icon: 'effect',
      actor: c.player(d),
      line: L.effectDrawn(c.playerRef(d), text(d, 'drawEvent') === 'bad' ? 'bad' : 'good'),
    }),
  ],
  [
    'points-changed',
    (d, c) => ({
      icon: 'admin',
      actor: c.player(d),
      line: L.points(c.playerRef(d), number(d, 'delta') ?? 0, text(d, 'reason')),
    }),
  ],
  [
    'coins-changed',
    (d, c) => ({
      icon: 'admin',
      actor: c.player(d),
      line: L.coins(c.playerRef(d), number(d, 'delta') ?? 0, text(d, 'reason')),
    }),
  ],
  [
    'player-moved',
    (d, c) => ({ icon: 'admin', actor: c.player(d), line: L.moved(c.playerRef(d)) }),
  ],
];

const effectSource = (value: string | null) =>
  value === 'paidReroll' || value === 'drop' || value === 'difficulty' || value === 'item'
    ? value
    : 'drop';

/** The facts the rest of a command's events add to its line: dice, points and cells, coins, the finish, events drawn */
function factsOf(events: Event[], lead: Event): string[] {
  const facts: string[] = [];
  let points = 0;
  let cells = 0;
  let moved = false;
  let transferred = false;
  let coins = 0;
  for (const event of events) {
    if (event === lead) continue;
    const d = event.data;
    switch (event.type) {
      case 'completion-rolled': {
        const dice = dieValues(d, 'dice');
        if (dice.length) facts.push(F.dice(dice));
        const challenge = dieValues(d, 'challengeDice');
        if (challenge.length) facts.push(F.challenge(challenge));
        break;
      }
      case 'points-changed':
        points += number(d, 'delta') ?? 0;
        break;
      case 'coins-changed':
        coins += number(d, 'delta') ?? 0;
        break;
      case 'player-moved': {
        const steps = number(d, 'steps') ?? 0;
        const path = list(d, 'path').length;
        moved = true;
        if (steps === 0) transferred = true;
        else cells += Math.sign(steps) * path;
        break;
      }
      case 'player-finished':
        facts.push(F.finish(number(d, 'order') ?? 1));
        break;
      case 'player-frozen':
        facts.push(F.frozen);
        break;
      case 'manual-effect-created':
        facts.push(
          ru.effects.drawEvent(
            text(d, 'drawEvent') === 'bad' ? 'bad' : 'good',
            effectSource(text(d, 'source')),
          ),
        );
        break;
      case 'game-rerolled':
        facts.push(F.reroll[text(d, 'payment') ?? ''] ?? F.reroll['freeThisRoll'] ?? '');
        break;
      case 'effect-chain-cut':
        facts.push(F.chainCut);
        break;
      default:
        break;
    }
  }
  if (points !== 0 && points === cells) facts.push(F.pointsAndCells(points));
  else {
    if (points !== 0) facts.push(F.points(points));
    if (cells !== 0) facts.push(F.cells(cells));
  }
  if (moved && transferred && cells === 0) facts.push(F.moved);
  if (coins !== 0) facts.push(F.coins(coins));
  return facts;
}

/** One command's events (newest first, as the feed gives them) as one line */
function itemOf(entries: Entry[], refs: FeedRefs): FeedItem | null {
  const newest = entries[0];
  if (!newest) return null;
  const events: Event[] = [...entries]
    .reverse()
    .map((e) => ({ type: e.type, data: record(e.data) }));

  const player = (d: Data): FeedPlayer => {
    const id = text(d, 'playerId') ?? '';
    return (
      refs.players.get(id) ?? {
        id,
        userId: '',
        name: ru.feed.someone,
        token: 0,
        hasProfile: false,
      }
    );
  };
  const context: Context = {
    player,
    playerRef: (d) => ({ kind: 'player', player: player(d) }),
    gameRef: (d) => {
      const id = text(d, 'gameId') ?? refs.runs.get(text(d, 'runId') ?? '') ?? '';
      return {
        kind: 'game',
        game: refs.games.get(id) ?? { id, title: ru.feed.someGame, hasPage: false },
      };
    },
  };

  let lead: Event | undefined;
  let headline: Headline | undefined;
  for (const [type, make] of headlines) {
    lead = events.find((e) => e.type === type);
    if (lead) {
      headline = make(lead.data, context);
      break;
    }
  }
  if (!lead || !headline) {
    // Only what a line would not say (a finish surplus, a resource): no line of its own
    return null;
  }

  return {
    id: newest.commandId,
    sequence: newest.sequence,
    at: newest.occurredAt,
    icon: headline.icon,
    actor: headline.actor,
    line: headline.line,
    facts: [...(headline.facts ?? []), ...factsOf(events, lead)],
    quote: headline.quote ?? null,
    undone: entries.every((e) => e.undone),
  };
}

/** The feed's entries (newest first) as lines, one per command, grouped by the Moscow day */
export function feedDays(
  entries: readonly Entry[],
  refs: FeedRefs,
  now: Date = new Date(),
): FeedDay[] {
  const commands = new Map<string, Entry[]>();
  for (const entry of entries) {
    const group = commands.get(entry.commandId);
    if (group) group.push(entry);
    else commands.set(entry.commandId, [entry]);
  }

  const days: FeedDay[] = [];
  for (const group of commands.values()) {
    const item = itemOf(group, refs);
    if (!item) continue;
    const key = moscowDayKey(item.at);
    const last = days.at(-1);
    if (last?.key === key) last.items.push(item);
    else days.push({ key, label: moscowDayLabel(item.at, now), items: [item] });
  }
  return days;
}
