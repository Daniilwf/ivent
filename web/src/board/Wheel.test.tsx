import { act, render, screen, waitFor } from '@testing-library/react';
import type { MotionValue } from 'motion/react';
import { StrictMode } from 'react';
import { describe, expect, it, vi } from 'vitest';
import { ru } from '../i18n/ru';
import { WheelMoment, type WheelRoll } from './Wheel';

// Every spin ends at once: the test counts the spins, not the frames
const spins = vi.hoisted(() => ({
  count: 0,
  loaderBlocked: false,
  last: null as {
    distance: number;
    duration: number;
    ease: [number, number, number, number];
  } | null,
}));
vi.mock('./loadMotion', async (original) => {
  const loader = await original<typeof import('./loadMotion')>();
  return {
    loadMotion: () => (spins.loaderBlocked ? new Promise<never>(() => {}) : loader.loadMotion()),
  };
});
vi.mock('motion/react', async (original) => {
  const motion = await original<typeof import('motion/react')>();
  return {
    ...motion,
    useReducedMotion: () => false,
    animate: (
      value: MotionValue<number>,
      to: number,
      { duration, ease }: { duration: number; ease: [number, number, number, number] },
    ) => {
      spins.count++;
      spins.last = { distance: Math.abs(to - value.get()), duration, ease };
      value.set(to);
      return Object.assign(Promise.resolve(), { stop: () => undefined });
    },
  };
});

const roll: WheelRoll = {
  id: 7,
  misses: [{ sector: 1, game: { title: 'Outlast' }, by: 'Петя' }],
  pick: { sector: 1, game: { title: 'Silent Hill' } },
};

describe('the wheel moment', () => {
  it('draws the sectors it is given', () => {
    render(<WheelMoment sectors={['Action', 'Horror', 'RPG']} roll={null} />);

    for (const name of ['Action', 'Horror', 'RPG']) {
      expect(screen.getByText(name)).toBeInTheDocument();
    }
  });

  it('spins to a distant result in 3.2 seconds without exceeding 720 degrees per second', async () => {
    const phase = vi.fn();
    render(
      <WheelMoment
        sectors={['A', 'B', 'C', 'D', 'E', 'F', 'G', 'H']}
        roll={{ ...roll, misses: [], pick: { ...roll.pick, sector: 7 } }}
        onPhase={phase}
      />,
    );
    await waitFor(() => {
      expect(phase).toHaveBeenCalledExactlyOnceWith('done');
    });
    const spin = spins.last;
    if (!spin) throw new Error('The wheel did not turn');
    expect(spin.duration).toBe(3.2);
    const [x1, y1, x2, y2] = spin.ease;
    const derivative = (t: number, a: number, b: number) =>
      3 * ((1 - t) ** 2 * a + 2 * t * (1 - t) * (b - a) + t ** 2 * (1 - b));
    for (let i = 0; i <= 100; i++) {
      const t = i / 100;
      const slope = derivative(t, y1, y2) / derivative(t, x1, x2);
      expect((spin.distance / spin.duration) * slope).toBeLessThanOrEqual(720);
    }
  });

  it('does not start over when the page hands in the same roll as a new object', async () => {
    const phase = vi.fn();
    spins.count = 0;
    const { rerender } = render(
      <WheelMoment sectors={['Action', 'Horror']} roll={roll} onPhase={phase} />,
    );
    // The first spin stopped on the miss; the wheel rests there
    expect(await screen.findByText(ru.moments.wheel.miss('Петя'))).toBeInTheDocument();

    // A season refresh while the wheel rests: an equal roll, a new object
    await act(async () => {
      rerender(<WheelMoment sectors={['Action', 'Horror']} roll={{ ...roll }} onPhase={phase} />);
      await Promise.resolve();
    });

    await waitFor(
      () => {
        expect(phase).toHaveBeenCalledWith('done');
      },
      { timeout: 3000 },
    );
    // One spin to the miss and one on to the pick: nothing played twice
    expect(spins.count).toBe(2);
    expect(phase.mock.calls.filter(([p]) => p === 'done')).toHaveLength(1);
  });

  it('plays only the first two misses and counts the other misses in the result', async () => {
    const skippedMisses = [
      { sector: 1, game: { title: 'Third miss' }, by: 'Лиса' },
      { sector: 1, game: { title: 'Fourth miss' }, by: 'Бобр' },
    ];
    spins.count = 0;
    const phase = vi.fn();
    vi.useFakeTimers();
    try {
      render(
        <WheelMoment
          sectors={['Action', 'Horror']}
          roll={{
            ...roll,
            misses: [
              ...roll.misses,
              { sector: 1, game: { title: 'Second miss' }, by: 'Сова' },
              ...skippedMisses,
            ],
          }}
          onPhase={phase}
        />,
      );
      await act(() => vi.advanceTimersByTimeAsync(2100));
      expect(spins.count).toBe(3);
      expect(phase).toHaveBeenCalledExactlyOnceWith('done');
      expect(spins.last?.duration).toBe(1.4);
      expect(spins.last?.distance).toBeLessThanOrEqual(360);
      await act(() => vi.advanceTimersByTimeAsync(450));
      expect(screen.getByText(/И ещё 2 промаха/u)).toBeVisible();
    } finally {
      vi.useRealTimers();
    }
  });

  it('shows the server pick by 3.5 seconds while motion is still downloading', async () => {
    const phase = vi.fn();
    spins.loaderBlocked = true;
    vi.useFakeTimers();
    try {
      render(<WheelMoment sectors={['Action', 'Horror']} roll={roll} onPhase={phase} />);
      expect(screen.queryByText('Silent Hill')).not.toBeInTheDocument();

      await act(async () => {
        await vi.advanceTimersByTimeAsync(3490);
      });

      expect(screen.getByText('Silent Hill')).toBeVisible();
      expect(phase).toHaveBeenCalledExactlyOnceWith('done');
    } finally {
      vi.useRealTimers();
      spins.loaderBlocked = false;
    }
  });

  it('runs one animation sequence after a StrictMode remount', async () => {
    const phase = vi.fn();
    spins.count = 0;
    render(
      <StrictMode>
        <WheelMoment sectors={['Action', 'Horror']} roll={roll} onPhase={phase} />
      </StrictMode>,
    );

    await waitFor(
      () => {
        expect(phase).toHaveBeenCalledExactlyOnceWith('done');
      },
      { timeout: 3000 },
    );

    expect(spins.count).toBe(2);
  });
});
