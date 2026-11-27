import { useEffect, useEffectEvent, useRef } from 'react';
import type { Schemas } from '../api/client';

type Entry = Schemas['FeedEntryView'];

function disclosureDelay({ type, data }: Entry) {
  if (
    type !== 'game-roll-hidden' ||
    data === null ||
    typeof data !== 'object' ||
    !('disclosureInMilliseconds' in data)
  )
    return null;
  const delay = data.disclosureInMilliseconds;
  return typeof delay === 'number' && Number.isSafeInteger(delay) && delay > 0 ? delay : null;
}

/** A discarded roll has no live receipt. Recheck its visible placeholder after the server's remaining delay. */
export function useRollDisclosureRefresh(
  entries: readonly Entry[] | null,
  refresh: (due: readonly Entry[]) => void,
) {
  const refreshDue = useEffectEvent(refresh);
  const deadlines = useRef(new WeakMap<Entry, number>());
  const checked = useRef(new WeakSet<Entry>());

  useEffect(() => {
    const now = performance.now();
    const pending = (entries ?? []).flatMap((entry) => {
      const delay = disclosureDelay(entry);
      if (delay === null || checked.current.has(entry)) return [];
      const at = deadlines.current.get(entry) ?? now + delay;
      deadlines.current.set(entry, at);
      return [{ entry, at }];
    });
    pending.sort((a, b) => a.at - b.at);

    let timer: ReturnType<typeof setTimeout> | undefined;
    let next = 0;
    function schedule() {
      const first = pending[next];
      if (!first) return;
      timer = setTimeout(
        () => {
          const ready = performance.now();
          const due: Entry[] = [];
          for (let item = pending[next]; item && item.at <= ready; item = pending[++next]) {
            checked.current.add(item.entry);
            due.push(item.entry);
          }
          if (due.length) refreshDue(due);
          schedule();
        },
        Math.max(0, Math.ceil(first.at - performance.now())),
      );
    }

    schedule();
    return () => {
      clearTimeout(timer);
    };
  }, [entries]);
}
