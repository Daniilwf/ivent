import { render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { Schemas } from '../api/client';
import { demoRules, demoRuleset } from './demoRules';
import { RulesContent } from './RulesScreen';

const activeRules = {
  ...demoRuleset,
  features: {
    ...demoRuleset.features,
    items: true,
    coop: true,
    duels: true,
    partnerBoard: true,
    weeklyChallenge: true,
    suggestions: true,
    polls: true,
    globalEvents: true,
    events: true,
  },
  reward: {
    ...demoRuleset.reward,
    coop: { ...demoRuleset.reward.coop, pointsShare: 0.63, coinsShare: 0.26 },
  },
  interactions: { inviteTtlHours: 37, maxOpenInvitesPerPlayer: 8 },
  partnerBoard: { requestTtlHours: 53 },
  duels: {
    rewardCoins: 17,
    durationDays: 11,
    maxOpenPerPlayer: 4,
    countRunsStartedAfterAccept: true,
    samePairCooldownDays: 19,
    maxWinsPerSeason: 6,
    dropForfeits: true,
  },
  weeklyChallenge: { durationDays: 9, defaultRewardCoins: 357 },
  suggestions: { perDay: 7 },
  polls: { gameOptions: 13, allowChangeVote: false, showLiveCountsDefault: true },
  globalEvents: { maxActive: 3 },
  events: { defaultDeck: 'good', answerTimeoutHours: 29, irlProof: { maxFiles: 4 } },
} satisfies Schemas['Ruleset'];

const coopHeading = /^Кооп(?:-прохождения)?$/iu;
const invitationHeading = /^Приглашения$/iu;
const boardHeading = /^Доска [«„"]ищу напарника[»“"]$/iu;
const duelHeading = /^Дуэли$/iu;
const weeklyHeading = /^Челлендж недели$/iu;
const suggestionHeading = /^Предложка$/iu;
const pollHeading = /^Голосования$/iu;
const globalHeading = /^Общие ивенты$/iu;
const eventHeading = /^Ивенты(?: и IRL-пруфы)?$/iu;

function section(name: RegExp) {
  return screen.getByRole('region', { name });
}

describe('stage 5 on the season rules page', () => {
  it('shows the configured coop shares, invitation limits and partner request lifetime', () => {
    render(<RulesContent rules={{ ...demoRules, ruleset: activeRules }} />);

    expect(section(coopHeading)).toHaveTextContent(/63\s*%/u);
    expect(section(coopHeading)).toHaveTextContent(/26\s*%/u);
    expect(section(coopHeading)).toHaveTextContent(/округл[^.]*вверх/iu);
    expect(section(invitationHeading)).toHaveTextContent(/37\s*ч/u);
    expect(section(invitationHeading)).toHaveTextContent(/\b8\b/u);
    expect(section(invitationHeading)).toHaveTextContent(/кооп/iu);
    expect(section(invitationHeading)).toHaveTextContent(/дуэл/iu);
    expect(section(boardHeading)).toHaveTextContent(/53\s*ч/u);
  });

  it('shows the live duel, weekly challenge and suggestion rules from the season', () => {
    render(<RulesContent rules={{ ...demoRules, ruleset: activeRules }} />);

    expect(section(duelHeading)).toHaveTextContent(/17\s*монет/u);
    expect(section(duelHeading)).toHaveTextContent(/11\s*дн/u);
    expect(section(duelHeading)).toHaveTextContent(/\b4\b/u);
    expect(section(duelHeading)).toHaveTextContent(/19\s*дн/u);
    expect(section(duelHeading)).toHaveTextContent(/\b6\b/u);
    expect(section(weeklyHeading)).toHaveTextContent(/9\s*дн/u);
    expect(section(weeklyHeading)).not.toHaveTextContent('357');
    expect(section(suggestionHeading)).toHaveTextContent(/(?=.*\b7\b)(?=.*день)/iu);
  });

  it('keeps a small positive coop share above zero', () => {
    render(
      <RulesContent
        rules={{
          ...demoRules,
          ruleset: {
            ...activeRules,
            reward: {
              ...activeRules.reward,
              coop: { ...activeRules.reward.coop, pointsShare: 1e-9 },
            },
          },
        }}
      />,
    );

    expect(section(coopHeading)).toHaveTextContent(/0,0000001\s*%/u);
  });

  it('shows the voting and event limits, with links in the condensed contents', async () => {
    render(<RulesContent rules={{ ...demoRules, ruleset: activeRules }} />);

    expect(section(pollHeading)).toHaveTextContent(/\b13\b/u);
    expect(section(globalHeading)).toHaveTextContent(/\b3\b/u);
    expect(section(eventHeading)).toHaveTextContent(/29\s*ч/u);
    expect(section(eventHeading)).toHaveTextContent(/\b4\b/u);

    const contents = within(screen.getByRole('navigation', { name: 'На этой странице' }));
    const toggle = contents.getByText('На этой странице', { exact: true });
    const disclosure = toggle.closest('details');
    expect(disclosure).not.toBeNull();
    expect(disclosure).not.toHaveAttribute('open');
    await userEvent.click(toggle);
    expect(disclosure).toHaveAttribute('open');
    for (const heading of [pollHeading, globalHeading, eventHeading]) {
      const id = section(heading).id;
      expect(id).not.toBe('');
      expect(contents.getByRole('link', { name: heading })).toHaveAttribute('href', `#${id}`);
    }
  });

  it('describes a link-only proof, a question without a timeout and the duel zeros', () => {
    const ruleset = {
      ...activeRules,
      duels: { ...activeRules.duels, rewardCoins: 0, samePairCooldownDays: 0 },
      events: { defaultDeck: 'good', answerTimeoutHours: null, irlProof: { maxFiles: 0 } },
      interactions: { inviteTtlHours: 37, maxOpenInvitesPerPlayer: null },
      reward: { ...activeRules.reward, coop: { ...activeRules.reward.coop, coinsShare: null } },
    } satisfies Schemas['Ruleset'];
    render(<RulesContent rules={{ ...demoRules, ruleset }} />);

    expect(section(eventHeading)).toHaveTextContent(/без срока|нет срока|срок[^.]*не ограничен/iu);
    expect(section(eventHeading)).toHaveTextContent(/только[^.]*ссылк/iu);
    expect(section(duelHeading)).toHaveTextContent(/0\s*монет|без[^.]*наград|монет[^.]*нет/iu);
    expect(section(duelHeading)).toHaveTextContent(
      /0\s*дн|без[^.]*паузы|без[^.]*ожидани|нет[^.]*пауз|можно[^.]*сразу/iu,
    );
    expect(section(invitationHeading)).toHaveTextContent(/37\s*ч/u);
    expect(section(invitationHeading)).not.toHaveTextContent(/\b8\b|\bnull\b/u);
    expect(section(coopHeading)).toHaveTextContent(/63\s*%/u);
    expect(section(coopHeading)).not.toHaveTextContent(/26\s*%|\bnull\b/u);
  });

  it.each([
    ['disabled', { ...activeRules, features: demoRuleset.features }],
    ['legacy', demoRuleset],
  ])('hides the stage 5 sections and their links with %s mechanics', (_name, ruleset) => {
    render(<RulesContent rules={{ ...demoRules, ruleset }} />);

    for (const name of [
      coopHeading,
      invitationHeading,
      boardHeading,
      duelHeading,
      weeklyHeading,
      suggestionHeading,
      pollHeading,
      globalHeading,
      eventHeading,
    ]) {
      expect(screen.queryByRole('region', { name })).not.toBeInTheDocument();
      expect(screen.queryByRole('link', { name })).not.toBeInTheDocument();
    }
  });

  it('describes the server history using Russian terms for changed mechanics and fields', () => {
    const changes = [
      { path: 'features.duels', before: 'false', after: 'true', label: /дуэл/iu },
      { path: 'reward.coop.coinsShare', before: '0.5', after: '0.26', label: /монет/iu },
      { path: 'interactions.inviteTtlHours', before: '24', after: '37', label: /приглашен/iu },
      {
        path: 'partnerBoard.requestTtlHours',
        before: '48',
        after: '53',
        label: /напарник|запрос/iu,
      },
      { path: 'duels.samePairCooldownDays', before: '14', after: '19', label: /дуэл|пар/iu },
      { path: 'weeklyChallenge.durationDays', before: '7', after: '9', label: /челлендж/iu },
      { path: 'suggestions.perDay', before: '3', after: '7', label: /предлож/iu },
      { path: 'polls.gameOptions', before: '5', after: '13', label: /голосован/iu },
      { path: 'globalEvents.maxActive', before: '1', after: '3', label: /ивент/iu },
      { path: 'events.irlProof.maxFiles', before: '5', after: '4', label: /IRL|фото/iu },
    ];
    render(
      <RulesContent
        rules={{
          ...demoRules,
          ruleset: activeRules,
          version: 4,
          history: [
            {
              version: 4,
              at: '2026-10-20T10:00:00Z',
              authorId: 'a0000000-0000-0000-0000-000000000001',
              authorName: 'Админ',
              changes,
            },
            ...demoRules.history,
          ],
        }}
      />,
    );
    const history = within(screen.getByTestId('rules-history'));
    const [latestVersion] = history.getAllByRole('listitem');
    if (!latestVersion) throw new Error('The latest rules version is missing');
    const rows = within(latestVersion).getAllByRole('listitem');
    expect(rows).toHaveLength(changes.length);
    for (const [index, { path, label }] of changes.entries()) {
      expect(rows[index]).toHaveTextContent(label);
      expect(rows[index]).not.toHaveTextContent(path);
      expect(rows[index]).toHaveTextContent('Было');
      expect(rows[index]).toHaveTextContent('Стало');
    }
  });
});
