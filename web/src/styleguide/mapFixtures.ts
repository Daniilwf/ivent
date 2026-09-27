import type { Schemas } from '../api/client';
import type { GraphMap } from '../board/graphBoard';

// A season's graph map for the styleguide and the visual tests (2.10, 2.11): a fork into two zones that meet again
// on a checkpoint, a snake back, a shortcut and a points bonus, as the editor would publish it.

type Cell = Schemas['CellView'];

const cells: Cell[] = [];
const edges: Schemas['EdgeView'][] = [];

/** A run of cells along the points given, each joined to the one before */
function run(from: string | null, spots: [string, number, number, Partial<Cell>?][]) {
  let previous = from;
  for (const [id, x, y, extra] of spots) {
    if (!cells.some((c) => c.id === id)) cells.push({ id, type: 'empty', x, y, ...extra });
    if (previous)
      edges.push({
        from: previous,
        to: id,
        isDefaultForward: !edges.some((e) => e.from === previous),
        isPrimaryBackward: !edges.some((e) => e.to === id),
      });
    previous = id;
  }
}

run(null, [
  ['start', 80, 520, { type: 'start' }],
  ['c1', 200, 520],
  ['c2', 310, 480, { type: 'pointsBonus', amount: 2 }],
  ['c3', 400, 410],
  ['fork', 470, 320, { type: 'fork' }],
]);
// The swamp: long and calm
run('fork', [
  ['s1', 400, 220, { zone: 'swamp' }],
  ['s2', 420, 120, { zone: 'swamp' }],
  ['s3', 530, 70, { zone: 'swamp', type: 'teleport', to: 'c1' }],
  ['s4', 650, 90, { zone: 'swamp' }],
  ['s5', 740, 160, { zone: 'swamp' }],
  ['gate', 790, 270, { type: 'checkpoint' }],
]);
// The city: short and dear
run('fork', [
  ['m1', 580, 330, { zone: 'city' }],
  ['m2', 680, 360, { zone: 'city', type: 'teleport', to: 'f2' }],
  ['gate', 790, 270],
]);
run('gate', [
  ['f1', 880, 350],
  ['f2', 960, 430, { type: 'pointsBonus', amount: 3 }],
  ['f3', 1040, 500],
  ['finish', 1150, 520, { type: 'finish' }],
]);

export const demoGraph: GraphMap = {
  cells,
  edges,
  zones: [
    {
      id: 'swamp',
      name: 'Болото ужаса',
      rollFilter: { tags: ['Horror'] },
      diceModifier: { stage: 'add', value: 1 },
      dropPenaltyMultiplier: 1.5,
    },
    { id: 'city', name: 'Город коротышек', rollFilter: { maxHours: 8 } },
  ],
};

/** The branch choice at the fork: two steps left */
export const demoBranch: Schemas['ChoiceView'] = {
  id: 'c0000000-0000-0000-0000-00000000000b',
  kind: 'branch',
  options: [
    { id: 's1', game: null },
    { id: 'm1', game: null },
  ],
  steps: 3,
};

/** What the editor's check says about a broken draft: a cell with players removed, a dead end */
export const demoProblems: Schemas['MapProblemView'][] = [
  { code: 'map.occupiedCellRemoved', subject: 'c3', message: '' },
  { code: 'map.deadEnd', subject: 'm2', message: '' },
  { code: 'map.primaryBackward', subject: 'gate', message: '' },
];
