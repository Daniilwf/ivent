import {
  applyNodeChanges,
  Background,
  Handle,
  Panel as FlowPanel,
  Position,
  ReactFlow,
  type Edge as FlowEdge,
  type Node as FlowNode,
  type NodeProps,
  useReactFlow,
} from '@xyflow/react';
import '@xyflow/react/dist/base.css';
import { Flag, GitFork, Maximize, Minus, Orbit, Play, Plus, Trophy, Users } from 'lucide-react';
import { memo, useMemo, useState } from 'react';
import { graphBoard } from '../board/graphBoard';
import { ru } from '../i18n/ru';
import { IconButton } from '../ui/Button';
import { cx } from '../ui/cx';
import type { Draft } from './mapDraft';

const t = ru.admin.map;

type CellData = {
  label: string;
  /** The cell's number as the players see it on their map */
  number: number;
  type: string;
  amount: number | null;
  zone: string | null;
  players: number;
  problem: boolean;
};
type CellNode = FlowNode<CellData, 'cell'>;

const icons: Record<string, React.ReactNode> = {
  start: <Play size={20} aria-hidden />,
  finish: <Trophy size={20} aria-hidden />,
  fork: <GitFork size={20} aria-hidden />,
  teleport: <Orbit size={20} aria-hidden />,
  checkpoint: <Flag size={20} aria-hidden />,
};

/** A cell on the canvas: its id, its type and what stands there; red when the check found a problem with it */
const CellBox = memo(function CellBox({ data, selected }: NodeProps<CellNode>) {
  // A compact card: the number the players see, what the cell does, a bonus's amount, who stands there; the id, the
  // type's name and the zone are in the card's title and in the panel when the cell is picked (D-320)
  const title = [
    data.label,
    t.types[data.type] ?? data.type,
    data.zone,
    data.players > 0 ? t.cell.onCell(data.players) : null,
  ]
    .filter(Boolean)
    .join(', ');
  return (
    <div
      data-testid={`canvas-cell-${data.label}`}
      title={title}
      aria-label={title}
      className={cx(
        'flex items-center gap-1 rounded-full border-3 bg-card py-1 pr-3 pl-1 font-display text-xl font-heavy text-ink',
        data.problem ? 'border-danger' : 'border-ink',
        data.zone && 'bg-page',
        selected && 'outline-3 outline-offset-2 outline-me',
      )}
    >
      <Handle
        type="target"
        position={Position.Left}
        className="size-3! border-2! border-ink! bg-card!"
      />
      <span className="grid size-10 shrink-0 place-items-center rounded-full border-2 border-ink">
        {data.number}
      </span>
      {icons[data.type] ?? null}
      {data.type === 'pointsBonus' && data.amount ? ru.board.bonus(data.amount) : null}
      {data.players > 0 ? (
        <span className="flex items-center gap-1 text-base text-me">
          <Users size={16} aria-hidden />
          {data.players}
        </span>
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
 * The canvas spreads the map's places: a node card is wider than a cell on the players' map, so at the map's own
 * places the cards would overlap. The draft keeps the map's places; a drop divides back.
 */
const spread = 1.4;

/** The zoom buttons of the player's map, on the canvas */
function CanvasTools() {
  const flow = useReactFlow();
  return (
    <FlowPanel position="bottom-right" className="flex gap-2">
      <IconButton label={ru.board.zoomIn} onClick={() => void flow.zoomIn()}>
        <Plus size={20} />
      </IconButton>
      <IconButton label={ru.board.zoomOut} onClick={() => void flow.zoomOut()}>
        <Minus size={20} />
      </IconButton>
      <IconButton label={t.fit} onClick={() => void flow.fitView({ padding: 0.08 })}>
        <Maximize size={20} />
      </IconButton>
    </FlowPanel>
  );
}

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
  const { cellNumber } = useMemo(() => graphBoard(draft), [draft]);
  const fromDraft: CellNode[] = draft.cells.map((c, i) => ({
    id: c.id,
    type: 'cell',
    position: { x: (c.x ?? i * 140) * spread, y: (c.y ?? 0) * spread },
    selected: c.id === selected,
    ariaLabel: t.cell.title(c.id),
    data: {
      label: c.id,
      number: cellNumber.get(c.id) ?? 0,
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
  // A node being dragged keeps its place until it is dropped: a check's answer must not pull it back mid-drag
  const [dragging, setDragging] = useState(false);
  if (shown !== key && !dragging) {
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
        labelStyle: { fill: 'var(--color-ink)', fontWeight: 700 },
        labelBgStyle: { fill: 'var(--color-card)' },
        labelBgPadding: [6, 3] as [number, number],
        labelBgBorderRadius: 4,
        markerEnd: { type: 'arrowclosed' as const, color: 'var(--color-ink)' },
        style: {
          stroke: 'var(--color-ink)',
          strokeWidth: e.isDefaultForward ? 3 : 2,
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
              markerEnd: { type: 'arrowclosed' as const, color: 'var(--color-ink)' },
              // As on the player's map: a dashed jump
              style: { stroke: 'var(--color-ink)', strokeWidth: 3, strokeDasharray: '10 8' },
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
        // Text on the canvas never shrinks below the small text size of the site
        fitViewOptions={{ padding: 0.15, minZoom: 0.5, maxZoom: 1.2 }}
        minZoom={0.5}
        deleteKeyCode={null}
        onNodeClick={(_, node) => {
          onSelect(node.id);
        }}
        onPaneClick={() => {
          onSelect(null);
        }}
        // One cell moves at a time: the draft takes the place of the node that was dropped
        multiSelectionKeyCode={null}
        selectionKeyCode={null}
        onNodeDragStart={() => {
          setDragging(true);
        }}
        onNodeDragStop={(_, node) => {
          setDragging(false);
          onMove(node.id, { x: node.position.x / spread, y: node.position.y / spread });
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
        <CanvasTools />
      </ReactFlow>
    </div>
  );
}
