import { EyeOff, ImageOff } from 'lucide-react';
import { useState, type RefObject } from 'react';
import { moscowTime } from '../app/time';
import { ReactionButtons, type ChangeReaction } from '../feed/ReviewReactionButtons';
import { ru } from '../i18n/ru';
import { Button } from '../ui/Button';
import { Confirm, FormDialog } from '../ui/Dialogs';
import { Skeleton } from '../ui/Progress';
import { cx } from '../ui/cx';
import { Notice } from '../ui/States';
import type { GalleryMoment } from './galleryApi';

const t = ru.gallery;

export function GalleryMomentDialog({
  item,
  close,
  admin,
  hide,
  changeReaction,
  reactionsUnavailable,
  returnFocus,
}: {
  item: GalleryMoment;
  close: () => void;
  admin: boolean;
  hide: (id: string) => Promise<boolean>;
  changeReaction: ChangeReaction;
  reactionsUnavailable: boolean;
  returnFocus: RefObject<HTMLElement | null>;
}) {
  const [picture, setPicture] = useState<'loading' | 'ready' | 'missing'>('loading');
  const [confirm, setConfirm] = useState(false);
  const [hiding, setHiding] = useState(false);
  const [hideFailed, setHideFailed] = useState(false);

  async function confirmHide() {
    setHiding(true);
    const hidden = await hide(item.id);
    if (!hidden) {
      setHiding(false);
      setHideFailed(true);
    }
  }

  return (
    <FormDialog
      open
      wide
      title={t.details}
      onOpenChange={(open) => {
        if (!open) close();
      }}
      returnFocus={returnFocus}
    >
      <figure className="grid min-w-0 gap-3">
        <div className="relative grid h-68 max-h-gallery-photo place-items-center overflow-hidden rounded-md bg-page desk:h-112">
          {picture === 'missing' ? (
            <Notice tone="warning">{t.pictureMissing}</Notice>
          ) : (
            <>
              <img
                src={item.imageUrl}
                alt={t.fullImage(item.playerName)}
                onLoad={() => {
                  setPicture('ready');
                }}
                onError={() => {
                  setPicture('missing');
                }}
                className={cx('size-full object-contain', picture === 'loading' && 'opacity-0')}
              />
              {picture === 'loading' ? <Skeleton className="absolute inset-0 rounded-md" /> : null}
            </>
          )}
        </div>
        <figcaption className="grid min-w-0 gap-1 wrap-anywhere">
          <p className="font-bold whitespace-pre-wrap">{item.caption}</p>
          <p className="flex flex-wrap gap-x-3 text-sm text-ink-soft">
            <span>{item.playerName}</span>
            <time dateTime={item.createdAt}>{moscowTime(item.createdAt)}</time>
          </p>
        </figcaption>
      </figure>
      {item.reactions?.length ? (
        <ReactionButtons
          targetId={item.id}
          groupLabel={t.reactions}
          reactions={item.reactions}
          changeReaction={changeReaction}
          stale={false}
        />
      ) : null}
      {reactionsUnavailable && item.reactions?.length ? (
        <Notice tone="warning">{t.reactionsUnavailable}</Notice>
      ) : null}
      {admin ? (
        <div className="grid justify-items-start">
          <Confirm
            trigger={<Button icon={<EyeOff size={18} aria-hidden />}>{t.hide}</Button>}
            title={t.hideQuestion}
            consequences={[t.hideConsequence]}
            confirm={t.hide}
            open={confirm}
            onOpenChange={(open) => {
              if (hiding) return;
              setConfirm(open);
              setHideFailed(false);
            }}
            busy={hiding}
            onConfirm={() => void confirmHide()}
          >
            {hideFailed ? <Notice tone="danger">{t.hideFailed}</Notice> : null}
          </Confirm>
        </div>
      ) : null}
    </FormDialog>
  );
}

export function GalleryThumbnail({ item }: { item: GalleryMoment }) {
  const [missing, setMissing] = useState(false);
  return missing ? (
    <span
      className="grid aspect-video w-full place-items-center bg-muted text-ink-soft"
      aria-hidden
    >
      <ImageOff size={28} />
    </span>
  ) : (
    <img
      src={item.thumbnailUrl}
      alt=""
      loading="lazy"
      decoding="async"
      className="aspect-video w-full bg-muted object-cover"
      onError={() => {
        setMissing(true);
      }}
    />
  );
}
