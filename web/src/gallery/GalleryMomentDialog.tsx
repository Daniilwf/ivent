import { EyeOff, ImageOff, Undo2 } from 'lucide-react';
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

export type GalleryMomentAction = 'hide' | 'withdraw';
type ChangeVisibility = (id: string, action: GalleryMomentAction) => Promise<boolean>;

const momentActions = {
  hide: {
    Icon: EyeOff,
    label: t.hide,
    question: t.hideQuestion,
    consequence: t.hideConsequence,
    failed: t.hideFailed,
  },
  withdraw: {
    Icon: Undo2,
    label: t.withdraw,
    question: t.withdrawQuestion,
    consequence: t.withdrawConsequence,
    failed: t.withdrawFailed,
  },
};

export function GalleryMomentConfirm({
  id,
  action,
  changeVisibility,
  describedBy,
}: {
  id: string;
  action: GalleryMomentAction;
  changeVisibility: ChangeVisibility;
  describedBy?: string;
}) {
  const [open, setOpen] = useState(false);
  const [busy, setBusy] = useState(false);
  const [failed, setFailed] = useState(false);
  const { Icon, label, question, consequence, failed: failure } = momentActions[action];

  async function confirmChange() {
    setBusy(true);
    setFailed(false);
    if (!(await changeVisibility(id, action))) {
      setBusy(false);
      setFailed(true);
    }
  }

  return (
    <Confirm
      trigger={
        <Button aria-describedby={describedBy} icon={<Icon size={18} aria-hidden />}>
          {label}
        </Button>
      }
      title={question}
      consequences={[consequence]}
      confirm={label}
      stackActionsOnPhone={action === 'withdraw'}
      open={open}
      onOpenChange={(next) => {
        if (busy) return;
        setOpen(next);
        setFailed(false);
      }}
      busy={busy}
      onConfirm={() => void confirmChange()}
    >
      {failed ? <Notice tone="danger">{failure}</Notice> : null}
    </Confirm>
  );
}

export function GalleryMomentDialog({
  item,
  close,
  admin,
  changeVisibility,
  changeReaction,
  reactionsUnavailable,
  returnFocus,
}: {
  item: GalleryMoment;
  close: () => void;
  admin: boolean;
  changeVisibility: ChangeVisibility;
  changeReaction: ChangeReaction;
  reactionsUnavailable: boolean;
  returnFocus: RefObject<HTMLElement | null>;
}) {
  const [picture, setPicture] = useState<'loading' | 'ready' | 'missing'>('loading');

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
      {item.canWithdraw || admin ? (
        <div className="flex flex-wrap gap-2">
          {item.canWithdraw ? (
            <GalleryMomentConfirm
              id={item.id}
              action="withdraw"
              changeVisibility={changeVisibility}
            />
          ) : null}
          {admin ? (
            <GalleryMomentConfirm id={item.id} action="hide" changeVisibility={changeVisibility} />
          ) : null}
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
