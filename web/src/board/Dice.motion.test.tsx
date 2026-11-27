import { act, render, screen } from '@testing-library/react';
import { DiceMoment } from './Dice';

// A slow download must not keep a die facing away from the server's result.
vi.mock('./loadMotion', () => ({ loadMotion: () => new Promise(() => {}) }));

it('shows the saved faces on all ten dice at settlement while Motion is still loading', async () => {
  vi.useFakeTimers();
  try {
    const onPhase = vi.fn();
    const { container } = render(
      <DiceMoment
        roll={{ id: 1, values: [6, 2, 3, 4, 5, 1, 1, 1, 1, 5], challenge: 0 }}
        onPhase={onPhase}
      />,
    );
    expect(container.querySelectorAll('[data-die]')).toHaveLength(10);

    await act(() => vi.advanceTimersByTimeAsync(1599));
    expect(onPhase).not.toHaveBeenCalled();
    await act(() => vi.advanceTimersByTimeAsync(1));

    expect(onPhase).toHaveBeenCalledExactlyOnceWith('done');
    expect(screen.getByTestId('dice-equation')).toHaveTextContent(
      '6 + 2 + 3 + 4 + 5 + 1 + 1 + 1 + 1 + 5 = 29',
    );
    const cubes = container.querySelectorAll('.transform-3d');
    const faces = [
      'rotateY(180deg)',
      'rotateX(-90deg)',
      'rotateY(-90deg)',
      'rotateY(90deg)',
      'rotateX(90deg)',
      'none',
      'none',
      'none',
      'none',
      'rotateX(90deg)',
    ];
    expect(cubes).toHaveLength(faces.length);
    cubes.forEach((cube, index) => {
      expect(cube).toHaveStyle({ transform: faces[index] });
    });
  } finally {
    vi.useRealTimers();
  }
});
