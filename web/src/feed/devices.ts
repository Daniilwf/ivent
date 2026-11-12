import { ru } from '../i18n/ru';

const t = ru.sessions;

// Order matters: Edge, Opera and Yandex also say «Chrome», Chrome also says «Safari»
const browsers: [RegExp, string][] = [
  [/YaBrowser\//, t.browsers.yandex],
  [/Edg(e|A|iOS)?\//, 'Edge'],
  [/(OPR|Opera)\//, 'Opera'],
  [/SamsungBrowser\//, 'Samsung Internet'],
  [/(Firefox|FxiOS)\//, 'Firefox'],
  [/(Chrome|CriOS)\//, 'Chrome'],
  [/Safari\//, 'Safari'],
];

const systems: [RegExp, string][] = [
  [/iPhone/, 'iPhone'],
  [/iPad/, 'iPad'],
  [/Android/, 'Android'],
  [/Windows/, 'Windows'],
  [/Mac OS X|Macintosh/, 'macOS'],
  [/CrOS/, 'ChromeOS'],
  [/Linux/, 'Linux'],
];

/**
 * A device's name from the browser's own description at the sign-in (D-466): «Chrome, Windows», «Safari, iPhone»; what
 * cannot be told apart reads «Неизвестное устройство». Only a hint for the owner, never a check.
 */
export function deviceOf(userAgent: string | null | undefined): string {
  const ua = userAgent ?? '';
  const browser = browsers.find(([pattern]) => pattern.test(ua))?.[1];
  const system = systems.find(([pattern]) => pattern.test(ua))?.[1];
  if (browser && system) return t.device(browser, system);
  return browser ?? system ?? t.unknownDevice;
}
