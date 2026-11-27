import { act, fireEvent, render, screen } from '@testing-library/react';
import { frame } from 'motion/react';
import { useState } from 'react';
import type { Schemas } from '../api/client';
import { DrawnEventCard } from './DrawnEventCard';

type MotionFeatures = typeof import('../board/motionFeatures');

const features = vi.hoisted(() => ({ load: vi.fn<() => Promise<MotionFeatures>>() }));
vi.mock('../board/loadMotion', () => ({ loadMotion: () => features.load() }));

function deferred<T>() {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>((done) => {
    resolve = done;
  });
  return { promise, resolve };
}

const drawn: Schemas['DrawnEventView'] = {
  sequence: 7,
  deck: 'good',
  kind: 'good',
  objectId: 'tailwind',
  name: 'Попутный ветер',
  description: 'В следующем броске за прохождение на один кубик больше.',
  source: 'cell',
};

/** A child's real local state and focus must survive skipping its enclosing card. */
function AnswerDraft() {
  const [answer, setAnswer] = useState('coins');
  return (
    // Preparing the answer does not itself skip the card: the test clicks the article separately below.
    <fieldset
      onClick={(event) => {
        event.stopPropagation();
      }}
    >
      <legend>Черновик ответа</legend>
      <label>
        <input
          type="radio"
          name="draft-answer"
          value="coins"
          checked={answer === 'coins'}
          onChange={() => {
            setAnswer('coins');
          }}
        />
        Монетки
      </label>
      <label>
        <input
          type="radio"
          name="draft-answer"
          value="step"
          checked={answer === 'step'}
          onChange={() => {
            setAnswer('step');
          }}
        />
        Шаг
      </label>
    </fieldset>
  );
}

function expectVisibleResult(article: HTMLElement) {
  const appearance = getComputedStyle(article);
  expect(
    Number(appearance.opacity || '1'),
    `A skipped card must already be opaque before lazy features load; transform: ${appearance.transform}`,
  ).toBe(1);
  expect(appearance.transform).not.toMatch(/rotateY\(\s*-?90(?:\.0+)?deg\s*\)/);
  expect(article).toBeVisible();
  expect(screen.getByText(drawn.description)).toBeVisible();
}

describe('skipping a fresh event while its real Motion features are still loading', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
    features.load.mockReset();
  });

  it('offers a focusable button to reveal the event', () => {
    vi.stubGlobal('matchMedia', () => ({
      matches: false,
      addEventListener() {},
      removeEventListener() {},
    }));
    features.load.mockReturnValue(new Promise(() => undefined));
    render(<DrawnEventCard drawn={drawn} fresh />);
    const article = screen.getByRole('article', { name: drawn.name });
    const skip = screen.getByTestId('event-moment-skip');

    expect(article).toHaveAttribute('inert');
    skip.focus();
    fireEvent.click(skip);

    expectVisibleResult(article);
    expect(article).not.toHaveAttribute('inert');
    expect(screen.getByRole('heading', { name: drawn.name })).toHaveFocus();
    expect(skip).not.toBeInTheDocument();
  });

  it('reveals the existing card and answer immediately, then stays final when the delayed features arrive', async () => {
    const media = {
      matches: false,
      media: '(prefers-reduced-motion: reduce)',
      onchange: null,
      addListener() {},
      removeListener() {},
      addEventListener() {},
      removeEventListener() {},
      dispatchEvent: () => false,
    };
    vi.stubGlobal('matchMedia', () => media);
    const delayed = deferred<MotionFeatures>();
    features.load.mockReturnValue(delayed.promise);
    render(
      <DrawnEventCard drawn={drawn} fresh>
        <AnswerDraft />
      </DrawnEventCard>,
    );
    const article = screen.getByRole('article', { name: drawn.name });
    const step = screen.getByRole('radio', { name: 'Шаг' });
    expect(article).toHaveAttribute('data-drawing', 'true');
    expect(features.load).toHaveBeenCalled();
    fireEvent.click(step);
    expect(step).toBeChecked();
    expect(article).toHaveAttribute('data-drawing', 'true');
    step.focus();

    try {
      fireEvent.click(article);

      // No await, frame flush or feature resolution is permitted before these immediate-result assertions.
      expect(screen.getByRole('article', { name: drawn.name })).toBe(article);
      expect(article).not.toHaveAttribute('data-drawing');
      expectVisibleResult(article);
      expect(screen.getByRole('radio', { name: 'Шаг' })).toBe(step);
      expect(step).toBeChecked();
      expect(step).toHaveFocus();
    } finally {
      // The real feature bundle is released after the assertions, including when the regression fails.
      const actualFeatures = await import('../board/motionFeatures');
      await act(async () => {
        delayed.resolve(actualFeatures);
        await delayed.promise;
      });
    }

    await act(async () => {
      await new Promise<void>((resolve) =>
        frame.postRender(() => {
          resolve();
        }),
      );
    });
    expect(screen.getByRole('article', { name: drawn.name })).toBe(article);
    expect(article).not.toHaveAttribute('data-drawing');
    expectVisibleResult(article);
    expect(screen.getByRole('radio', { name: 'Шаг' })).toBe(step);
    expect(step).toBeChecked();
    expect(step).toHaveFocus();
  });

  it('reveals the same card within the chain budget without loaded features and never restarts on late resolution', async () => {
    vi.useFakeTimers();
    const media = {
      matches: false,
      media: '(prefers-reduced-motion: reduce)',
      onchange: null,
      addListener() {},
      removeListener() {},
      addEventListener() {},
      removeEventListener() {},
      dispatchEvent: () => false,
    };
    vi.stubGlobal('matchMedia', () => media);
    const delayed = deferred<MotionFeatures>();
    features.load.mockReturnValue(delayed.promise);
    const { unmount } = render(<DrawnEventCard drawn={drawn} fresh />);
    const article = screen.getByRole('article', { name: drawn.name });

    try {
      expect(article).toHaveAttribute('data-drawing', 'true');
      expect(features.load).toHaveBeenCalled();

      // The result must be available by DESIGN's limit even when the lazy network request never finishes.
      // No pointer or keyboard event, feature resolution or application-specific timeout is involved.
      await act(async () => {
        await vi.advanceTimersByTimeAsync(1500);
      });

      expect(screen.getByRole('article', { name: drawn.name })).toBe(article);
      expectVisibleResult(article);
      expect(article).not.toHaveAttribute('data-drawing');
    } finally {
      // Restore the real clock before releasing Motion: an unresolved fake RAF must not hang failure cleanup.
      vi.useRealTimers();
      const actualFeatures = await import('../board/motionFeatures');
      await act(async () => {
        delayed.resolve(actualFeatures);
        await delayed.promise;
      });
    }

    try {
      await act(async () => {
        await new Promise<void>((resolve) =>
          frame.postRender(() => {
            resolve();
          }),
        );
      });
      expect(screen.getByRole('article', { name: drawn.name })).toBe(article);
      expectVisibleResult(article);
      expect(article).not.toHaveAttribute('data-drawing');
    } finally {
      unmount();
    }
  });
});
