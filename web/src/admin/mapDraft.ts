import type { Schemas } from '../api/client';

// The map editor's draft (2.11): the season's map as the admin changes it before publishing. Every change is a pure
// function of the draft, so the editor, the keyboard panel and the tests share one set of rules. The engine checks
// the result (MapValidator); these functions only keep the draft consistent where it is cheap: a removed cell takes
// its arrows, a type change drops the parameters of the old type, the first exit is the default branch, the first
// entry the primary one.

export type Cell = Schemas['CellView'];
export type Edge = Schemas['EdgeView'];
export type Zone = Schemas['ZoneView'];
export type Draft = { cells: Cell[]; edges: Edge[]; zones: Zone[] };
export type CellType = Cell['type'];

/** The draft of a map that came from the server: lists only, no nulls where the editor expects values */
export function draftOf(map: Schemas['MapGraphView']): Draft {
  return { cells: map.cells ?? [], edges: map.edges ?? [], zones: map.zones ?? [] };
}

/** A new id not used by any cell: `c1`, `c2`… */
export function freeId(draft: Draft, prefix = 'c'): string {
  const used = new Set(draft.cells.map((c) => c.id));
  for (let n = 1; ; n++) if (!used.has(`${prefix}${n}`)) return `${prefix}${n}`;
}

export function addCell(draft: Draft, at: { x: number; y: number }, type: CellType = 'empty') {
  const id = freeId(draft);
  return {
    id,
    draft: {
      ...draft,
      cells: [...draft.cells, { id, type, x: Math.round(at.x), y: Math.round(at.y) }],
    },
  };
}

/** A cell goes with its arrows; a teleport that led there loses its destination */
export function removeCell(draft: Draft, id: string): Draft {
  return {
    ...draft,
    cells: draft.cells
      .filter((c) => c.id !== id)
      .map((c) => (c.to === id ? { ...c, to: null } : c)),
    edges: draft.edges.filter((e) => e.from !== id && e.to !== id),
  };
}

export function moveCell(draft: Draft, id: string, at: { x: number; y: number }): Draft {
  return {
    ...draft,
    cells: draft.cells.map((c) =>
      c.id === id ? { ...c, x: Math.round(at.x), y: Math.round(at.y) } : c,
    ),
  };
}

/** A new type keeps the zone and the place; the parameters of the old type go, the new type's get a start value */
export function setCellType(draft: Draft, id: string, type: CellType): Draft {
  return {
    ...draft,
    cells: draft.cells.map((c) =>
      c.id === id
        ? {
            id: c.id,
            type,
            zone: c.zone ?? null,
            x: c.x ?? null,
            y: c.y ?? null,
            ...(type === 'pointsBonus' ? { amount: c.amount ?? 1 } : {}),
            ...(type === 'teleport' ? { to: c.to ?? null } : {}),
          }
        : c,
    ),
  };
}

export function setCell(
  draft: Draft,
  id: string,
  patch: Partial<Pick<Cell, 'zone' | 'to' | 'amount'>>,
): Draft {
  return { ...draft, cells: draft.cells.map((c) => (c.id === id ? { ...c, ...patch } : c)) };
}

/**
 * An arrow between two cells, once: the first exit of a cell is its default branch, the first entry of a cell its
 * primary incoming edge. A loop or a repeated arrow changes nothing.
 */
export function addEdge(draft: Draft, from: string, to: string): Draft {
  if (from === to || draft.edges.some((e) => e.from === from && e.to === to)) return draft;
  return {
    ...draft,
    edges: [
      ...draft.edges,
      {
        from,
        to,
        isDefaultForward: !draft.edges.some((e) => e.from === from),
        isPrimaryBackward: !draft.edges.some((e) => e.to === to),
      },
    ],
  };
}

/** Removing the default branch or the primary entry hands the mark to the next arrow of the cell, if any */
export function removeEdge(draft: Draft, from: string, to: string): Draft {
  const gone = draft.edges.find((e) => e.from === from && e.to === to);
  if (!gone) return draft;
  let edges = draft.edges.filter((e) => e !== gone);
  const nextOut = edges.find((e) => e.from === from);
  if (gone.isDefaultForward && nextOut && !edges.some((e) => e.from === from && e.isDefaultForward))
    edges = edges.map((e) => (e === nextOut ? { ...e, isDefaultForward: true } : e));
  const nextIn = edges.find((e) => e.to === to);
  if (gone.isPrimaryBackward && nextIn && !edges.some((e) => e.to === to && e.isPrimaryBackward))
    edges = edges.map((e) => (e === nextIn ? { ...e, isPrimaryBackward: true } : e));
  return { ...draft, edges };
}

/** One default branch among a cell's exits */
export function setDefaultBranch(draft: Draft, from: string, to: string): Draft {
  return {
    ...draft,
    edges: draft.edges.map((e) => (e.from === from ? { ...e, isDefaultForward: e.to === to } : e)),
  };
}

/** One primary incoming edge among a cell's entries */
export function setPrimaryEntry(draft: Draft, from: string, to: string): Draft {
  return {
    ...draft,
    edges: draft.edges.map((e) => (e.to === to ? { ...e, isPrimaryBackward: e.from === from } : e)),
  };
}

