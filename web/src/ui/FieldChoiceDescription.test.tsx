import { render, screen, within } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { ChoiceGroup } from './Field';

// An optional note describes the answer; it must not become part of the answer's name or its submitted value.
// The options are variables so the existing public contract accepts their extra field before its implementation.
describe('choice option descriptions for live vote counts', () => {
  it('keeps the option name separate from its visible, accessible description', () => {
    const options = [
      { value: 'horror', label: 'Хорроры', description: '15 голосов' },
      { value: 'strategy', label: 'Стратегии', description: '3 голоса' },
    ];
    render(<ChoiceGroup label="Твой голос" options={options} value="horror" onChange={vi.fn()} />);

    const horror = screen.getByRole('radio', { name: 'Хорроры' });
    const strategy = screen.getByRole('radio', { name: 'Стратегии' });
    expect(horror).toHaveAccessibleName('Хорроры');
    expect(horror).toHaveAccessibleDescription('15 голосов');
    expect(strategy).toHaveAccessibleName('Стратегии');
    expect(strategy).toHaveAccessibleDescription('3 голоса');
    expect(screen.getByText('15 голосов', { exact: true })).toBeVisible();
    expect(screen.getByText('3 голоса', { exact: true })).toBeVisible();
    expect(horror).toHaveAttribute('value', 'horror');
    expect(horror).toBeChecked();
  });

  it('updates the count on refresh without changing the selected value or focused control', () => {
    const change = vi.fn();
    const initial = [
      { value: 'horror', label: 'Хорроры', description: '15 голосов' },
      { value: 'strategy', label: 'Стратегии' },
    ];
    const { rerender } = render(
      <ChoiceGroup label="Твой голос" options={initial} value="horror" onChange={change} />,
    );
    const horror = screen.getByRole('radio', { name: 'Хорроры' });
    expect(horror).toHaveAccessibleDescription('15 голосов');
    horror.focus();

    const refreshed = [
      { value: 'horror', label: 'Хорроры', description: '18 голосов' },
      { value: 'strategy', label: 'Стратегии' },
    ];
    rerender(
      <ChoiceGroup label="Твой голос" options={refreshed} value="horror" onChange={change} />,
    );

    const selected = screen.getByRole('radio', { name: 'Хорроры' });
    expect(selected).toHaveAccessibleDescription('18 голосов');
    expect(selected).toBeChecked();
    expect(selected).toHaveAttribute('value', 'horror');
    expect(selected).toHaveFocus();
    expect(screen.getByText('18 голосов', { exact: true })).toBeVisible();
    expect(screen.queryByText('15 голосов', { exact: true })).not.toBeInTheDocument();
    expect(change).not.toHaveBeenCalled();
  });

  it('removes an old description while preserving the original option name and selection', () => {
    const withDescription = [{ value: 'horror', label: 'Хорроры', description: '15 голосов' }];
    const { rerender } = render(
      <ChoiceGroup
        label="Твой голос"
        options={withDescription}
        value="horror"
        onChange={vi.fn()}
      />,
    );
    expect(screen.getByRole('radio', { name: 'Хорроры' })).toHaveAccessibleDescription(
      '15 голосов',
    );

    const withoutDescription = [{ value: 'horror', label: 'Хорроры' }];
    rerender(
      <ChoiceGroup
        label="Твой голос"
        options={withoutDescription}
        value="horror"
        onChange={vi.fn()}
      />,
    );

    const selected = screen.getByRole('radio', { name: 'Хорроры' });
    expect(selected).toHaveAccessibleName('Хорроры');
    expect(selected).not.toHaveAccessibleDescription();
    expect(selected).toBeChecked();
    expect(screen.queryByText('15 голосов', { exact: true })).not.toBeInTheDocument();
  });

  it('associates each description only with its own option when separate groups reuse the same value', () => {
    const first = [{ value: 'horror', label: 'Хорроры', description: '15 голосов' }];
    const second = [{ value: 'horror', label: 'Хорроры', description: '18 голосов' }];
    render(
      <>
        <ChoiceGroup label="Первое голосование" options={first} value="horror" onChange={vi.fn()} />
        <ChoiceGroup
          label="Второе голосование"
          options={second}
          value="horror"
          onChange={vi.fn()}
        />
      </>,
    );

    const firstRadio = within(screen.getByRole('group', { name: 'Первое голосование' })).getByRole(
      'radio',
      { name: 'Хорроры' },
    );
    const secondRadio = within(screen.getByRole('group', { name: 'Второе голосование' })).getByRole(
      'radio',
      { name: 'Хорроры' },
    );
    expect(firstRadio).toHaveAccessibleDescription('15 голосов');
    expect(secondRadio).toHaveAccessibleDescription('18 голосов');
    expect(firstRadio).toBeChecked();
    expect(secondRadio).toBeChecked();
  });
});
