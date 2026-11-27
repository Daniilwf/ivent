import { ClipboardCheck, ExternalLink } from 'lucide-react';
import { useCallback, useState } from 'react';
import { api, rejectionCode, type Schemas } from '../api/client';
import { moscowTime } from '../app/time';
import { answerOf, useLoaded, type Answer } from '../app/useLoaded';
import { ru } from '../i18n/ru';
import { parseIrlLink } from '../season/irlLink';
import { AsyncState } from '../ui/AsyncState';
import { standaloneLink } from '../ui/buttonStyles';
import { cx } from '../ui/cx';
import { EmptyState, Notice } from '../ui/States';
import { ReviewActions } from './stage5/ReviewActions';

const t = ru.admin.stage5Moderation.irlProofs;
type Item = Schemas['IrlProofQueueItemView'];
type Props = { seasonId: string; version: number };

export function IrlProofQueue(props: Props) {
  return <QueueForSeason key={props.seasonId} {...props} />;
}

function QueueForSeason({ seasonId, version }: Props) {
  const load = useCallback(async (): Promise<Answer<Item[] | null>> => {
    const response = await api.GET('/api/admin/seasons/{seasonId}/irl-proofs', {
      params: { path: { seasonId } },
    });
    if (rejectionCode(response.error) === 'feature.disabled') return { kind: 'ready', value: null };
    return answerOf(response);
  }, [seasonId]);
  const loaded = useLoaded(load, { version });
  const [done, setDone] = useState<string>();

  return (
    <AsyncState loaded={loaded} errorTitle={ru.admin.loadErrorTitle}>
      {(items, reload) => (
        <div className="grid gap-4" data-testid="irl-proof-queue">
          {done ? <Notice tone="success">{done}</Notice> : null}
          {items?.length ? (
            <ol className="grid gap-4">
              {items.map((item, index) => (
                <li key={item.effectId} className="min-w-0">
                  <ProofItem
                    item={item}
                    seasonId={seasonId}
                    first={index === 0}
                    onDone={(approved) => {
                      setDone(approved ? t.approved : t.rejected);
                      reload();
                    }}
                  />
                </li>
              ))}
            </ol>
          ) : (
            <EmptyState
              level={2}
              icon={<ClipboardCheck size={28} aria-hidden />}
              title={items === null ? t.disabledTitle : t.emptyTitle}
              text={items === null ? t.disabledText : t.emptyText}
            />
          )}
        </div>
      )}
    </AsyncState>
  );
}

function ProofItem({
  seasonId,
  item,
  first,
  onDone,
}: {
  seasonId: string;
  item: Item;
  first: boolean;
  onDone: (approved: boolean) => void;
}) {
  const video = item.link ? parseIrlLink(item.link) : null;
  const files = item.files.filter(isProofPhoto);

  return (
    <article className="grid gap-3 rounded-lg border-2 border-muted bg-card p-4">
      <header className="grid gap-1">
        <h2 className="font-display text-lg font-heavy wrap-anywhere">{item.name}</h2>
        <div className="flex flex-wrap items-baseline gap-x-3 gap-y-1 text-sm text-ink-soft">
          <span className="font-medium wrap-anywhere">{item.playerName}</span>
          <time dateTime={item.submittedAt}>{moscowTime(item.submittedAt)}</time>
        </div>
      </header>
      {item.description ? (
        <p className="max-w-prose whitespace-pre-wrap wrap-anywhere">{item.description}</p>
      ) : null}
      {files.length > 0 ? (
        <ul className="flex flex-wrap gap-3">
          {files.map((file, index) => (
            <li key={file.id}>
              <a
                href={file.url}
                target="_blank"
                rel="noopener noreferrer"
                className="block rounded-md is-focus:focus-ring"
              >
                <img
                  src={file.thumbnailUrl}
                  alt={ru.irlProof.photoAlt(index + 1)}
                  className="size-24 rounded-md border-2 border-muted bg-page object-cover"
                  loading="lazy"
                />
              </a>
            </li>
          ))}
        </ul>
      ) : null}
      {video ? (
        <a
          href={video.href}
          target="_blank"
          rel="noopener noreferrer"
          className={cx(standaloneLink, 'min-w-0 justify-self-start wrap-anywhere')}
        >
          <ExternalLink size={16} className="shrink-0" aria-hidden />
          <span>
            {t.openVideo} ({video.hostname})
          </span>
        </a>
      ) : null}
      {(!video && item.link) || files.length !== item.files.length ? (
        <Notice tone="warning">{t.mediaUnavailable}</Notice>
      ) : null}
      <ReviewActions
        first={first}
        rejectTitle={t.rejectTitle(item.name)}
        rejectConsequence={t.rejectConsequence}
        send={(commandId, approved, comment) =>
          api.POST('/api/admin/seasons/{seasonId}/effects/{effectId}/irl-proof/review', {
            params: { path: { seasonId, effectId: item.effectId } },
            body: { commandId, approved, comment },
          })
        }
        onDone={onDone}
      />
    </article>
  );
}

function isProofPhoto(file: Schemas['FileLinkView']): boolean {
  if (!/^[\da-f]{8}(?:-[\da-f]{4}){3}-[\da-f]{12}$/i.test(file.id)) return false;
  const path = `/api/files/${file.id}`;
  return file.url === path && file.thumbnailUrl === `${path}/thumbnail`;
}
