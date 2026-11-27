import { act, render, screen } from '@testing-library/react';
import { Confetti } from './Confetti';

it('starts 70 particles at the projected finish and lets them fall', () => {
  const translate = vi.fn();
  const context = {
    clearRect: vi.fn(),
    fillRect: vi.fn(),
    restore: vi.fn(),
    rotate: vi.fn(),
    save: vi.fn(),
    translate,
  };
  vi.spyOn(HTMLCanvasElement.prototype, 'getContext').mockReturnValue(
    context as unknown as CanvasRenderingContext2D,
  );
  vi.spyOn(HTMLCanvasElement.prototype, 'clientWidth', 'get').mockReturnValue(300);
  vi.spyOn(HTMLCanvasElement.prototype, 'clientHeight', 'get').mockReturnValue(400);
  vi.spyOn(Math, 'random').mockReturnValue(0.5);
  vi.spyOn(performance, 'now').mockReturnValue(0);
  vi.stubGlobal('devicePixelRatio', 2);
  const frames: FrameRequestCallback[] = [];
  vi.spyOn(window, 'requestAnimationFrame').mockImplementation((frame) => frames.push(frame));
  const onDone = vi.fn();
  try {
    render(<Confetti overlay origin={() => ({ x: 80, y: 180 })} onDone={onDone} />);
    act(() => frames.shift()?.(0));
    expect(translate).toHaveBeenCalledTimes(70);
    expect(translate).toHaveBeenCalledWith(160, 360);
    translate.mockClear();
    act(() => frames.shift()?.(500));
    expect(translate.mock.calls.every(([, y]) => Number(y) > 360)).toBe(true);
    act(() => frames.shift()?.(2600));
    expect(onDone).toHaveBeenCalledOnce();
    expect(screen.getByTestId('finish-confetti')).toHaveAttribute('aria-hidden', 'true');
  } finally {
    vi.unstubAllGlobals();
    vi.restoreAllMocks();
  }
});
