import { useEffect, useState } from 'react';
import type { Schemas } from '../api/client';
import { ru } from '../i18n/ru';

// What the panels of my economy share (4.11a, 4.11c): the loaded view, the answer to a command, the refusal in words

export type Economy = Schemas['EconomyView'];

/** The answer of the server to a command: done, or refused with the engine's code in words */
export type UseResult = { ok: true } | { ok: false; message: string };

export type Loaded =
  | { kind: 'loading' }
  | { kind: 'failed' }
  | { kind: 'signedOut' }
  | { kind: 'ready'; economy: Economy };

/** A panel's state: signed out shows as loading while the page goes to the login */
export type PanelState = Exclude<Loaded, { kind: 'signedOut' }>;

/** A refusal of the engine in words: the mechanic's own words first, then the common ones */
export function refusalText(code: string | null | undefined, own?: Record<string, string>): string {
  if (!code) return ru.rejection.unknown;
  return own?.[code] ?? ru.rejection[code] ?? ru.rejection.unknown;
}

/** Now, again every second while `ticking`: the timer of the lots counts down and they vanish on time */
export function useNow(ticking: boolean, fixed?: Date): Date {
  const [now, setNow] = useState(() => new Date());
  useEffect(() => {
    if (fixed || !ticking) return;
    const timer = setInterval(() => {
      setNow(new Date());
    }, 1000);
    return () => {
      clearInterval(timer);
    };
  }, [ticking, fixed]);
  return fixed ?? now;
}

/** What is left until `until`, as m:ss (or h:mm:ss): tabular digits, so the timer does not jump */
export function timeLeft(until: Date, now: Date): string {
  const total = Math.max(0, Math.ceil((until.getTime() - now.getTime()) / 1000));
  const h = Math.floor(total / 3600);
  const m = Math.floor((total % 3600) / 60);
  const s = String(total % 60).padStart(2, '0');
  return h > 0 ? `${h}:${String(m).padStart(2, '0')}:${s}` : `${m}:${s}`;
}

/** The win of a stake: ⌊stake × multiplier⌋, as the engine pays it (D-414) */
export const payoutOf = (stake: number, multiplier: number) => Math.floor(stake * multiplier);
