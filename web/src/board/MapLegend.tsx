import { Flag, MapPin, Orbit } from 'lucide-react';
import type { ReactNode } from 'react';
import { ru } from '../i18n/ru';
import { cx } from '../ui/cx';
import { Panel } from '../ui/Surface';
import { zoneFill, zoneRules, zoneTheme, type MapZone } from './graphBoard';

const t = ru.map;

function Symbol({ children, text }: { children: ReactNode; text: string }) {
  return (
    <li className="grid grid-cols-[auto_minmax(0,1fr)] items-start gap-3">
      <span
        aria-hidden
        className="grid size-8 place-items-center rounded-full border-2 border-ink bg-card"
      >
        {children}
      </span>
      <span className="text-sm">{text}</span>
    </li>
  );
}

/**
 * The graph map explained (2.10): its zones with their rules, mine first, and what the special cells do. Only the
 * symbols the map has are listed.
 */
export function MapLegend({
  zones,
  kinds,
  myZone,
  className,
}: {
  zones: MapZone[];
  /** The cell types on the map */
  kinds: ReadonlySet<string>;
  /** The zone my token stands in */
  myZone?: string | null | undefined;
  className?: string;
}) {
  const ordered = zones
    .map((zone, index) => ({ zone, index }))
    .sort((a, b) => Number(b.zone.id === myZone) - Number(a.zone.id === myZone));
  const symbols = [
    kinds.has('fork') ? (
      <Symbol key="fork" text={t.legend.fork}>
        <span className="size-4 rotate-45 rounded-sm border-2 border-ink" />
      </Symbol>
    ) : null,
    kinds.has('teleport') ? (
      <Symbol key="teleport" text={t.legend.teleport}>
        <Orbit size={16} />
      </Symbol>
    ) : null,
    kinds.has('checkpoint') ? (
      <Symbol key="checkpoint" text={t.legend.checkpoint}>
        <Flag size={16} />
      </Symbol>
    ) : null,
    kinds.has('pointsBonus') ? (
      <Symbol key="bonus" text={t.legend.bonus}>
        <span className="font-display text-xs font-heavy">+1</span>
      </Symbol>
    ) : null,
  ].filter(Boolean);

  return (
    <div className={cx('grid gap-4', className)} data-testid="map-legend">
      {zones.length > 0 ? (
        <Panel title={t.zones.title}>
          <p className="text-sm text-ink-soft">{t.zones.lead}</p>
          <ul className="grid gap-3">
            {ordered.map(({ zone, index }) => (
              <li
                key={zone.id}
                data-testid={`zone-${zone.id}`}
                className="grid grid-cols-[auto_minmax(0,1fr)] items-start gap-3"
              >
                <span
                  aria-hidden
                  className="mt-1 size-6 rounded-full border-2 border-ink"
                  style={{ backgroundColor: zoneFill[zoneTheme(index)] }}
                />
                <span className="grid gap-1">
                  <span className="flex flex-wrap items-center gap-2">
                    <strong className="font-display wrap-anywhere">{zone.name}</strong>
                    {zone.id === myZone ? (
                      <span className="inline-flex items-center gap-1 rounded-full bg-me px-2 text-sm font-bold text-on-color">
                        <MapPin size={14} aria-hidden />
                        {t.zones.here}
                      </span>
                    ) : null}
                  </span>
                  {zoneRules(zone).map((rule) => (
                    <span key={rule} className="text-sm text-ink-soft">
                      {rule}
                    </span>
                  ))}
                </span>
              </li>
            ))}
          </ul>
        </Panel>
      ) : null}
      {symbols.length > 0 ? (
        <details className="rounded-lg bg-card p-4" data-testid="map-symbols">
          <summary className="min-h-11 cursor-pointer content-center rounded-md font-display font-heavy is-focus:focus-ring">
            {t.legend.title}
          </summary>
          <ul className="mt-3 grid gap-3">{symbols}</ul>
        </details>
      ) : null}
    </div>
  );
}