export function addZone(draft: Draft, name: string): { id: string; draft: Draft } {
  const used = new Set(draft.zones.map((z) => z.id));
  let n = 1;
  while (used.has(`zone-${n}`)) n++;
  const id = `zone-${n}`;
  return { id, draft: { ...draft, zones: [...draft.zones, { id, name }] } };
}

export function setZone(draft: Draft, id: string, patch: Partial<Omit<Zone, 'id'>>): Draft {
  return { ...draft, zones: draft.zones.map((z) => (z.id === id ? { ...z, ...patch } : z)) };
}

/** A zone goes from its cells too */
export function removeZone(draft: Draft, id: string): Draft {
  return {
    ...draft,
    zones: draft.zones.filter((z) => z.id !== id),
    cells: draft.cells.map((c) => (c.zone === id ? { ...c, zone: null } : c)),
  };
}

/**
 * The draft as the server takes it: optional fields left out when empty, so a draft equals the published map when
 * nothing changed (the check says «nothing to publish» then)
 */
export function normalized(draft: Draft): Draft {
  // Keys in one fixed order, whatever order the edits added them in: the server's map and a draft of the same map
  // serialize alike (sameMap compares the text)
  const clean = <T extends object>(o: T, order: readonly (keyof T)[]): T =>
    Object.fromEntries(
      order
        .map((key) => [key, o[key]] as const)
        .filter(([, v]) => v !== null && v !== undefined && v !== ''),
    ) as T;
  const cellKeys = ['id', 'type', 'zone', 'to', 'amount', 'deck', 'grants', 'x', 'y'] as const;
  const edgeKeys = ['from', 'to', 'isDefaultForward', 'isPrimaryBackward'] as const;
  const zoneKeys = [
    'id',
    'name',
    'rollFilter',
    'diceModifier',
    'dropPenaltyMultiplier',
    'deck',
    'shopPriceMultiplier',
  ] as const;
  const filterKeys = ['tags', 'minHours', 'maxHours', 'releaseYearBefore'] as const;
  return {
    cells: draft.cells.map((c) => clean(c, cellKeys)),
    edges: draft.edges.map((e) => clean(e, edgeKeys)),
    zones: draft.zones.map((z) => {
      const zone = clean(z, zoneKeys);
      if (zone.rollFilter) {
        const filter = clean(zone.rollFilter, filterKeys);
        if (filter.tags?.length === 0) delete filter.tags;
        if (Object.keys(filter).length === 0) delete zone.rollFilter;
        else zone.rollFilter = filter;
      }
      if (zone.diceModifier) zone.diceModifier = clean(zone.diceModifier, ['stage', 'value']);
      return zone;
    }),
  };
}

export function sameMap(a: Draft, b: Draft): boolean {
  return JSON.stringify(normalized(a)) === JSON.stringify(normalized(b));
}

/** Where the draft is kept between visits: this browser only, per season (a convenience, not a store) */
const storageKey = (seasonId: string) => `map-draft:${seasonId}`;

const isDraft = (value: unknown): value is Draft =>
  typeof value === 'object' &&
  value !== null &&
  Array.isArray((value as Draft).cells) &&
  Array.isArray((value as Draft).edges) &&
  Array.isArray((value as Draft).zones);

/**
 * The kept draft, if it was made from the map published now. A draft of an older map (someone published since, from
 * another browser) is not opened over the new one: publishing it would undo their changes silently (D-314).
 */
export function loadDraft(
  seasonId: string,
  published: Draft,
): { draft: Draft | null; stale: boolean } {
  try {
    const raw = localStorage.getItem(storageKey(seasonId));
    if (!raw) return { draft: null, stale: false };
    const value = JSON.parse(raw) as { base?: unknown; draft?: unknown };
    if (!isDraft(value.base) || !isDraft(value.draft)) return { draft: null, stale: false };
    if (!sameMap(value.base, published)) {
      localStorage.removeItem(storageKey(seasonId));
      return { draft: null, stale: true };
    }
    return { draft: value.draft, stale: false };
  } catch {
    return { draft: null, stale: false };
  }
}

/** Keeps the draft with the published map it was made from; none forgets it */
export function saveDraft(seasonId: string, draft: { base: Draft; draft: Draft } | null) {
  try {
    if (draft) localStorage.setItem(storageKey(seasonId), JSON.stringify(draft));
    else localStorage.removeItem(storageKey(seasonId));
  } catch {
    // A private window or blocked storage: the draft lives while the page is open
  }
}

/** A map from the server as the editor opens it: every cell with a place (a map without them is laid out once) */
export function placed(
  draft: Draft,
  place: (id: string) => { x: number; y: number } | undefined,
): Draft {
  if (draft.cells.every((c) => c.x != null && c.y != null)) return draft;
  return {
    ...draft,
    cells: draft.cells.map((c) => {
      const at = place(c.id);
      return at ? { ...c, x: Math.round(at.x), y: Math.round(at.y) } : c;
    }),
  };
}

/**
 * Problems that follow from others: a cell cut off from the start or from the finish is the consequence of a missing
 * or wrong arrow elsewhere. The editor names the causes one by one and these in one line each (D-320).
 */
export const consequenceCodes: ReadonlySet<string> = new Set([
  'map.unreachable',
  'map.finishUnreachable',
]);

/** The cell a problem is about (its subject), or null for the map, an arrow or a zone */
export function problemCell(draft: Draft, subject: string): string | null {
  return draft.cells.some((c) => c.id === subject) ? subject : null;
}
