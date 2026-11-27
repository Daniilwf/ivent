import { act, render, screen } from '@testing-library/react';
import { createRef, StrictMode, useImperativeHandle } from 'react';
import { TokenMove } from './TokenMove';
import type { MapViewProps } from './MapView';
import type { MomentHandle } from './moment';
import type { Board, Player, Point } from './types';

type Animation = Promise<void> & { stop: () => void };
type Animate = (
  from: number,
  to: number,
  options: { duration: number; onUpdate: (value: number) => void },
) => Animation;
type Features = { animate: Animate };

const motion = vi.hoisted(() => ({
  load: vi.fn<() => Promise<Features>>(),
  animate: vi.fn<Animate>(),
}));
const project = vi.hoisted(() => vi.fn<(point: Point) => Point | null>());

vi.mock('./loadMotion', () => ({ loadMotion: () => motion.load() }));
vi.mock('./MapView', () => ({
  MapView: ({ children, ref }: Pick<MapViewProps, 'children' | 'ref'>) => {
    useImperativeHandle(ref, () => ({
      centerOn: vi.fn(),
      planMove: () => ({}),
      project,
      reset: vi.fn(),
    }));
    return <div>{children}</div>;
  },
  MapSticker: ({ at }: { at: Point }) => (
    <output data-testid="position">{`${at.x},${at.y}`}</output>
  ),
}));

const board: Board = {
  width: 400,
  height: 200,
  cells: [
    { id: 1, kind: 'start', x: 50, y: 70 },
    { id: 2, kind: 'plain', x: 150, y: 70 },
    { id: 3, kind: 'finish', x: 350, y: 70 },
  ],
  edges: [],
  roads: [],
  zones: [],
};
const player: Player = { id: 'fox', name: 'Лиса', token: 0, cell: 1, points: 0 };
const path = [1, 2, 3];

async function advance(ms: number) {
  await act(async () => {
    await vi.advanceTimersByTimeAsync(ms);
  });
}

