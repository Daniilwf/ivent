import { useMemo, useState } from 'react';
import { CheckPanel } from '../admin/MapSection';
import { MapCanvas } from '../admin/MapCanvas';
import { branchOptions, graphBoard } from '../board/graphBoard';
import { MapLegend } from '../board/MapLegend';
import { MapView } from '../board/MapView';
import type { Player } from '../board/types';
import { ru } from '../i18n/ru';
import { BranchChoice } from '../season/BranchChoice';
import { demoBranch, demoGraph, demoProblems } from './mapFixtures';

const t = ru.styleguide;

/** The season's graph map with a branch to choose: the desktop's stage and the phone's turn card */
export function GraphMapDemo() {
  const { board, cellNumber } = useMemo(() => graphBoard(demoGraph), []);
  const at = (id: string) => cellNumber.get(id) ?? 1;
  const players: Player[] = [
    { id: 'g1', name: 'Маша', token: 0, cell: at('s4'), points: 41, first: true },
    { id: 'g2', name: 'Петя', token: 1, cell: at('m1'), points: 33 },
    { id: 'g3', name: 'Вася', token: 2, cell: at('fork'), points: 27, me: true },
    { id: 'g4', name: 'Лёша', token: 3, cell: at('c1'), points: 12 },
  ];
  const options = branchOptions(demoBranch, demoGraph, cellNumber);
  const card = (
    <BranchChoice
      steps={demoBranch.steps ?? 1}
      options={options}
      pending={false}
      onChoose={() => undefined}
    />
  );
  return (
    <div className="grid gap-4">
      <div className="relative">
        <MapView
          board={board}
          players={players}
          options={options.map((o) => o.cell)}
          className="h-150 rounded-lg border-3 border-ink"
        />
        <div className="absolute bottom-3 left-3 z-10 hidden w-96 rounded-lg border-3 border-ink bg-card p-4 shadow-lift desk:block">
          {card}
        </div>
      </div>
      <div className="grid gap-4 desk:grid-cols-2">
        <div className="grid content-start gap-2 desk:hidden">
          <span className="text-sm text-ink-soft">{t.graph.phone}</span>
          <div className="rounded-lg bg-card p-4">{card}</div>
        </div>
        <MapLegend
          zones={demoGraph.zones}
          kinds={new Set(demoGraph.cells.map((c) => c.type))}
          myZone={null}
        />
      </div>
    </div>
  );
}

/** The editor's canvas with the check's problems marked, and the check in words */
export function EditorDemo() {
  const [selected, setSelected] = useState<string | null>('fork');
  const problems = new Set(demoProblems.map((p) => p.subject));
  return (
    <div className="grid gap-4">
      <MapCanvas
        draft={demoGraph}
        selected={selected}
        problems={problems}
        players={new Map([['fork', 1]])}
        onSelect={setSelected}
        onMove={() => undefined}
        onConnect={() => undefined}
      />
      <CheckPanel
        check={{
          kind: 'done',
          result: {
            canPublish: false,
            problems: demoProblems,
            warnings: [{ zoneId: 'city', available: 6, wanted: 15 }],
          },
        }}
        draft={demoGraph}
        onRetry={() => undefined}
        onSelect={setSelected}
      />
    </div>
  );
}
