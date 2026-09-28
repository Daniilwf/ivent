import { act, render, screen, waitFor } from '@testing-library/react';
import type { MotionValue } from 'motion/react';
import { describe, expect, it, vi } from 'vitest';
import { ru } from '../i18n/ru';
import { WheelMoment, type WheelRoll } from './Wheel';

// Every spin ends at once: the test counts the spins, not the frames
const spins = vi.hoisted(() => ({ count: 0 }));
vi.mock('motion/react', async (original) => {
  const motion = await original<typeof import('motion/react')>();
  return {
    ...motion,
    useReducedMotion: () => false,
    animate: (value: MotionValue<number>, to: number) => {
      spins.count++;
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
});
