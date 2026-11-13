import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { ReactNode } from 'react';
import { ru } from '../i18n/ru';
import { MapCanvas, overviewMinZoom, readableMinZoom } from './MapCanvas';
import type { Draft } from './mapDraft';

// jsdom does not lay React Flow out, so the canvas is replaced by what it was told: its zoom limits and the fit the
// buttons ask for (MP-2)
interface CanvasProps {
  minZoom?: number;
  fitViewOptions?: { minZoom?: number };
  children?: ReactNode;
}

const flow = vi.hoisted(() => {
  const props: CanvasProps = {};
  return {
    fitView: vi.fn(() => Promise.resolve(true)),
    zoomIn: vi.fn(),
    zoomOut: vi.fn(),
    props,
  };
});

vi.mock('@xyflow/react', async (original) => ({
  ...(await original<typeof import('@xyflow/react')>()),
  ReactFlow: (props: CanvasProps) => {
    flow.props = props;
    return <div>{props.children}</div>;
  },
  Background: () => null,
  Panel: ({ children }: { children?: ReactNode }) => <div>{children}</div>,
  useReactFlow: () => flow,
}));

const draft: Draft = {
  cells: [
    { id: 'start', type: 'start', x: 0, y: 0 },
    { id: 'finish', type: 'finish', x: 1900, y: 1450 },
  ],
  edges: [{ from: 'start', to: 'finish', isDefaultForward: true, isPrimaryBackward: true }],
  zones: [],
} as unknown as Draft;

function canvas() {
  render(
    <MapCanvas
      draft={draft}
      selected={null}
      problems={new Set()}
      players={new Map()}
      onSelect={() => {}}
      onMove={() => {}}
      onConnect={() => {}}
    />,
  );
}

describe('MapCanvas zoom (MP-2)', () => {
  it('opens readable, but lets «Показать всю карту» shrink the whole map into the frame', async () => {
    canvas();

    expect(flow.props.fitViewOptions?.minZoom).toBe(readableMinZoom);
    expect(flow.props.minZoom).toBe(overviewMinZoom);

    await userEvent.click(screen.getByRole('button', { name: ru.admin.map.fit }));
    expect(flow.fitView).toHaveBeenCalledWith(
      expect.objectContaining({ minZoom: overviewMinZoom }),
    );
  });

  it('fits the largest map a background allows into the canvas frame', () => {
    // The longest side of a background (Files:MapBackgroundMaxLongSide, D-278), spread on the canvas (×1.4) with the
    // fit's padding, into the canvas's 640 px height
    const longest = 3200 * 1.4 * (1 + 2 * 0.08);
    expect(640 / longest).toBeGreaterThanOrEqual(overviewMinZoom);
    expect(overviewMinZoom).toBeLessThan(readableMinZoom);
  });
});
