import { useMemo, useState } from 'react';
import { MapBackgroundPanel } from '../admin/MapBackgroundPanel';
import { CheckPanel } from '../admin/MapSection';
import { MapCanvas } from '../admin/MapCanvas';
import { branchOptions, graphBoard } from '../board/graphBoard';
import { MapLegend } from '../board/MapLegend';
import { MapView } from '../board/MapView';
import type { Player } from '../board/types';
import { ru } from '../i18n/ru';
import { BranchChoice } from '../season/BranchChoice';
import mapBackground from './mapBackground.svg';
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

/** The picture made for the styleguide's map (scripts/map-background.mjs), at its own pixels */
const demoBackground = { fileId: 'styleguide', width: 1240, height: 620, url: mapBackground };

/** The map over the admin's picture, and the editor's background panel in each of its states (D-277) */
export function MapBackgroundDemo() {
  const { board, cellNumber } = useMemo(
    () => graphBoard({ ...demoGraph, background: demoBackground }),
    [],
  );
  const at = (id: string) => cellNumber.get(id) ?? 1;
  const players: Player[] = [
    { id: 'b1', name: 'Маша', token: 0, cell: at('s4'), points: 41, first: true },
    { id: 'b2', name: 'Вася', token: 2, cell: at('c3'), points: 27, me: true },
  ];
  const s = t.mapBackground;
  const states = [
    { label: s.none, background: null, state: { kind: 'idle' as const } },
    { label: s.uploading, background: null, state: { kind: 'uploading' as const } },
    {
      label: s.failed,
      background: null,
      state: { kind: 'failed' as const, reason: ru.upload.errors['file.tooLarge'] },
    },
    { label: s.ready, background: demoBackground, state: { kind: 'idle' as const } },
  ];
  return (
    <div className="grid gap-4">
      <MapView board={board} players={players} className="h-120 rounded-lg border-3 border-ink" />
      <div className="grid gap-4 desk:grid-cols-2">
        {states.map((x) => (
          <div key={x.label} className="grid content-start gap-2">
            <span className="text-sm text-ink-soft">{x.label}</span>
            <MapBackgroundPanel
              seasonId="styleguide"
              background={x.background}
              state={x.state}
              pending={x.background !== null}
              preview={mapBackground}
              onChange={() => undefined}
            />
          </div>
        ))}
      </div>
    </div>
  );
}
