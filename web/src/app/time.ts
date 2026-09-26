import { ru } from '../i18n/ru';

// Times on the site are Moscow time with an explicit label (SPEC): the players live in different time zones, the
// season's deadline does not.

const zone = 'Europe/Moscow';
const day = new Intl.DateTimeFormat('ru-RU', { timeZone: zone, day: 'numeric', month: 'long' });
const dayWithYear = new Intl.DateTimeFormat('ru-RU', {
  timeZone: zone,
  day: 'numeric',
  month: 'long',
  year: 'numeric',
});
const clock = new Intl.DateTimeFormat('ru-RU', {
  timeZone: zone,
  hour: '2-digit',
  minute: '2-digit',
  hourCycle: 'h23',
});
const year = new Intl.DateTimeFormat('ru-RU', { timeZone: zone, year: 'numeric' });

/** A UTC instant as Moscow date and time with the label: «3 октября, 23:59 МСК»; the year only when it is not now's */
export function moscowTime(utc: string | Date, now: Date = new Date()): string {
  const at = typeof utc === 'string' ? new Date(utc) : utc;
  const date = (year.format(at) === year.format(now) ? day : dayWithYear).format(at);
  return ru.time.moscow(date, clock.format(at));
}

const dayMonth = new Intl.DateTimeFormat('ru-RU', {
  timeZone: zone,
  day: '2-digit',
  month: '2-digit',
});

/** A UTC instant as the Moscow day in short: «12.10» (SPEC «Уже прошёл Вася, 12.10») */
export function moscowDay(utc: string | Date): string {
  return dayMonth.format(typeof utc === 'string' ? new Date(utc) : utc);
}
