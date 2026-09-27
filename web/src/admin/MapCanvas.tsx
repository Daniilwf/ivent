import {
  applyNodeChanges,
  Background,
  Controls,
  Handle,
  Position,
  ReactFlow,
  type Edge as FlowEdge,
  type Node as FlowNode,
  type NodeProps,
} from '@xyflow/react';
import '@xyflow/react/dist/base.css';
import { Flag, GitFork, Orbit, Play, Trophy } from 'lucide-react';
import { memo, useMemo, useState } from 'react';
import { ru } from '../i18n/ru';
import { cx } from '../ui/cx';
import type { Draft } from './mapDraft';

const t = ru.admin.map;

type CellData = {
  label: string;
  type: string;
  amount: number | null;
  zone: string | null;
  players: number;
  problem: boolean;
};
type CellNode = FlowNode<CellData, 'cell'>;

const icons: Record<string, React.ReactNode> = {
  start: <Play size={14} aria-hidden />,
  finish: <Trophy size={14} aria-hidden />,
  fork: <GitFork size={14} aria-hidden />,
  teleport: <Orbit size={14} aria-hidden />,
  checkpoint: <Flag size={14} aria-hidden />,
};

/** A cell on the canvas: its id, its type and what stands there; red when the check found a problem with it */
const CellBox = memo(function CellBox({ data, selected }: NodeProps<CellNode>) {
  return (
    <div
      data-testid={`canvas-cell-${data.label}`}
      className={cx(
        'grid min-w-24 gap-1 rounded-md border-3 bg-card px-3 py-2 text-sm text-ink',
        data.problem ? 'border-danger' : 'border-ink',
        selected && 'outline-3 outline-offset-2 outline-me',
      )}
    >
      <Handle
        type="target"
        position={Position.Left}
        className="size-3! border-2! border-ink! bg-card!"
      />
      <span className="flex items-center gap-1 font-display font-heavy">
        {icons[data.type] ?? null}
        {data.label}
      </span>
      <span className="text-xs text-ink-soft">
        {t.types[data.type] ?? data.type}
        {data.type === 'pointsBonus' && data.amount ? ` ${ru.board.bonus(data.amount)}` : ''}
      </span>
      {data.zone ? <span className="text-xs font-bold">{data.zone}</span> : null}
      {data.players > 0 ? (
        <span className="text-xs font-bold text-me">{ru.board.more(data.players)}</span>
      ) : null}
      <Handle
        type="source"
        position={Position.Right}
        className="size-3! border-2! border-ink! bg-me!"
      />
    </div>
  );
});

const nodeTypes = { cell: CellBox };

/**
 * The map on a React Flow canvas (SPEC «Трудности реализации»): cells to drag, arrows drawn from a cell's right edge
 * to another cell. The default branch of a fork and the primary entry of a joint are labelled on their arrows; a
 * teleport's jump is a dashed line. Everything here can also be done in the panel beside it.
 */
export function MapCanvas({
  draft,
  selected,
  problems,
  players,
  onSelect,
  onMove,
  onConnect,
}: {
  draft: Draft;
  selected: string | null;
  /** The cells the check found problems with */
  problems: ReadonlySet<string>;
  /** How many players stand on each cell */
  players: ReadonlyMap<string, number>;
  onSelect: (id: string | null) => void;
  onMove: (id: string, at: { x: number; y: number }) => void;
  onConnect: (from: string, to: string) => void;
}) {
  const zoneName = useMemo(() => new Map(draft.zones.map((z) => [z.id, z.name])), [draft.zones]);
  const fromDraft: CellNode[] = draft.cells.map((c, i) => ({
    id: c.id,
    type: 'cell',
    position: { x: c.x ?? i * 140, y: c.y ?? 0 },
    selected: c.id === selected,
    ariaLabel: t.cell.title(c.id),
    data: {
      label: c.id,
      type: c.type,
      amount: c.amount ?? null,
      zone: c.zone ? (zoneName.get(c.zone) ?? c.zone) : null,
      players: players.get(c.id) ?? 0,
      problem: problems.has(c.id),
    },
  }));
  // The canvas moves its nodes itself while dragging; the draft takes the place when the drag ends
  const [nodes, setNodes] = useState(fromDraft);
  const key = JSON.stringify(fromDraft);
  const [shown, setShown] = useState(key);
  if (shown !== key) {
    setShown(key);
    setNodes(fromDraft);
  }
  const exits = new Map<string, number>();
  const entries = new Map<string, number>();
  for (const e of draft.edges) {
    exits.set(e.from, (exits.get(e.from) ?? 0) + 1);
    entries.set(e.to, (entries.get(e.to) ?? 0) + 1);
  }
  const edges: FlowEdge[] = [
    ...draft.edges.map((e) => {
      const tags = [
        (exits.get(e.from) ?? 0) > 1 && e.isDefaultForward ? t.edge.defaultTag : '',
        (entries.get(e.to) ?? 0) > 1 && e.isPrimaryBackward ? t.edge.primaryTag : '',
      ].filter(Boolean);
      return {
        id: `${e.from}>${e.to}`,
        source: e.from,
        target: e.to,
        label: tags.length > 0 ? tags.join(', ') : undefined,
        markerEnd: { type: 'arrowclosed' as const, color: 'var(--color-ink)' },
        style: {
          stroke: 'var(--color-ink)',
          strokeWidth: e.isDefaultForward ? 3 : 2,
          strokeDasharray: e.isDefaultForward ? undefined : '6 4',
        },
      };
    }),
    ...draft.cells.flatMap((c) =>
      c.type === 'teleport' && c.to
        ? [
            {
              id: `teleport:${c.id}`,
              source: c.id,
              target: c.to,
              selectable: false,
              focusable: false,
              markerEnd: { type: 'arrowclosed' as const, color: 'var(--color-me)' },
              style: { stroke: 'var(--color-me)', strokeWidth: 2, strokeDasharray: '2 6' },
            },
          ]
        : [],
    ),
  ];

  return (
    <div
      className="h-160 overflow-hidden rounded-lg border-3 border-ink bg-page"
      data-testid="map-canvas"
      aria-label={t.canvas}
      role="group"
    >
      <ReactFlow
        nodes={nodes}
        edges={edges}
        nodeTypes={nodeTypes}
        fitView
        minZoom={0.2}
        deleteKeyCode={null}
        onNodeClick={(_, node) => {
          onSelect(node.id);
        }}
        onPaneClick={() => {
          onSelect(null);
        }}
        onNodeDragStop={(_, node) => {
          onMove(node.id, node.position);
        }}
        onNodesChange={(changes) => {
          setNodes((current) => applyNodeChanges(changes, current));
          for (const change of changes)
            if (change.type === 'select' && change.selected) onSelect(change.id);
        }}
        onConnect={(c) => {
          onConnect(c.source, c.target);
        }}
      >
        <Background />
        <Controls showInteractive={false} />
      </ReactFlow>
    </div>
  );
}
