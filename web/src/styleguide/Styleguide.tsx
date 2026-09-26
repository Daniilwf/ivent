import { CalendarClock, Inbox, Plus, Trophy } from 'lucide-react';
import { useEffect, type ReactNode } from 'react';
import { Shell } from '../app/Shell';
import { RunCard } from '../board/GameCards';
import { ChoiceCard, OfferCard } from '../season/RollResult';
import { Leaderboard } from '../board/Leaderboard';
import { cellsToFinish } from '../board/geometry';
import { demoBoard } from '../board/demoBoard';
import { ru } from '../i18n/ru';
import { Button, IconButton, type ButtonVariant } from '../ui/Button';
import { BottomSheet, ConfirmDanger } from '../ui/Dialogs';
import { Field } from '../ui/Field';
import { Badge, Chip, Tag } from '../ui/Marks';
import { RouteProgress, Skeleton } from '../ui/Progress';
import { ConnectionLost, EmptyState, ErrorState, Notice } from '../ui/States';
import { Sticker } from '../ui/Sticker';
import { Panel } from '../ui/Surface';
import { demoChoice, demoGames, demoMe, demoOffer, demoRoll, demoRows, demoUser } from './fixtures';
import { DiceDemo, FinishDemo, MapDemo, MoveDemo, WheelDemo } from './MomentDemos';
import { FeedList, FeedSkeleton } from '../feed/FeedList';
import { GameDetails } from '../feed/GameScreen';
import { ProfileDetails } from '../feed/ProfileScreen';
import {
  demoFeedDays,
  demoGameCard,
  demoGameRuns,
  demoProfile,
  demoProfileEmpty,
} from './feedFixtures';

// The styleguide's cards act on nothing
const noop = () => undefined;

const t = ru.styleguide;

function Section({
  id,
  title,
  lead,
  children,
}: {
  id: string;
  title: string;
  lead?: string;
  children: ReactNode;
}) {
  return (
    <section id={id} className="grid scroll-mt-4 gap-4 border-t-2 border-muted pt-6">
      <div className="grid gap-1">
        <h2 className="font-display text-xl font-heavy">{title}</h2>
        {lead ? <p className="max-w-prose text-ink-soft">{lead}</p> : null}
      </div>
      {children}
    </section>
  );
}

function Swatch({ token, role }: { token: string; role: string }) {
  return (
    <li className="grid grid-cols-[auto_minmax(0,1fr)] items-center gap-3">
      <span
        className="size-12 rounded-md border-2 border-ink"
        style={{ background: `var(--color-${token})` }}
      />
      <span className="min-w-0">
        <span className="block text-sm font-bold break-all">--color-{token}</span>
        <span className="text-sm text-ink-soft">{role}</span>
      </span>
    </li>
  );
}

const buttonStates: {
  label: string;
  props: { force?: string; disabled?: boolean; loading?: boolean };
}[] = [
  { label: t.states.normal, props: {} },
  { label: t.states.hover, props: { force: 'hover' } },
  { label: t.states.focus, props: { force: 'focus' } },
  { label: t.states.active, props: { force: 'active' } },
  { label: t.states.disabled, props: { disabled: true } },
  { label: t.states.loading, props: { loading: true } },
];

const variants: { variant: ButtonVariant; label: string; text: string }[] = [
  { variant: 'main', label: t.buttons.main, text: ru.board.complete },
  { variant: 'quiet', label: t.buttons.quiet, text: t.buttons.quietText },
  { variant: 'danger', label: t.buttons.danger, text: ru.board.drop },
  { variant: 'dangerMain', label: t.buttons.dangerMain, text: ru.board.dropConfirm },
  { variant: 'link', label: t.buttons.link, text: t.buttons.linkText },
  { variant: 'dangerLink', label: t.buttons.dangerLink, text: ru.board.drop },
];

const cellsLeft = (p: { cell: number }) => cellsToFinish(demoBoard, p.cell);

