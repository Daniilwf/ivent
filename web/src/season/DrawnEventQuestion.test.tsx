import { render, screen } from '@testing-library/react';
import type { Schemas } from '../api/client';
import { ru } from '../i18n/ru';
import { DrawnEventCard } from './DrawnEventCard';

const drawn: Schemas['DrawnEventView'] = {
  sequence: 7,
  deck: 'good',
  kind: 'good',
  objectId: 'tailwind-gift',
  name: 'Попутный ветер',
  description: 'Выбери, кому подарить попутный ветер.',
  source: 'cell',
};

describe('the caption of an event whose effect asks a question', () => {
  it('says it waits for an answer instead of claiming the whole event is already played', () => {
    render(<DrawnEventCard drawn={drawn} waitingForAnswer />);

    expect(screen.getByText('Ждёт твоего ответа.')).toBeVisible();
    expect(screen.queryByText(ru.events.played)).not.toBeInTheDocument();
    expect(screen.getByRole('heading', { name: drawn.name })).toBeVisible();
  });

  it('keeps the normal caption by default and restores it when the question is no longer pending', () => {
    const { rerender } = render(<DrawnEventCard drawn={drawn} />);
    expect(screen.getByText(ru.events.played)).toBeVisible();

    rerender(<DrawnEventCard drawn={drawn} waitingForAnswer />);
    expect(screen.queryByText(ru.events.played)).not.toBeInTheDocument();

    rerender(<DrawnEventCard drawn={drawn} waitingForAnswer={false} />);
    expect(screen.getByText(ru.events.played)).toBeVisible();
    expect(screen.queryByText('Ждёт твоего ответа.')).not.toBeInTheDocument();
  });
});
