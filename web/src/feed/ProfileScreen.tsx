import { CalendarRange, MessageSquareQuote, Trophy, UserRoundX } from 'lucide-react';
import { useCallback } from 'react';
import { api, type Schemas } from '../api/client';
import { Link } from '../app/Link';
import { navigate, paths } from '../app/router';
import { moscowDay } from '../app/time';
import { userToken } from '../design/players';
import { ru } from '../i18n/ru';
import { Button } from '../ui/Button';
import { Badge, Chip, Tag } from '../ui/Marks';
import { Skeleton } from '../ui/Progress';
import { EmptyState, ErrorState } from '../ui/States';
import { Sticker } from '../ui/Sticker';
import { Panel } from '../ui/Surface';
import { Quote } from './Review';
import { useLoaded, type Answer } from './useLoaded';

type Profile = Schemas['ProfileView'];

const linkText =
  'rounded-sm underline decoration-2 decoration-muted underline-offset-4 is-hover:decoration-ink';

/** A player's page (H5, SPEC «Профиль»): their name and avatar, how many games they completed, their seasons and reviews */
export function ProfileScreen({
  userId,
  meId,
  onSignedOut,
}: {
  userId: string;
  /** The signed-in user: their own page says «ты» */
  meId: string;
  onSignedOut: () => void;
}) {
  const load = useCallback(async (): Promise<Answer<Profile>> => {
    try {
      const { data, response } = await api.GET('/api/users/{userId}', {
        params: { path: { userId } },
      });
      if (data) return { kind: 'ready', value: data };
      if (response.status === 401) return { kind: 'signedOut' };
      return { kind: response.status === 404 ? 'notFound' : 'failed' };
    } catch {
      return { kind: 'failed' };
    }
  }, [userId]);
  const [state, retry] = useLoaded(load, onSignedOut);

  return (
    <main className="mx-auto grid max-w-180 content-start gap-4 px-4 pt-4 pb-10 desk:px-8 desk:pt-8">
      {state.kind === 'loading' ? (
        <ProfileSkeleton />
      ) : state.kind === 'failed' ? (
        <ErrorState
          level={1}
          title={ru.profile.errorTitle}
          text={ru.feed.errorText}
          onRetry={retry}
        />
      ) : state.kind === 'notFound' ? (
        <EmptyState
          level={1}
          icon={<UserRoundX size={28} aria-hidden />}
          title={ru.profile.notFoundTitle}
          text={ru.profile.notFoundText}
          action={
            <Button
              variant="main"
              onClick={() => {
                navigate(paths.season());
              }}
            >
              {ru.profile.toSeason}
            </Button>
          }
        />
      ) : (
        <ProfileDetails profile={state.value} mine={state.value.id === meId} />
      )}
    </main>
  );
}

/** A loaded profile: also what the styleguide shows */
export function ProfileDetails({ profile, mine }: { profile: Profile; mine: boolean }) {
  return (
    <>
      <header
        className="grid grid-cols-[auto_minmax(0,1fr)] items-center gap-4"
        data-testid="profile"
      >
        <Sticker
          player={{
            name: profile.name,
            // The colour of their token in their latest season, as on its map; none played yet — by the account
            token: profile.seasons[0]?.token ?? userToken(profile.id),
            // The profile shows the whole picture, a GIF moving (DESIGN.md «Производительность»)
            avatar: profile.avatar?.url,
          }}
          size={80}
        />
        <div className="grid min-w-0 justify-items-start gap-2">
          <h1 className="flex flex-wrap items-center gap-2 font-display text-xl font-heavy wrap-anywhere">
            {profile.name}
            {mine ? <Badge tone="me">{ru.profile.you}</Badge> : null}
          </h1>
          <span data-testid="profile-completed">
            <Chip icon={<Trophy size={16} aria-hidden />}>
              {ru.profile.completed(profile.completed)}
            </Chip>
          </span>
        </div>
      </header>

      <Panel title={ru.profile.seasons}>
        {profile.seasons.length === 0 ? (
          <EmptyState
            icon={<CalendarRange size={28} aria-hidden />}
            title={ru.profile.noSeasonsTitle}
            text={ru.profile.noSeasonsText}
          />
        ) : (
          <ul className="grid divide-y-2 divide-muted" data-testid="profile-seasons">
            {profile.seasons.map((season) => (
              <li
                key={season.playerId}
                className="grid grid-cols-[minmax(0,1fr)_auto] items-center gap-x-3 gap-y-1 py-3"
              >
                <Link
                  to={paths.feed(season.seasonId)}
                  className={`font-medium wrap-anywhere ${linkText}`}
                >
                  {season.seasonName}
                </Link>
                <span className="font-display font-heavy tabular-nums">
                  {ru.profile.points(season.points)}
                </span>
                <span className="flex flex-wrap gap-2 text-sm">
                  <Tag>{ru.profile.status[season.status] ?? season.status}</Tag>
                </span>
                <span className="text-sm text-ink-soft tabular-nums">
                  {season.place === null ? ru.profile.noPlace : ru.profile.place(season.place)}
                </span>
              </li>
            ))}
          </ul>
        )}
      </Panel>

      <Panel title={ru.profile.reviews}>
        {profile.reviews.length === 0 ? (
          <EmptyState
            icon={<MessageSquareQuote size={28} aria-hidden />}
            title={ru.profile.noReviewsTitle}
            text={mine ? ru.profile.noReviewsMine : ru.profile.noReviewsText}
          />
        ) : (
          <ul className="grid divide-y-2 divide-muted" data-testid="profile-reviews">
            {profile.reviews.map((review) => (
              <li key={review.runId} className="grid gap-2 py-3">
                <div className="flex flex-wrap items-baseline justify-between gap-x-3">
                  <Link
                    to={paths.game(review.gameId)}
                    className={`font-bold wrap-anywhere ${linkText}`}
                  >
                    {review.gameTitle}
                  </Link>
                  <span className="text-sm text-ink-soft tabular-nums">
                    {review.seasonName}
                    {review.completedAt ? `, ${moscowDay(review.completedAt)}` : ''}
                  </span>
                </div>
                <Quote rating={review.rating} text={review.text} />
              </li>
            ))}
          </ul>
        )}
      </Panel>
    </>
  );
}

function ProfileSkeleton() {
  return (
    <div className="grid gap-4" aria-busy="true" data-testid="profile-loading">
      <p className="sr-only">{ru.app.loading}</p>
      <div className="grid grid-cols-[auto_minmax(0,1fr)] items-center gap-4">
        <Skeleton className="size-20 rounded-full" />
        <div className="grid gap-2">
          <Skeleton className="h-8 w-1/2" />
          <Skeleton className="h-7 w-40 rounded-full" />
        </div>
      </div>
      <Skeleton className="h-40 w-full rounded-lg" />
      <Skeleton className="h-56 w-full rounded-lg" />
    </div>
  );
}