export function Styleguide() {
  // The page loads after the address: a link to a section scrolls there once the sections exist
  useEffect(() => {
    const id = window.location.hash.slice(1);
    if (id) document.getElementById(id)?.scrollIntoView();
  }, []);
  const me = demoMe;
  const run = demoGames[0] as (typeof demoGames)[number];
  const routeLength = cellsToFinish(demoBoard, 1);
  return (
    <main className="mx-auto grid max-w-300 gap-8 px-4 py-6 desk:px-8">
      <header className="grid gap-3">
        <h1 className="font-display text-2xl font-heavy">{t.title}</h1>
        <p className="max-w-prose text-ink-soft">{t.lead}</p>
        <nav aria-label={t.contents} className="flex flex-wrap gap-2">
          {t.sections.map(([id, name]) => (
            <a
              key={id}
              href={`#${id}`}
              className="inline-flex min-h-11 items-center rounded-full border-2 border-ink bg-card px-4 text-sm font-medium is-hover:bg-page"
            >
              {name}
            </a>
          ))}
        </nav>
      </header>

      <Section id="shell" title={t.shell.title} lead={t.shell.lead}>
        <div className="grid gap-4">
          <div className="h-30 w-full max-w-98 overflow-hidden rounded-lg border-2 border-muted desk:h-16">
            <Shell
              user={demoUser}
              offline
              onChangePassword={() => undefined}
              onLogout={() => undefined}
            >
              {null}
            </Shell>
          </div>
          <div className="h-30 overflow-hidden rounded-lg border-2 border-muted desk:h-16">
            <Shell
              user={demoUser}
              offline={false}
              onChangePassword={() => undefined}
              onLogout={() => undefined}
            >
              {null}
            </Shell>
          </div>
        </div>
      </Section>

      <Section id="colors" title={t.colors.title} lead={t.colors.lead}>
        <ul className="grid gap-4 desk:grid-cols-3">
          {t.colors.list.map(([token, role]) => (
            <Swatch key={token} token={token} role={role} />
          ))}
        </ul>
      </Section>

      <Section id="tokens" title={t.tokens.title} lead={t.tokens.lead}>
        <ul className="flex flex-wrap gap-3">
          {Array.from({ length: 16 }, (_, i) => (
            <li key={i}>
              <Sticker player={{ name: t.tokens.letters[i] ?? '?', token: i }} size={44} />
            </li>
          ))}
        </ul>
      </Section>

      <Section id="type" title={t.type.title} lead={t.type.lead}>
        <div className="grid gap-3">
          <p className="font-display text-3xl font-heavy">+12</p>
          <p className="font-display text-2xl font-heavy">{t.type.h1}</p>
          <p className="font-display text-xl font-heavy">{t.type.h2}</p>
          <p className="font-display text-lg font-heavy">{t.type.h3}</p>
          <p className="max-w-prose text-base">{t.type.body}</p>
          <p className="text-sm text-ink-soft">{t.type.small}</p>
          <p className="text-xs text-ink-soft">{t.type.tiny}</p>
        </div>
      </Section>

      <Section id="shape" title={t.shape.title} lead={t.shape.lead}>
        <div className="flex flex-wrap items-end gap-4">
          {[1, 2, 3, 4, 6, 8, 12].map((n) => (
            <span key={n} className="grid justify-items-center gap-1 text-xs">
              <span className="block bg-action" style={{ width: n * 4, height: n * 4 }} />
              {n * 4}
            </span>
          ))}
        </div>
        <div className="flex flex-wrap gap-4">
          <span className="grid size-24 place-items-center rounded-sm border-2 border-ink bg-card text-xs">
            {t.shape.radius.sm}
          </span>
          <span className="grid size-24 place-items-center rounded-md border-2 border-ink bg-card text-xs">
            {t.shape.radius.md}
          </span>
          <span className="grid size-24 place-items-center rounded-lg border-2 border-ink bg-card text-xs">
            {t.shape.radius.lg}
          </span>
          <span className="grid size-24 place-items-center rounded-lg border-3 border-ink bg-card text-xs shadow-lift">
            {t.shape.lift}
          </span>
        </div>
      </Section>

      <Section id="buttons" title={t.buttons.title} lead={t.buttons.lead}>
        {/* Every variant in every state, wrapping on a phone instead of scrolling */}
        <div className="grid gap-6">
          {variants.map((v) => (
            <div key={v.variant} className="grid gap-2">
              <h3 className="text-sm font-bold">{v.label}</h3>
              <ul className="flex flex-wrap gap-x-4 gap-y-3">
                {buttonStates.map((state) => (
                  <li key={state.label} className="grid justify-items-start gap-1">
                    <span className="text-xs text-ink-soft">{state.label}</span>
                    <Button variant={v.variant} {...state.props}>
                      {v.text}
                    </Button>
                  </li>
                ))}
              </ul>
            </div>
          ))}
          <div className="grid gap-2">
            <h3 className="text-sm font-bold">{t.buttons.icon}</h3>
            <ul className="flex flex-wrap gap-x-4 gap-y-3">
              {buttonStates.slice(0, 5).map((state) => (
                <li key={state.label} className="grid justify-items-start gap-1">
                  <span className="text-xs text-ink-soft">{state.label}</span>
                  <IconButton
                    label={ru.board.zoomIn}
                    {...(state.props.force ? { force: state.props.force } : {})}
                    disabled={state.props.disabled}
                  >
                    <Plus size={20} />
                  </IconButton>
                </li>
              ))}
            </ul>
          </div>
        </div>
      </Section>

      <Section id="marks" title={t.marks.title}>
        <div className="flex flex-wrap items-center gap-3">
          <Chip icon={<CalendarClock size={16} aria-hidden />}>{t.marks.deadline}</Chip>
          <Badge>{ru.board.first}</Badge>
          <Badge tone="me">{ru.board.you}</Badge>
          <Tag>Platformer</Tag>
          <Tag>Online Co-Op</Tag>
          <ConnectionLost />
        </div>
      </Section>

      <Section id="fields" title={t.fields.title} lead={t.fields.lead}>
        <div className="grid max-w-110 gap-4">
          <Field
            label={t.fields.hours}
            hint={t.fields.hoursHint}
            inputMode="decimal"
            defaultValue="27"
          />
          <Field label={t.fields.hours} force="focus" defaultValue="27" />
          <Field label={t.fields.link} error={t.fields.linkError} defaultValue="steam" />
          <Field label={t.fields.link} disabled defaultValue="https://youtu.be/…" />
        </div>
      </Section>

      <Section id="states" title={t.feedback.title} lead={t.feedback.lead}>
        <div className="grid gap-3 desk:grid-cols-2">
          <Notice tone="success">{t.feedback.success}</Notice>
          <Notice tone="info">{t.feedback.info}</Notice>
          <Notice tone="warning">{t.feedback.warning}</Notice>
          <Notice tone="danger">{t.feedback.danger}</Notice>
        </div>
        <div className="grid gap-4 desk:grid-cols-3">
          <EmptyState
            icon={<Inbox size={28} aria-hidden />}
            title={t.feedback.emptyTitle}
            text={t.feedback.emptyText}
            action={<Button variant="main">{t.feedback.emptyAction}</Button>}
          />
          <ErrorState
            title={t.feedback.errorTitle}
            text={t.feedback.errorText}
            onRetry={() => undefined}
          />
          <Panel title={t.feedback.loading} aria-busy="true">
            {[0, 1, 2, 3].map((i) => (
              <span key={i} className="flex items-center gap-3">
                <Skeleton className="size-9 rounded-full" />
                <span className="grid flex-1 gap-2">
                  <Skeleton className="h-4 w-3/4" />
                  <Skeleton className="h-2 w-full" />
                </span>
              </span>
            ))}
          </Panel>
        </div>
      </Section>

      <Section id="progress" title={t.progress.title} lead={t.progress.lead}>
        <div className="grid gap-6 desk:grid-cols-2">
          <div className="grid content-start gap-4">
            <RouteProgress left={cellsLeft(me)} total={routeLength} />
            <RouteProgress left={routeLength} total={routeLength} />
            <RouteProgress left={0} total={routeLength} />
          </div>
          <Panel title={ru.board.leaderboard}>
            <Leaderboard rows={demoRows} limit={6} />
          </Panel>
        </div>
      </Section>

      <Section id="run" title={t.run.title} lead={t.run.lead}>
        <div className="grid gap-4 desk:grid-cols-2">
          <RunCard
            game={run}
            left={cellsLeft(me)}
            total={routeLength}
            dropConsequences={t.run.dropConsequences}
          />
          <RunCard
            game={demoGames[4] ?? run}
            left={routeLength - 2}
            total={routeLength}
            dropConsequences={t.run.dropConsequences}
            busy
          />
        </div>
      </Section>

      <Section id="offer" title={t.offer.title} lead={t.offer.lead}>
        <div className="grid items-start gap-4 desk:grid-cols-2">
          <div className="rounded-lg bg-card p-4">
            <OfferCard
              offer={demoOffer}
              roll={demoRoll}
              price={{ payment: 'coins', coins: 5 }}
              pending={false}
              onStart={noop}
              onAlreadyPlayed={noop}
              onReroll={noop}
            />
          </div>
          <div className="rounded-lg bg-card p-4">
            <ChoiceCard
              choice={demoChoice}
              roll={{ ...demoRoll, misses: [] }}
              price={{ payment: 'freeThisRoll', coins: 0 }}
              pending={false}
              onChoose={noop}
              onAlreadyPlayed={noop}
              onReroll={noop}
            />
          </div>
        </div>
      </Section>

      <Section id="dialogs" title={t.dialogs.title} lead={t.dialogs.lead}>
        <div className="flex flex-wrap gap-3">
          <ConfirmDanger
            trigger={<Button variant="danger">{ru.board.drop}</Button>}
            title={ru.board.dropTitle(run.title)}
            consequences={t.run.dropConsequences}
            confirm={ru.board.dropConfirm}
            onConfirm={() => undefined}
          />
          <BottomSheet
            trigger={
              <Button icon={<Trophy size={20} aria-hidden />}>
                {ru.board.sheet(3, me.points)}
              </Button>
            }
            title={ru.board.leaderboard}
          >
            <Leaderboard rows={demoRows} />
          </BottomSheet>
        </div>
      </Section>

      <Section id="map" title={t.map.title} lead={t.map.lead}>
        <MapDemo />
      </Section>

      <Section id="wheel" title={t.wheel.title} lead={t.wheel.lead}>
        <WheelDemo />
      </Section>

      <Section id="dice" title={t.dice.title} lead={t.dice.lead}>
        <DiceDemo />
      </Section>

      <Section id="move" title={t.move.title} lead={t.move.lead}>
        <MoveDemo />
      </Section>

      <Section id="finish" title={t.finish.title} lead={t.finish.lead}>
        <FinishDemo />
      </Section>

      <Section id="feed" title={t.feed.title} lead={t.feed.lead}>
        <div className="grid items-start gap-4 desk:grid-cols-[minmax(0,2fr)_minmax(0,1fr)]">
          <Panel>
            <FeedList days={demoFeedDays} />
            <div className="grid justify-items-center gap-3 border-t-2 border-muted pt-4">
              <Button>{ru.feed.more}</Button>
              <Button loading>{ru.feed.more}</Button>
              <p className="text-sm text-ink-soft">{ru.feed.start}</p>
            </div>
          </Panel>
          <Panel title={t.feed.loading}>
            <FeedSkeleton rows={4} />
          </Panel>
        </div>
      </Section>

      <Section id="profile" title={t.profile.title} lead={t.profile.lead}>
        <div className="grid max-w-180 gap-4">
          <ProfileDetails profile={demoProfile} mine={false} />
        </div>
        <div className="grid max-w-180 gap-4 border-t-2 border-dashed border-muted pt-4">
          <ProfileDetails profile={demoProfileEmpty} mine />
        </div>
      </Section>

      <Section id="game" title={t.gamePage.title} lead={t.gamePage.lead}>
        <div className="grid max-w-180 gap-4">
          <GameDetails game={demoGameCard} runs={demoGameRuns} />
        </div>
        <div className="grid max-w-180 gap-4 border-t-2 border-dashed border-muted pt-4">
          <GameDetails
            game={{
              ...demoGameCard,
              id: 'empty',
              title: 'Hollow Knight',
              isCoop: false,
              year: null,
              completionCondition: null,
              tags: ['Platformer'],
            }}
            runs={[]}
          />
        </div>
      </Section>
    </main>
  );
}
