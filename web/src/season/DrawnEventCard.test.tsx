import { fireEvent, render, screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import type { Schemas } from '../api/client';
import { ru } from '../i18n/ru';
import { DrawnEventCard } from './DrawnEventCard';

// 5.1: the card of the event I drew last — its kind, where it came from, its name and text, already played (D-500)

const drawn: Schemas['DrawnEventView'] = {
  sequence: 7,
  deck: 'good',
  kind: 'good',
  objectId: 'tailwind',
  name: 'Попутный ветер',
  description: 'В следующем броске за прохождение на один кубик больше.',
  source: 'cell',
};

describe('DrawnEventCard', () => {
  it('names the event as a heading with its kind, source and text', () => {
    render(<DrawnEventCard drawn={drawn} />);

    expect(screen.getByRole('heading', { name: 'Попутный ветер' })).toBeInTheDocument();
    expect(screen.getByRole('article', { name: 'Попутный ветер' })).toBeInTheDocument();
    expect(screen.getByTestId('drawn-event-kind')).toHaveTextContent(ru.events.kinds.good);
    expect(screen.getByText(ru.events.sources.cell)).toBeInTheDocument();
    expect(screen.getByText(drawn.description)).toBeInTheDocument();
    expect(screen.getByText(ru.events.played)).toBeInTheDocument();
  });

  it('names every source of a drawn event', () => {
    const { rerender } = render(<DrawnEventCard drawn={drawn} />);
    for (const source of [
      'cell',
      'item',
      'difficulty',
      'incoming',
      'rerollCost',
      'dropPenalty',
    ] as const) {
      rerender(<DrawnEventCard drawn={{ ...drawn, source }} />);
      expect(screen.getByText(ru.events.sources[source])).toBeInTheDocument();
    }
  });

  it('shows every kind in its words and an event without text without an empty paragraph', () => {
    const { rerender, container } = render(
      <DrawnEventCard drawn={{ ...drawn, kind: 'risky', description: '', source: 'item' }} />,
    );
    expect(screen.getByTestId('drawn-event-kind')).toHaveTextContent('Рискованный ивент');
    expect(screen.getByText(ru.events.sources.item)).toBeInTheDocument();
    expect(container.querySelectorAll('p')).toHaveLength(2);

    rerender(<DrawnEventCard drawn={{ ...drawn, kind: 'special' }} />);
    expect(screen.getByTestId('drawn-event-kind')).toHaveTextContent('Специвент');
    rerender(<DrawnEventCard drawn={{ ...drawn, kind: 'bad' }} />);
    expect(screen.getByTestId('drawn-event-kind')).toHaveTextContent('Плохой ивент');
  });

  it('draws a fresh card off the deck, a click shows it at once, and an old card just stands', () => {
    const { rerender } = render(<DrawnEventCard drawn={drawn} fresh />);
    const card = screen.getByTestId('drawn-event');
    expect(card).toHaveAttribute('data-drawing', 'true');

    fireEvent.click(card);
    expect(screen.getByTestId('drawn-event')).not.toHaveAttribute('data-drawing');

    rerender(<DrawnEventCard key="old" drawn={drawn} />);
    expect(screen.getByTestId('drawn-event')).not.toHaveAttribute('data-drawing');
  });
});
