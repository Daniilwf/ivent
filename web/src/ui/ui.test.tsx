import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';
import { ru } from '../i18n/ru';
import { Button } from './Button';
import { ConfirmDanger } from './Dialogs';
import { ChoiceGroup, Field, FilePicker, Select } from './Field';
import { RouteProgress, Skeleton } from './Progress';
import { ErrorState, Notice } from './States';

describe('the design system components', () => {
  it('a loading button keeps its label, says it is busy and takes no clicks', async () => {
    const click = vi.fn();
    render(
      <Button variant="main" loading onClick={click}>
        Бросить кубы
      </Button>,
    );
    const button = screen.getByRole('button', { name: /Бросить кубы/ });
    expect(button).toHaveAttribute('aria-busy', 'true');
    await userEvent.click(button);
    expect(click).not.toHaveBeenCalled();
  });

  it('a loading submit button does not send its form twice', async () => {
    const submit = vi.fn((e: { preventDefault: () => void }) => {
      e.preventDefault();
    });
    render(
      <form onSubmit={submit}>
        <Button type="submit" variant="main" loading>
          Отправить пруф
        </Button>
      </form>,
    );
    await userEvent.click(screen.getByRole('button', { name: /Отправить пруф/ }));
    expect(submit).not.toHaveBeenCalled();
  });

  it('a disabled button takes no clicks', async () => {
    const click = vi.fn();
    render(
      <Button disabled onClick={click}>
        Реролл
      </Button>,
    );
    await userEvent.click(screen.getByRole('button', { name: 'Реролл' }));
    expect(click).not.toHaveBeenCalled();
  });

  it('a dangerous action shows its consequences before it happens', async () => {
    const confirm = vi.fn();
    render(
      <ConfirmDanger
        trigger={<Button variant="danger">Дропнуть</Button>}
        title="Дропнуть игру?"
        consequences={['−2d4 очков и клеток', 'Плохой ивент']}
        confirm="Дропнуть игру"
        onConfirm={confirm}
      />,
    );
    await userEvent.click(screen.getByRole('button', { name: 'Дропнуть' }));
    const dialog = screen.getByRole('alertdialog');
    expect(dialog).toHaveTextContent('−2d4 очков и клеток');
    expect(dialog).toHaveTextContent('Плохой ивент');
    expect(confirm).not.toHaveBeenCalled();
    await userEvent.click(screen.getByRole('button', { name: 'Дропнуть игру' }));
    expect(confirm).toHaveBeenCalledOnce();
  });

  it('cancelling a dangerous action does nothing', async () => {
    const confirm = vi.fn();
    render(
      <ConfirmDanger
        trigger={<Button variant="danger">Дропнуть</Button>}
        title="Дропнуть игру?"
        consequences={['Плохой ивент']}
        confirm="Дропнуть игру"
        onConfirm={confirm}
      />,
    );
    await userEvent.click(screen.getByRole('button', { name: 'Дропнуть' }));
    await userEvent.click(screen.getByRole('button', { name: ru.ui.cancel }));
    expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument();
    expect(confirm).not.toHaveBeenCalled();
  });

  it('a field with an error says so next to it and to screen readers', () => {
    render(<Field label="Ссылка на пруф" error="Нужна ссылка целиком" />);
    const input = screen.getByLabelText('Ссылка на пруф');
    expect(input).toHaveAttribute('aria-invalid', 'true');
    expect(input).toHaveAccessibleDescription('Нужна ссылка целиком');
  });

  it('the route bar counts the cells done out of the whole way', () => {
    render(<RouteProgress left={12} total={40} />);
    const bar = screen.getByRole('progressbar', { name: ru.ui.routeProgress });
    expect(bar).toHaveAttribute('aria-valuenow', '28');
    expect(bar).toHaveAttribute('aria-valuemax', '40');
    expect(screen.getByText(ru.ui.toFinish(12))).toBeInTheDocument();
  });

  it('a status is words and an icon, not colour alone; an error is an alert with a way to retry', async () => {
    const retry = vi.fn();
    render(
      <>
        <Notice tone="success">Пруф одобрен</Notice>
        <ErrorState title="Лента не загрузилась" text="Сервер не ответил" onRetry={retry} />
      </>,
    );
    expect(screen.getByRole('status')).toHaveTextContent('Пруф одобрен');
    expect(screen.getByRole('alert')).toHaveTextContent('Лента не загрузилась');
    await userEvent.click(screen.getByRole('button', { name: ru.ui.retry }));
    expect(retry).toHaveBeenCalledOnce();
  });

  it('a choice group shows every answer as a radio button and picks one', async () => {
    const change = vi.fn();
    render(
      <ChoiceGroup
        label="Сложность"
        options={[
          { value: 'easy', label: 'Лёгкая' },
          { value: 'hard', label: 'Сложная' },
        ]}
        value="easy"
        onChange={change}
      />,
    );
    expect(screen.getByRole('group', { name: 'Сложность' })).toBeInTheDocument();
    expect(screen.getByRole('radio', { name: 'Лёгкая' })).toBeChecked();
    await userEvent.click(screen.getByText('Сложная'));
    expect(change).toHaveBeenCalledWith('hard');
  });

  it('a select has its label and says its error', () => {
    render(
      <Select label="Причина" error="Выбери причину">
        <option value="">—</option>
      </Select>,
    );
    const select = screen.getByLabelText('Причина');
    expect(select).toHaveAttribute('aria-invalid', 'true');
    expect(select).toHaveAccessibleDescription('Выбери причину');
  });

  it('a busy file picker takes no new file and says it is busy', () => {
    render(<FilePicker label="Добавить скрин" busy />);
    expect(screen.getByLabelText('Добавить скрин')).toBeDisabled();
    expect(screen.getByText('Добавить скрин').closest('label')).toHaveAttribute(
      'aria-busy',
      'true',
    );
  });

  it('keeps the rounding a skeleton is given: a round sticker stays round (H5)', () => {
    const { container } = render(
      <>
        <Skeleton className="size-10 rounded-full" />
        <Skeleton className="h-5 w-20" />
      </>,
    );
    const [round, plain] = [...container.querySelectorAll('span')];

    expect(round?.className).toContain('rounded-full');
    expect(round?.className).not.toContain('rounded-sm');
    expect(plain?.className).toContain('rounded-sm');
  });
});
