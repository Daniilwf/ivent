import { render, screen } from '@testing-library/react';
import { App } from './App';
import { ru } from './i18n/ru';

describe('App', () => {
  it('shows the site title from the dictionary', () => {
    render(<App />);

    expect(screen.getByRole('heading', { level: 1 })).toHaveTextContent(ru.app.title);
  });
});
