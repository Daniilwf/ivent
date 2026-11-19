import { ClipboardList, ExternalLink } from 'lucide-react';
import { useCallback, useState } from 'react';
import { api, rejectionCode, type Schemas } from '../api/client';
import { moscowTime } from '../app/time';
import { answerOf, useLoaded, type Answer } from '../app/useLoaded';
import { ru } from '../i18n/ru';
import { AsyncState } from '../ui/AsyncState';
import { inlineLink } from '../ui/buttonStyles';
import { Select } from '../ui/Field';
import { Chip } from '../ui/Marks';
import { EmptyState, Notice } from '../ui/States';
import { ReviewActions } from './stage5/ReviewActions';

const t = ru.admin.stage5Moderation.suggestions;
type Suggestion = Schemas['SuggestionView'];
type SuggestionFilter = Suggestion['status'] | 'all';
type Items = { suggestions: Suggestion[]; playerNames: Map<string, string> };
type Props = { seasonId: string; version: number };
const filters: readonly SuggestionFilter[] = ['pending', 'approved', 'rejected', 'all'];
const statusTone = { pending: 'info', approved: 'success', rejected: 'muted' } as const;

async function loadItems(seasonId: string): Promise<Answer<Items | null>> {
  const params = { path: { seasonId } };
  const response = await api.GET('/api/admin/seasons/{seasonId}/suggestions', { params });
  // The flag may be turned off between the rules and the list requests.
  if (rejectionCode(response.error) === 'feature.disabled') return { kind: 'ready', value: null };
  const list = answerOf(response);
  if (list.kind !== 'ready') return list;
  const playerNames = new Map<string, string>();
  if (list.value.length > 0) {
    const players = answerOf(await api.GET('/api/admin/seasons/{seasonId}/players', { params }));
    if (players.kind !== 'ready') return players;
    for (const { id, name } of players.value) playerNames.set(id, name);
  }
  return { kind: 'ready', value: { suggestions: list.value, playerNames } };
}

export function SuggestionsSection(props: Props) {
  return <SuggestionsForSeason key={props.seasonId} {...props} />;
}

function SuggestionsForSeason({ seasonId, version }: Props) {
  const load = useCallback(() => loadItems(seasonId), [seasonId]);
  const loaded = useLoaded(load, { version });
  const [filter, setFilter] = useState<SuggestionFilter>('pending');
  const [done, setDone] = useState<{ approved: boolean; warnings: string[] }>();

  return (
    <AsyncState loaded={loaded} errorTitle={ru.admin.loadErrorTitle}>
      {(items, reload) => {
        if (!items) {
          return (
            <EmptyState
              level={2}
              icon={<ClipboardList size={28} aria-hidden />}
              title={t.disabledTitle}
              text={t.disabledText}
            />
          );
        }
        const visible =
          filter === 'all'
            ? items.suggestions
            : items.suggestions.filter((item) => item.status === filter);
        const firstPending = visible.find((item) => item.status === 'pending')?.id;
        const anySuggestions = items.suggestions.length > 0;
        return (
          <div className="grid gap-4" data-testid="suggestions-list">
            {done ? (
              <Notice tone="success">{done.approved ? t.approved : t.rejected}</Notice>
            ) : null}
            {done?.warnings.map((warning) => (
              <Notice key={warning} tone="warning">
                {warning}
              </Notice>
            ))}
            {anySuggestions ? (
              <Select
                label={t.statusFilter}
                value={filter}
                onChange={(event) => {
                  const value = filters.find((status) => status === event.target.value);
                  if (value) setFilter(value);
                }}
                className="desk:max-w-64"
              >
                {filters.map((status) => (
                  <option key={status} value={status}>
                    {t.statuses[status]}
                  </option>
                ))}
              </Select>
            ) : null}
            {visible.length > 0 ? (
              <ul className="grid gap-4">
                {visible.map((item) => (
                  <li key={item.id} className="min-w-0">
                    <SuggestionCard
                      seasonId={seasonId}
                      item={item}
                      playerName={items.playerNames.get(item.playerId) ?? t.unknownAuthor}
                      first={item.id === firstPending}
                      onDone={(approved, response) => {
                        const warnings = response.warnings?.map(t.poolWarning) ?? [];
                        setDone({ approved, warnings: [...new Set(warnings)] });
                        reload();
                      }}
                    />
                  </li>
                ))}
              </ul>
            ) : (
              <EmptyState
                level={2}
                icon={<ClipboardList size={28} aria-hidden />}
                title={anySuggestions ? t.filteredEmpty[filter] : t.emptyTitle}
                {...(anySuggestions ? {} : { text: t.emptyText })}
              />
            )}
          </div>
        );
      }}
    </AsyncState>
  );
}

