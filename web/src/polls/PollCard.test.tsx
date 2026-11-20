import { act, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { Schemas } from '../api/client';
import { PollCard } from './PollCard';

type Poll = Schemas['PollView'];
type Voting = NonNullable<Poll['voting']>;

const horrorId = 'e79ca6e8-7d83-4e71-9e2a-0a52b52008c5';
const strategyId = '903b75ba-c512-40b4-bce9-b48d41008d9a';
const adventureId = '854f7fbc-bfca-4144-b3ea-467062a19835';
const question = 'В каком жанре сыграем вместе?';

function voting(overrides: Partial<Voting> = {}): Voting {
  return { canVote: true, reason: null, allowChangeVote: true, ...overrides };
}

function poll(overrides: Partial<Poll> = {}): Poll {
  return {
    id: 'a8450fc8-7435-47ef-a2b3-a31257e7961a',
    question,
    options: [
      { id: horrorId, label: 'Хорроры' },
      { id: strategyId, label: 'Стратегии' },
    ],
    voters: 'players',
    anonymous: false,
    status: 'open',
    startedAt: '2030-11-17T12:00:00Z',
    closesAt: '2030-11-24T12:00:00Z',
    counts: null,
    result: null,
    myVote: null,
    source: 'admin',
    voting: voting(),
    ...overrides,
  };
}

function option(label: string): HTMLElement {
  // A vote count or the own-vote marker may be included in an option's accessible name.
  return screen.getByRole('radio', { name: (name) => name.includes(label) });
}

function voteButton(): HTMLElement {
  return screen.getByRole('button', { name: /^(Голосовать|Изменить голос)$/ });
}

function expectNoEnabledVote() {
  for (const radio of screen.queryAllByRole('radio')) expect(radio).toBeDisabled();
  for (const button of screen.queryAllByRole('button', {
    name: /^(Голосовать|Изменить голос)$/,
  }))
    expect(button).toBeDisabled();
}

function accessibleOptionText(label: string): string {
  let accessibleName = '';
  const radio = screen.getByRole('radio', {
    name: (name) => {
      if (!name.includes(label)) return false;
      accessibleName = name;
      return true;
    },
  });
  const description = (radio.getAttribute('aria-describedby') ?? '')
    .split(/\s+/)
    .filter(Boolean)
    .map((id) => document.getElementById(id)?.textContent ?? '')
    .join(' ');
  return `${accessibleName} ${description}`;
}

it('reads a final nomination and removes withheld material without losing its result', async () => {
  const onVote = vi.fn(() => Promise.resolve());
  const candidate = {
    id: horrorId,
    label: 'Ёж нашёл лифт',
    detail: 'Титры на последнем этаже',
    href: '/seasons/one/gallery',
    thumbnailUrl: '/api/example/thumbnail',
  };
  const closed = poll({
    question: '',
    nomination: 'funniestMoment',
    source: 'nomination',
    status: 'closed',
    result: horrorId,
    myVote: horrorId,
    options: [candidate, { id: strategyId, label: 'Упал со стула' }],
  });
  const { rerender } = render(<PollCard poll={closed} pending={false} onVote={onVote} />);
  expect(screen.getByRole('heading', { name: 'Самый смешной момент' })).toBeVisible();
  await userEvent.click(screen.getByText('Материалы голосования'));
  expect(screen.getByRole('link', { name: 'Открыть галерею' })).toHaveAttribute(
    'href',
    candidate.href,
  );
  expect(screen.queryByRole('link', { name: candidate.label })).not.toBeInTheDocument();
  expect(screen.getByText(candidate.detail)).toBeVisible();

  rerender(
    <PollCard
      poll={{ ...closed, options: [{ ...candidate, unavailable: true }] }}
      pending={false}
      onVote={onVote}
    />,
  );
  expect(screen.getByText('Материалы победителя скрыты.')).toBeVisible();
  expect(screen.queryByText(candidate.detail)).not.toBeInTheDocument();
  expect(screen.queryByRole('link', { name: 'Открыть галерею' })).not.toBeInTheDocument();
  expectNoEnabledVote();
});

describe('a poll card uses the server snapshot and my voting availability (EV21/EV22/EV24/EV39)', () => {
  it('shows the question, snapshot labels and readable deadline without selecting or sending a vote', () => {
    const onVote = vi.fn(() => Promise.resolve());
    const value = poll();
    const { container } = render(<PollCard poll={value} pending={false} onVote={onVote} />);

    expect(screen.getByRole('heading', { name: question })).toBeVisible();
    expect(
      screen.queryByRole('radiogroup', { name: 'Твой голос' }) ??
        screen.getByRole('group', { name: 'Твой голос' }),
    ).toBeVisible();
    expect(option('Хорроры')).not.toBeChecked();
    expect(option('Стратегии')).not.toBeChecked();
    expect(voteButton()).toBeDisabled();
    expect(onVote).not.toHaveBeenCalled();
    const deadline = container.querySelector(`time[datetime="${value.closesAt}"]`);
    expect(deadline).toBeVisible();
    expect(deadline).not.toHaveTextContent(/^\s*$/);
    expect(container).not.toHaveTextContent(horrorId);
    expect(container).not.toHaveTextContent(strategyId);
  });

  it('sends exactly the selected option id only after an explicit submit', async () => {
    const onVote = vi.fn(() => Promise.resolve());
    render(<PollCard poll={poll()} pending={false} onVote={onVote} />);

    await userEvent.click(option('Стратегии'));

    expect(option('Стратегии')).toBeChecked();
    expect(option('Хорроры')).not.toBeChecked();
    expect(voteButton()).toBeEnabled();
    expect(onVote).not.toHaveBeenCalled();
    await userEvent.click(voteButton());
    expect(onVote).toHaveBeenCalledExactlyOnceWith(strategyId);
  });

  it('supports choosing with the keyboard without automatically sending the choice', async () => {
    const onVote = vi.fn(() => Promise.resolve());
    render(<PollCard poll={poll()} pending={false} onVote={onVote} />);

    option('Хорроры').focus();
    await userEvent.keyboard('[ArrowDown]');

    expect(option('Стратегии')).toBeChecked();
    expect(onVote).not.toHaveBeenCalled();
    voteButton().focus();
    await userEvent.keyboard('[Enter]');
    expect(onVote).toHaveBeenCalledExactlyOnceWith(strategyId);
  });

  it('keeps an unsent choice while counts and the same poll refresh', async () => {
    const onVote = vi.fn(() => Promise.resolve());
    const { rerender } = render(<PollCard poll={poll()} pending={false} onVote={onVote} />);
    await userEvent.click(option('Стратегии'));

    rerender(
      <PollCard
        poll={poll({
          counts: [
            { optionId: horrorId, votes: 4 },
            { optionId: strategyId, votes: 1 },
          ],
        })}
        pending={false}
        onVote={onVote}
      />,
    );

    expect(option('Стратегии')).toBeChecked();
    expect(voteButton()).toBeEnabled();
    expect(onVote).not.toHaveBeenCalled();
  });

  it('keeps the selected id through a reorder and a label correction', async () => {
    const onVote = vi.fn(() => Promise.resolve());
    const { rerender } = render(<PollCard poll={poll()} pending={false} onVote={onVote} />);
    await userEvent.click(option('Стратегии'));

    rerender(
      <PollCard
        poll={poll({
          options: [
            { id: strategyId, label: 'Тактика и стратегии' },
            { id: horrorId, label: 'Хорроры' },
          ],
        })}
        pending={false}
        onVote={onVote}
      />,
    );

    expect(option('Тактика и стратегии')).toBeChecked();
    expect(option('Хорроры')).not.toBeChecked();
    await userEvent.click(voteButton());
    expect(onVote).toHaveBeenCalledExactlyOnceWith(strategyId);
  });

  it('invalidates an unsent choice removed from the snapshot and never sends its stale id', async () => {
    const onVote = vi.fn(() => Promise.resolve());
    const { rerender } = render(<PollCard poll={poll()} pending={false} onVote={onVote} />);
    await userEvent.click(option('Стратегии'));

    rerender(
      <PollCard
        poll={poll({
          options: [
            { id: horrorId, label: 'Хорроры' },
            { id: adventureId, label: 'Приключения' },
          ],
        })}
        pending={false}
        onVote={onVote}
      />,
    );

    expect(voteButton()).toBeDisabled();
    expect(option('Хорроры')).not.toBeChecked();
    expect(option('Приключения')).not.toBeChecked();
    expect(onVote).not.toHaveBeenCalled();
    await userEvent.click(option('Приключения'));
    await userEvent.click(voteButton());
    expect(onVote).toHaveBeenCalledExactlyOnceWith(adventureId);
  });

  it('does not carry an unsent choice into a different poll', async () => {
    const onVote = vi.fn(() => Promise.resolve());
    const { rerender } = render(<PollCard poll={poll()} pending={false} onVote={onVote} />);
    await userEvent.click(option('Стратегии'));

    rerender(
      <PollCard
        poll={poll({ id: '29a2a3a4-4873-4ab0-8e85-15d850fe4f3c', question: 'Другой вопрос' })}
        pending={false}
        onVote={onVote}
      />,
    );

    expect(screen.getByRole('heading', { name: 'Другой вопрос' })).toBeVisible();
    expect(option('Стратегии')).not.toBeChecked();
    expect(voteButton()).toBeDisabled();
    expect(onVote).not.toHaveBeenCalled();
  });

  it('blocks voting controls while the parent reports a pending command', () => {
    const onVote = vi.fn(() => Promise.resolve());
    render(<PollCard poll={poll()} pending onVote={onVote} />);

    expect(option('Хорроры')).toBeDisabled();
    expect(option('Стратегии')).toBeDisabled();
    expectNoEnabledVote();
    expect(onVote).not.toHaveBeenCalled();
  });

  it('sends one in-flight command even before the parent changes its pending prop', async () => {
    let release: (() => void) | undefined;
    const waiting = new Promise<void>((resolve) => {
      release = resolve;
    });
    const onVote = vi.fn(() => waiting);
    render(<PollCard poll={poll()} pending={false} onVote={onVote} />);
    await userEvent.click(option('Стратегии'));
    const submit = voteButton();
    try {
      await userEvent.dblClick(submit);

      expect(onVote).toHaveBeenCalledExactlyOnceWith(strategyId);
      expect(submit).toBeDisabled();
      expect(option('Хорроры')).toBeDisabled();
      expect(option('Стратегии')).toBeDisabled();
    } finally {
      await act(async () => {
        release?.();
        await waiting;
      });
    }
  });

  it('retains the draft and permits a deliberate retry after a rejected request without exposing the error', async () => {
    const privateMessage = 'internal-command-64ae7b43-user-private-token';
    const onVote = vi
      .fn<(optionId: string) => Promise<void>>()
      .mockRejectedValueOnce(new Error(privateMessage))
      .mockResolvedValueOnce(undefined);
    const { container } = render(<PollCard poll={poll()} pending={false} onVote={onVote} />);
    await userEvent.click(option('Стратегии'));
    await userEvent.click(voteButton());

    expect(await screen.findByRole('alert')).toHaveTextContent(
      'Не удалось отправить голос. Попробуй ещё раз.',
    );
    expect(container).not.toHaveTextContent(privateMessage);
    expect(option('Стратегии')).toBeChecked();
    await waitFor(() => expect(voteButton()).toBeEnabled());
    expect(onVote).toHaveBeenCalledExactlyOnceWith(strategyId);
    await userEvent.click(voteButton());
    expect(onVote).toHaveBeenCalledTimes(2);
    expect(onVote).toHaveBeenLastCalledWith(strategyId);
    await waitFor(() => expect(screen.queryByRole('alert')).not.toBeInTheDocument());
  });

  it('displays my recorded vote and requires a different option for a permitted change', async () => {
    const onVote = vi.fn(() => Promise.resolve());
    render(<PollCard poll={poll({ myVote: horrorId })} pending={false} onVote={onVote} />);

    expect(option('Хорроры')).toBeChecked();
    expect(screen.getByRole('button', { name: 'Изменить голос' })).toBeDisabled();
    await userEvent.click(option('Хорроры'));
    expect(onVote).not.toHaveBeenCalled();
    expect(voteButton()).toBeDisabled();
    await userEvent.click(option('Стратегии'));
    await userEvent.click(screen.getByRole('button', { name: 'Изменить голос' }));
    expect(onVote).toHaveBeenCalledExactlyOnceWith(strategyId);
  });

  it('does not show the old success message when a live update undoes my accepted vote', async () => {
    const onVote = vi.fn(() => Promise.resolve());
    const { rerender } = render(<PollCard poll={poll()} pending={false} onVote={onVote} />);
    await userEvent.click(option('Стратегии'));
    await userEvent.click(voteButton());
    rerender(<PollCard poll={poll({ myVote: strategyId })} pending={false} onVote={onVote} />);
    expect(await screen.findByText('Голос принят')).toBeVisible();

    rerender(<PollCard poll={poll()} pending={false} onVote={onVote} />);
    expect(screen.queryByText('Голос принят')).not.toBeInTheDocument();
    expect(option('Стратегии')).not.toBeChecked();
    expect(onVote).toHaveBeenCalledExactlyOnceWith(strategyId);
  });

  it('keeps keyboard focus in the question while a sent vote waits and leaves no action enabled', async () => {
    let release: (() => void) | undefined;
    const result = new Promise<void>((resolve) => {
      release = resolve;
    });
    const onVote = vi.fn(() => result);
    const { rerender } = render(<PollCard poll={poll()} pending={false} onVote={onVote} />);
    await userEvent.click(option('Стратегии'));
    await userEvent.tab();
    expect(voteButton()).toHaveFocus();
    try {
      await userEvent.keyboard('{Enter}');
      expect(onVote).toHaveBeenCalledExactlyOnceWith(strategyId);
      expect(voteButton()).toBeDisabled();
      expect(screen.getByRole('heading', { name: question })).toHaveFocus();
      rerender(
        <PollCard
          poll={poll({
            myVote: strategyId,
            voting: voting({ canVote: false, allowChangeVote: false, reason: 'poll.alreadyVoted' }),
          })}
          pending={false}
          onVote={onVote}
        />,
      );
    } finally {
      await act(async () => {
        release?.();
        await result;
      });
    }
    expectNoEnabledVote();
    expect(screen.getByRole('heading', { name: question })).toHaveFocus();
  });

  it('says voting has ended when the poll deadline passed and the final result is still pending', () => {
    render(
      <PollCard
        poll={poll({ counts: null, voting: voting({ canVote: false, reason: 'poll.closed' }) })}
        pending={false}
        onVote={vi.fn(() => Promise.resolve())}
      />,
    );
    expect(screen.getByText('Приём голосов завершён.')).toBeVisible();
    expect(screen.getByText('Счёт появится после подведения итогов.')).toBeVisible();
    expect(screen.queryByText('Голосование закрыто')).not.toBeInTheDocument();
    expectNoEnabledVote();
  });

  it('shows a recorded vote read-only when the server forbids changing it', () => {
    const onVote = vi.fn(() => Promise.resolve());
    const { container } = render(
      <PollCard
        poll={poll({
          myVote: horrorId,
          voting: voting({ canVote: false, reason: 'poll.alreadyVoted', allowChangeVote: false }),
        })}
        pending={false}
        onVote={onVote}
      />,
    );

    expect(container).toHaveTextContent('Хорроры');
    expect(screen.getByText('Голос уже подан, сменить его в этом сезоне нельзя.')).toBeVisible();
    expectNoEnabledVote();
    expect(onVote).not.toHaveBeenCalled();
  });

  it('shows a newer own vote from the server when no unsent draft was edited', () => {
    const onVote = vi.fn(() => Promise.resolve());
    const { rerender } = render(
      <PollCard poll={poll({ myVote: horrorId })} pending={false} onVote={onVote} />,
    );
    expect(option('Хорроры')).toBeChecked();

    rerender(<PollCard poll={poll({ myVote: strategyId })} pending={false} onVote={onVote} />);

    expect(option('Стратегии')).toBeChecked();
    expect(option('Хорроры')).not.toBeChecked();
    expect(voteButton()).toBeDisabled();
    expect(onVote).not.toHaveBeenCalled();
  });

  it('keeps an edited draft when another device changes my recorded vote', async () => {
    const onVote = vi.fn(() => Promise.resolve());
    const options = [...poll().options, { id: adventureId, label: 'Приключения' }];
    const { rerender } = render(
      <PollCard poll={poll({ options, myVote: horrorId })} pending={false} onVote={onVote} />,
    );
    await userEvent.click(option('Приключения'));

    rerender(
      <PollCard poll={poll({ options, myVote: strategyId })} pending={false} onVote={onVote} />,
    );

    expect(option('Приключения')).toBeChecked();
    expect(voteButton()).toBeEnabled();
    expect(onVote).not.toHaveBeenCalled();
  });

  it.each([
    ['poll.notVoter', 'В этом голосовании голосуют только игроки сезона.'],
    ['poll.frozenVoter', 'Финишировавший первым не голосует там, где итог действует на игроков.'],
  ])('shows the server denial %s and offers no enabled voting control', (reason, explanation) => {
    const onVote = vi.fn(() => Promise.resolve());
    render(
      <PollCard
        poll={poll({ voting: voting({ canVote: false, reason }) })}
        pending={false}
        onVote={onVote}
      />,
    );

    expect(screen.getByText(explanation)).toBeVisible();
    expectNoEnabledVote();
    expect(onVote).not.toHaveBeenCalled();
  });

  it.each(['omitted', 'null'] as const)(
    'does not guess voting permission when metadata is %s',
    (state) => {
      const onVote = vi.fn(() => Promise.resolve());
      const value = poll({ voting: null });
      if (state === 'omitted') delete value.voting;
      render(<PollCard poll={value} pending={false} onVote={onVote} />);

      expect(screen.getByRole('heading', { name: question })).toBeVisible();
      expectNoEnabledVote();
      expect(onVote).not.toHaveBeenCalled();
    },
  );

  it('removes voting availability immediately when a refresh revokes it', async () => {
    const onVote = vi.fn(() => Promise.resolve());
    const { rerender } = render(<PollCard poll={poll()} pending={false} onVote={onVote} />);
    await userEvent.click(option('Стратегии'));

    rerender(
      <PollCard
        poll={poll({ voting: voting({ canVote: false, reason: 'poll.notVoter' }) })}
        pending={false}
        onVote={onVote}
      />,
    );

    expectNoEnabledVote();
    expect(onVote).not.toHaveBeenCalled();
    expect(screen.getByText('В этом голосовании голосуют только игроки сезона.')).toBeVisible();
  });

  it('keeps anonymous live counts hidden rather than inventing zero votes, while retaining my own vote', () => {
    const onVote = vi.fn(() => Promise.resolve());
    const { container } = render(
      <PollCard
        poll={poll({ anonymous: true, counts: null, myVote: horrorId })}
        pending={false}
        onVote={onVote}
      />,
    );

    expect(option('Хорроры')).toBeChecked();
    expect(screen.getByText('Счёт скрыт до закрытия')).toBeVisible();
    expect(container).not.toHaveTextContent(/0\s+голос/);
    expect(container).not.toHaveTextContent(horrorId);
    expect(container).not.toHaveTextContent(strategyId);
  });

  it('also keeps a named poll hidden when its current counts are null', () => {
    const onVote = vi.fn(() => Promise.resolve());
    const { container } = render(<PollCard poll={poll()} pending={false} onVote={onVote} />);

    expect(screen.getByText('Счёт скрыт до закрытия')).toBeVisible();
    expect(container).not.toHaveTextContent(/0\s+голос/);
  });

  it('shows actual live zero and nonzero counts associated with their option labels', () => {
    const onVote = vi.fn(() => Promise.resolve());
    render(
      <PollCard
        poll={poll({
          counts: [
            { optionId: strategyId, votes: 0 },
            { optionId: horrorId, votes: 3 },
          ],
        })}
        pending={false}
        onVote={onVote}
      />,
    );

    expect(screen.getByText('3 голоса', { exact: true })).toBeVisible();
    expect(screen.getByText('0 голосов', { exact: true })).toBeVisible();
    expect(accessibleOptionText('Хорроры')).toContain('3 голоса');
    expect(accessibleOptionText('Стратегии')).toContain('0 голосов');
    expect(screen.queryByText('Счёт скрыт до закрытия')).not.toBeInTheDocument();
  });

  it('shows the server-selected result label after closing, including an anonymous poll', () => {
    const onVote = vi.fn(() => Promise.resolve());
    const { container } = render(
      <PollCard
        poll={poll({
          anonymous: true,
          status: 'closed',
          result: strategyId,
          counts: [
            { optionId: horrorId, votes: 1 },
            { optionId: strategyId, votes: 3 },
          ],
          voting: voting({ canVote: false, reason: 'poll.closed' }),
        })}
        pending={false}
        onVote={onVote}
      />,
    );

    expect(screen.getByText('Голосование закрыто', { exact: true })).toBeVisible();
    expect(screen.getByText('Итог: Стратегии', { exact: true })).toBeVisible();
    expect(container).not.toHaveTextContent(strategyId);
    expect(screen.queryByText('Счёт скрыт до закрытия')).not.toBeInTheDocument();
    expectNoEnabledVote();
  });

  it('uses the saved winner of tied counts instead of choosing the first label or inventing a draw', () => {
    const onVote = vi.fn(() => Promise.resolve());
    const { container } = render(
      <PollCard
        poll={poll({
          status: 'closed',
          result: strategyId,
          counts: [
            { optionId: horrorId, votes: 2 },
            { optionId: strategyId, votes: 2 },
          ],
          voting: voting({ canVote: false, reason: 'poll.closed' }),
        })}
        pending={false}
        onVote={onVote}
      />,
    );

    expect(screen.getByText('Итог: Стратегии', { exact: true })).toBeVisible();
    expect(screen.queryByText('Итог: Хорроры', { exact: true })).not.toBeInTheDocument();
    expect(container).not.toHaveTextContent(/ничья/i);
  });

  it('does not invent a winner for a closed poll with no saved result', () => {
    const onVote = vi.fn(() => Promise.resolve());
    const { container } = render(
      <PollCard
        poll={poll({
          status: 'closed',
          result: null,
          counts: [
            { optionId: horrorId, votes: 0 },
            { optionId: strategyId, votes: 0 },
          ],
          voting: voting({ canVote: false, reason: 'poll.closed' }),
        })}
        pending={false}
        onVote={onVote}
      />,
    );

    expect(screen.getByText('Голосование закрыто', { exact: true })).toBeVisible();
    expect(screen.queryByText('Итог: Хорроры', { exact: true })).not.toBeInTheDocument();
    expect(screen.queryByText('Итог: Стратегии', { exact: true })).not.toBeInTheDocument();
    expect(container).not.toHaveTextContent(/ничья/i);
    expectNoEnabledVote();
  });

  it('shows cancellation without a result, zero counts or an invitation to vote', () => {
    const onVote = vi.fn(() => Promise.resolve());
    const { container } = render(
      <PollCard
        poll={poll({
          status: 'cancelled',
          result: null,
          counts: null,
          voting: voting({ canVote: false, reason: 'poll.closed' }),
        })}
        pending={false}
        onVote={onVote}
      />,
    );

    expect(screen.getByText('Голосование отменено', { exact: true })).toBeVisible();
    expect(screen.queryByText(/^Итог:/)).not.toBeInTheDocument();
    expect(container).not.toHaveTextContent(/0\s+голос|ничья/i);
    expectNoEnabledVote();
    expect(onVote).not.toHaveBeenCalled();
  });

  it('stops an unsent vote when the same poll closes on a live refresh', async () => {
    const onVote = vi.fn(() => Promise.resolve());
    const { rerender } = render(<PollCard poll={poll()} pending={false} onVote={onVote} />);
    await userEvent.click(option('Стратегии'));

    rerender(
      <PollCard
        poll={poll({
          status: 'closed',
          result: horrorId,
          counts: [
            { optionId: horrorId, votes: 3 },
            { optionId: strategyId, votes: 1 },
          ],
          voting: voting({ canVote: false, reason: 'poll.closed' }),
        })}
        pending={false}
        onVote={onVote}
      />,
    );

    expect(screen.getByText('Итог: Хорроры', { exact: true })).toBeVisible();
    expectNoEnabledVote();
    expect(onVote).not.toHaveBeenCalled();
  });

  it('does not expose unknown result, count or own-vote identifiers as labels', () => {
    const unknown = '5fca9385-0b93-42b3-a0b1-87efb9143460';
    const onVote = vi.fn(() => Promise.resolve());
    const { container } = render(
      <PollCard
        poll={poll({
          status: 'closed',
          result: unknown,
          myVote: unknown,
          counts: [{ optionId: unknown, votes: 99 }],
          voting: voting({ canVote: false, reason: 'poll.closed' }),
        })}
        pending={false}
        onVote={onVote}
      />,
    );

    expect(screen.getByRole('heading', { name: question })).toBeVisible();
    expect(screen.getByText('Голосование закрыто', { exact: true })).toBeVisible();
    expect(container).not.toHaveTextContent(unknown);
    expect(screen.queryByText('Итог: Хорроры', { exact: true })).not.toBeInTheDocument();
    expect(screen.queryByText('Итог: Стратегии', { exact: true })).not.toBeInTheDocument();
    expectNoEnabledVote();
  });

  it('renders long snapshot text as text instead of interpreting markup', () => {
    const longQuestion = 'Длинный вопрос друзей: <img src=x onerror=alert(1)> — что выберем?';
    const label = 'Большое приключение <script>это текст</script> для всех';
    const onVote = vi.fn(() => Promise.resolve());
    const { container } = render(
      <PollCard
        poll={poll({
          question: longQuestion,
          options: [
            { id: horrorId, label },
            { id: strategyId, label: 'Стратегии' },
          ],
        })}
        pending={false}
        onVote={onVote}
      />,
    );

    expect(screen.getByRole('heading', { name: longQuestion })).toBeVisible();
    expect(option(label)).toBeVisible();
    expect(container.querySelector('img, script')).toBeNull();
  });
});
