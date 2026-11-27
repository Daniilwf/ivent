import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { Schemas } from '../api/client';
import { ManualEffectItem } from './ManualEffectItem';

const effect: Schemas['ManualEffectView'] = {
  id: 'manual-1',
  drawEvent: 'bad',
  source: 'drop',
  irl: false,
};

describe('manual effect task (DR7-12)', () => {
  it('offers a direct resolution and asks for a reason in a dialog', async () => {
    const resolve = vi.fn();
    const user = userEvent.setup();
    render(<ManualEffectItem effect={effect} pending={false} resolvable onResolve={resolve} />);

    const played = screen.getByRole('button', { name: 'Разыграно' });
    const unsuitable = screen.getByRole('button', { name: 'Не подходит…' });
    expect(screen.queryByRole('textbox')).not.toBeInTheDocument();
    await user.click(unsuitable);

    const dialog = within(screen.getByRole('dialog', { name: 'Ивент не подходит' }));
    const reason = dialog.getByRole('textbox', { name: 'Комментарий' });
    const submit = dialog.getByRole('button', { name: 'Не применимо' });
    expect(submit).toBeDisabled();
    await user.type(reason, '   ');
    expect(submit).toBeDisabled();
    await user.type(reason, 'нет игры  ');
    await user.click(submit);

    expect(resolve).toHaveBeenCalledExactlyOnceWith('notApplicable', 'нет игры');
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
    expect(unsuitable).toHaveFocus();
    expect(played).toBeEnabled();
  });

  it('keeps the dialog from submitting while a command is pending', async () => {
    const resolve = vi.fn();
    const user = userEvent.setup();
    const { rerender } = render(
      <ManualEffectItem effect={effect} pending={false} resolvable onResolve={resolve} />,
    );
    await user.click(screen.getByRole('button', { name: 'Не подходит…' }));
    await user.type(screen.getByRole('textbox', { name: 'Комментарий' }), 'нет игры');

    rerender(<ManualEffectItem effect={effect} pending resolvable onResolve={resolve} />);

    const submit = screen.getByRole('button', { name: 'Не применимо' });
    expect(submit).toBeDisabled();
    await user.click(submit);
    expect(resolve).not.toHaveBeenCalled();
    await user.keyboard('{Escape}');
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Разыграно' })).toBeDisabled();
    expect(screen.getByRole('button', { name: 'Не подходит…' })).toBeDisabled();
  });
});
