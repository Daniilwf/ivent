import { render, screen, waitFor } from '@testing-library/react';
import { createRef } from 'react';
import { describe, expect, it, vi } from 'vitest';
import { ru } from '../i18n/ru';
import { DiceMoment } from './Dice';
import type { MomentHandle } from './moment';
import { WheelMoment } from './Wheel';

// The viewer asks for reduced motion: every moment shows its result at once, in words too (docs/DESIGN.md «Движение»)
vi.hoisted(() => {
  Object.defineProperty(window, 'matchMedia', {
    configurable: true,
    value: (query: string) => ({
      matches: query.includes('prefers-reduced-motion'),
      media: query,
      addEventListener: () => undefined,
      removeEventListener: () => undefined,
      addListener: () => undefined,
      removeListener: () => undefined,
      onchange: null,
      dispatchEvent: () => false,
    }),
  });
});

describe('the main moments with reduced motion', () => {
  it('the dice show the total at once and say every die, the challenge one apart', async () => {
    const phase = vi.fn();
    render(<DiceMoment roll={{ id: 1, values: [2, 5, 3, 6], challenge: true }} onPhase={phase} />);
    await waitFor(() => {
      expect(phase).toHaveBeenCalledWith('done');
    });
    expect(screen.getByText('+16')).toBeInTheDocument();
    expect(screen.getByText(ru.moments.dice.result([2, 5, 3], 6, 16))).toBeInTheDocument();
  });

  it('the wheel stands on the pick at once and still tells the miss and who completed it', async () => {
    const phase = vi.fn();
    render(
      <WheelMoment
        sectors={['Action', 'Horror', 'RPG']}
        roll={{
          id: 1,
          misses: [{ sector: 1, game: { title: 'Hollow Knight' }, by: 'Сова' }],
          pick: { sector: 2, game: { title: 'Dead Cells' } },
        }}
        onPhase={phase}
      />,
    );
    await waitFor(() => {
      expect(phase).toHaveBeenCalledWith('done');
    });
    const said = screen.getByText(new RegExp(ru.moments.wheel.result('Dead Cells')));
    expect(said).toHaveTextContent(ru.moments.wheel.miss('Сова'));
    expect(said).toHaveTextContent(ru.moments.wheel.category('RPG'));
  });

  it('a skipped moment ends once', async () => {
    const phase = vi.fn();
    const handle = createRef<MomentHandle>();
    render(
      <DiceMoment
        ref={handle}
        roll={{ id: 1, values: [1, 1], challenge: false }}
        onPhase={phase}
      />,
    );
    handle.current?.skip();
    handle.current?.skip();
    await waitFor(() => {
      expect(phase).toHaveBeenCalledWith('done');
    });
    expect(phase.mock.calls.filter(([p]) => p === 'done')).toHaveLength(1);
  });
});