describe('TokenMove budget', () => {
  beforeEach(() => {
    vi.useFakeTimers();
    motion.load.mockReset();
    motion.animate.mockReset();
    project.mockReset();
    motion.animate.mockImplementation((_, to, { onUpdate }) => {
      onUpdate(to);
      return Object.assign(Promise.resolve(), { stop: vi.fn() });
    });
    motion.load.mockResolvedValue({ animate: motion.animate });
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  it('lands within a completion’s smaller budget while the features are still loading', async () => {
    let resolve!: (features: Features) => void;
    const delayed = new Promise<Features>((done) => {
      resolve = done;
    });
    motion.load.mockReturnValue(delayed);
    const phase = vi.fn();
    const { rerender } = render(
      <TokenMove
        board={board}
        players={[player]}
        mover={player}
        path={path}
        budget={0.8}
        onPhase={phase}
      />,
    );

    await advance(700);
    expect(phase).not.toHaveBeenCalled();
    expect(screen.getByTestId('position')).toHaveTextContent('50,70');
    rerender(
      <TokenMove
        board={{ ...board }}
        players={[player]}
        mover={{ ...player }}
        path={[...path]}
        budget={0.8}
        onPhase={phase}
      />,
    );
    await advance(100);

    expect(phase).toHaveBeenCalledExactlyOnceWith('done');
    expect(screen.getByTestId('position')).toHaveTextContent('350,70');
    await act(async () => {
      resolve({ animate: motion.animate });
      await delayed;
    });
    expect(motion.animate).not.toHaveBeenCalled();
    expect(phase).toHaveBeenCalledExactlyOnceWith('done');
    expect(screen.getByTestId('position')).toHaveTextContent('350,70');
  });

  it('starts only one set of hops when StrictMode repeats the effect', async () => {
    const phase = vi.fn();
    render(
      <StrictMode>
        <TokenMove board={board} players={[player]} mover={player} path={path} onPhase={phase} />
      </StrictMode>,
    );

    await advance(0);

    expect(motion.animate).toHaveBeenCalledTimes(2);
    expect(phase).toHaveBeenCalledExactlyOnceWith('done');
    expect(screen.getByTestId('position')).toHaveTextContent('350,70');
  });

  it('reports the exact stage projection before the parent removes the settled path', async () => {
    project.mockReturnValue({ x: 190, y: 240 });
    const onPhase = vi.fn();
    const onArrive = vi.fn();
    render(
      <TokenMove
        board={board}
        players={[player]}
        mover={player}
        path={path}
        onPhase={onPhase}
        onArrive={onArrive}
      />,
    );

    await advance(0);

    expect(project).toHaveBeenCalledExactlyOnceWith(board.cells[2]);
    expect(onArrive).toHaveBeenCalledExactlyOnceWith({ x: 190, y: 240 });
    expect(onArrive).toHaveBeenCalledBefore(onPhase);
    expect(onPhase).toHaveBeenCalledExactlyOnceWith('done');
  });

  it('never reports an arrival projection when the move was skipped', async () => {
    const ref = createRef<MomentHandle>();
    const onPhase = vi.fn();
    const onArrive = vi.fn();
    render(
      <TokenMove
        ref={ref}
        board={board}
        players={[player]}
        mover={player}
        path={path}
        onPhase={onPhase}
        onArrive={onArrive}
      />,
    );

    act(() => ref.current?.skip());
    await advance(3500);

    expect(project).not.toHaveBeenCalled();
    expect(onArrive).not.toHaveBeenCalled();
    expect(onPhase).toHaveBeenCalledExactlyOnceWith('done');
  });

  it('groups a long route into hops that take at least 0.2 seconds and fit the three-second path', async () => {
    const route = Array.from({ length: 40 }, (_, i) => i + 1);
    const longBoard: Board = {
      ...board,
      cells: route.map((id) => ({ id, kind: 'plain', x: id * 10, y: 70 })),
    };
    const phase = vi.fn();
    render(
      <TokenMove
        board={longBoard}
        players={[player]}
        mover={player}
        path={route}
        onPhase={phase}
      />,
    );

    await advance(0);

    const durations = motion.animate.mock.calls.map(([, , options]) => options.duration);
    expect(durations.length).toBeGreaterThan(1);
    expect(durations.every((duration) => duration >= 0.2)).toBe(true);
    expect(durations.reduce((a, b) => a + b, 0)).toBeLessThanOrEqual(3.000001);
    expect(phase).toHaveBeenCalledExactlyOnceWith('done');
    expect(screen.getByTestId('position')).toHaveTextContent('400,70');
  });

  it('stops an in-flight hop at the deadline and ignores any later updates', async () => {
    const stop = vi.fn();
    motion.animate.mockReturnValue(Object.assign(new Promise<void>(() => undefined), { stop }));
    const phase = vi.fn();
    render(
      <TokenMove
        board={board}
        players={[player]}
        mover={player}
        path={path}
        budget={0.8}
        onPhase={phase}
      />,
    );

    await advance(800);

    expect(stop).toHaveBeenCalledTimes(1);
    expect(phase).toHaveBeenCalledExactlyOnceWith('done');
    const update = motion.animate.mock.calls[0]?.[2].onUpdate;
    expect(update).toBeDefined();
    act(() => {
      update?.(0.5);
    });
    expect(screen.getByTestId('position')).toHaveTextContent('350,70');
  });

  it('shows the destination immediately when loading Motion fails', async () => {
    motion.load.mockRejectedValue(new Error('Motion could not load'));
    const phase = vi.fn();
    render(
      <TokenMove board={board} players={[player]} mover={player} path={path} onPhase={phase} />,
    );

    await advance(0);

    expect(screen.getByTestId('position')).toHaveTextContent('350,70');
    expect(phase).toHaveBeenCalledExactlyOnceWith('done');
    expect(motion.animate).not.toHaveBeenCalled();
  });

  it('stops the current animation and clears the deadline on unmount', async () => {
    const stop = vi.fn();
    motion.animate.mockReturnValue(Object.assign(new Promise<void>(() => undefined), { stop }));
    const phase = vi.fn();
    const { unmount } = render(
      <TokenMove board={board} players={[player]} mover={player} path={path} onPhase={phase} />,
    );

    await advance(0);
    expect(motion.animate).toHaveBeenCalledTimes(1);
    unmount();
    await advance(3000);

    expect(stop).toHaveBeenCalledTimes(1);
    expect(phase).not.toHaveBeenCalled();
  });
});