function SuggestionCard({
  item,
  playerName,
  seasonId,
  first,
  onDone,
}: {
  item: Suggestion;
  playerName: string;
  seasonId: string;
  first: boolean;
  onDone: (approved: boolean, response: Schemas['CommandResponse']) => void;
}) {
  return (
    <article className="grid gap-3 rounded-lg border-2 border-muted bg-card p-4">
      <header className="grid gap-1">
        <div className="flex flex-wrap items-start justify-between gap-2">
          <h2 className="min-w-0 font-display text-lg font-heavy wrap-anywhere">
            {item.game?.title ?? t.kinds[item.kind]}
          </h2>
          <Chip tone={statusTone[item.status]}>{t.statuses[item.status]}</Chip>
        </div>
        <div className="flex flex-wrap items-baseline gap-x-3 gap-y-1 text-sm text-ink-soft">
          <span className="font-medium wrap-anywhere">{playerName}</span>
          <time dateTime={item.submittedAt}>{moscowTime(item.submittedAt)}</time>
        </div>
      </header>
      {item.text ? (
        <p className="max-w-prose whitespace-pre-wrap wrap-anywhere">{item.text}</p>
      ) : null}
      {item.game ? <ProposedGame game={item.game} /> : null}
      {item.comment ? (
        <dl className="grid gap-1 rounded-md bg-page p-3 text-sm">
          <dt className="font-bold">{ru.admin.comment}</dt>
          <dd className="whitespace-pre-wrap wrap-anywhere">{item.comment}</dd>
        </dl>
      ) : null}
      {item.status === 'pending' ? (
        <ReviewActions
          first={first}
          rejectTitle={t.rejectTitle}
          rejectConsequence={t.rejectConsequence}
          send={(commandId, approve, comment) =>
            api.POST('/api/admin/seasons/{seasonId}/suggestions/{suggestionId}/review', {
              params: { path: { seasonId, suggestionId: item.id } },
              body: { commandId, approve, comment },
            })
          }
          onDone={onDone}
        />
      ) : null}
    </article>
  );
}

function ProposedGame({ game }: { game: Schemas['GameCard'] }) {
  const tags = Array.isArray(game.tags)
    ? game.tags.filter((tag): tag is string => typeof tag === 'string')
    : [];
  const coverId = game.coverFileId;
  const steamId = game.steamAppId;
  const fields = [
    { label: ru.pool.form.year, value: game.year },
    { label: ru.pool.form.hours, value: game.hours },
    { label: ru.pool.form.categories, value: tags.join(', ') },
    { label: ru.pool.form.note, value: game.note },
    { label: t.completionCondition, value: game.completionCondition },
  ].filter(({ value }) => value != null && value !== '');

  return (
    <div className="flex flex-wrap items-start gap-3 rounded-md bg-page p-3 text-sm">
      {coverId && /^[\da-f]{8}(?:-[\da-f]{4}){3}-[\da-f]{12}$/i.test(coverId) ? (
        <img
          src={`/api/files/${coverId}/thumbnail`}
          alt=""
          loading="lazy"
          className="size-24 rounded-md object-cover"
        />
      ) : null}
      <div className="grid min-w-0 gap-2">
        {game.isCoop ? <span className="font-bold">{ru.pool.form.coop}</span> : null}
        {fields.length > 0 ? (
          <dl className="grid gap-2">
            {fields.map(({ label, value }) => (
              <div key={label}>
                <dt className="font-bold">{label}</dt>
                <dd className="whitespace-pre-wrap wrap-anywhere">{value}</dd>
              </div>
            ))}
          </dl>
        ) : null}
        {steamId && /^\d{1,10}$/.test(steamId) ? (
          <a
            href={`https://store.steampowered.com/app/${steamId}/`}
            target="_blank"
            rel="noopener noreferrer"
            className={inlineLink}
          >
            {t.steam} <ExternalLink size={16} className="inline" aria-hidden />
          </a>
        ) : null}
      </div>
    </div>
  );
}
