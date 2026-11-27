import { ChartNoAxesColumn } from 'lucide-react';
import { useCallback, useId, useState } from 'react';
import { api, type Schemas } from '../api/client';
import { moscowTime } from '../app/time';
import { answerOf, useLoaded } from '../app/useLoaded';
import { ru } from '../i18n/ru';
import { AsyncState } from '../ui/AsyncState';
import { Select } from '../ui/Field';
import { EmptyState } from '../ui/States';

const t = ru.admin.statistics;
type Player = Schemas['AdminSeasonStatisticsPlayerView'];
type SortOrder = keyof typeof t.sort;

const column = 'min-w-0 text-left desk:px-4 desk:py-3';
const attack =
  'flex flex-wrap items-baseline justify-between gap-x-2 border-b border-muted pb-1 desk:table-cell desk:border-0';
const rate = new Intl.NumberFormat('ru-RU', { maximumFractionDigits: 2 });
const scientificRate = new Intl.NumberFormat('ru-RU', {
  notation: 'scientific',
  maximumFractionDigits: 2,
});

function formatRate(value: number) {
  if (value > 0 && value < 0.01) return t.belowPrecision;
  return Math.abs(value) >= 1e12 ? scientificRate.format(value) : rate.format(value);
}

function actionTime(player: Player) {
  return player.lastActionAt ? Date.parse(player.lastActionAt) : Number.NEGATIVE_INFINITY;
}

function ordered(players: readonly Player[], order: SortOrder) {
  if (order === 'name') return players;
  if (order === 'lastActionAt') return players.toSorted((a, b) => actionTime(b) - actionTime(a));
  if (order === 'pointsPerHour')
    return players.toSorted((a, b) => {
      if (a.pointsPerHour == null) return b.pointsPerHour == null ? 0 : 1;
      if (b.pointsPerHour == null) return -1;
      return b.pointsPerHour - a.pointsPerHour;
    });
  return players.toSorted((a, b) => b[order] - a[order]);
}

/** Effective attacks, credited completion efficiency and each participant's last own action in the selected season. */
export function StatisticsSection({ seasonId, version }: { seasonId: string; version: number }) {
  const load = useCallback(
    async () =>
      answerOf(
        await api.GET('/api/admin/seasons/{seasonId}/statistics', {
          params: { path: { seasonId } },
        }),
      ),
    [seasonId],
  );
  const loaded = useLoaded(load, { version });
  const [order, setOrder] = useState<SortOrder>('name');
  const rateExplanation = useId();

  return (
    <AsyncState loaded={loaded} errorTitle={ru.admin.loadErrorTitle}>
      {({ players }) =>
        players.length === 0 ? (
          <EmptyState
            level={2}
            icon={<ChartNoAxesColumn size={28} aria-hidden />}
            title={t.emptyTitle}
            text={t.emptyText}
          />
        ) : (
          <div className="grid min-w-0 gap-4">
            <p id={rateExplanation} className="max-w-prose text-sm text-ink-soft">
              {t.rateFormula}
            </p>
            <Select
              label={t.order}
              value={order}
              onChange={(event) => {
                const value = event.currentTarget.value;
                if (Object.hasOwn(t.sort, value)) setOrder(value as SortOrder);
              }}
              className="grid-cols-[auto_minmax(0,1fr)] items-center gap-x-4 desk:w-fit"
            >
              {Object.entries(t.sort).map(([value, label]) => (
                <option key={value} value={value}>
                  {label}
                </option>
              ))}
            </Select>
            {/* Keep the table roles when the phone lays out its rows as cards. */}
            <table
              role="table"
              aria-label={ru.admin.sections.statistics}
              aria-describedby={rateExplanation}
              className="block w-full border-collapse desk:table desk:bg-card"
            >
              <thead role="rowgroup" className="sr-only desk:not-sr-only desk:table-header-group">
                <tr role="row" className="bg-muted">
                  <th
                    role="columnheader"
                    scope="col"
                    aria-sort={order === 'name' ? 'ascending' : undefined}
                    className={column}
                  >
                    {t.player}
                  </th>
                  <th
                    role="columnheader"
                    scope="col"
                    aria-sort={order === 'attacksMade' ? 'descending' : undefined}
                    className={column}
                  >
                    {t.attacksMade}
                  </th>
                  <th
                    role="columnheader"
                    scope="col"
                    aria-sort={order === 'attacksReceived' ? 'descending' : undefined}
                    className={column}
                  >
                    {t.attacksReceived}
                  </th>
                  <th
                    role="columnheader"
                    scope="col"
                    aria-sort={order === 'pointsPerHour' ? 'descending' : undefined}
                    className={column}
                  >
                    {t.pointsPerHour}
                  </th>
                  <th
                    role="columnheader"
                    scope="col"
                    aria-sort={order === 'lastActionAt' ? 'descending' : undefined}
                    className={column}
                  >
                    {t.lastAction}
                  </th>
                </tr>
              </thead>
              <tbody role="rowgroup" className="grid gap-2 desk:table-row-group">
                {ordered(players, order).map((player) => (
                  <tr
                    key={player.playerId}
                    role="row"
                    className="grid grid-cols-2 gap-x-3 gap-y-2 rounded-lg bg-card px-4 py-3 desk:table-row desk:border-b desk:border-muted"
                  >
                    <th
                      role="rowheader"
                      scope="row"
                      className={`col-span-2 font-bold wrap-anywhere ${column}`}
                    >
                      {player.name}
                    </th>
                    <td role="cell" className={`${column} ${attack}`}>
                      <span aria-hidden="true" className="text-sm text-ink-soft desk:hidden">
                        {t.attacksMade}
                      </span>
                      <span className="font-bold tabular-nums">{player.attacksMade}</span>
                    </td>
                    <td role="cell" className={`${column} ${attack}`}>
                      <span aria-hidden="true" className="text-sm text-ink-soft desk:hidden">
                        {t.attacksReceived}
                      </span>
                      <span className="font-bold tabular-nums">{player.attacksReceived}</span>
                    </td>
                    <td
                      role="cell"
                      className={`col-span-2 border-b border-muted pb-1 desk:table-cell desk:border-0 ${column}`}
                    >
                      <div className="grid grid-cols-[auto_minmax(0,1fr)] items-baseline gap-x-2 desk:block">
                        <span aria-hidden="true" className="text-sm text-ink-soft desk:hidden">
                          {t.pointsPerHour}
                        </span>
                        <div className="min-w-0 text-right desk:text-left">
                          <span className="font-bold tabular-nums">
                            {player.pointsPerHour == null
                              ? t.noRate
                              : formatRate(player.pointsPerHour)}
                          </span>
                          <p className="text-sm text-ink-soft tabular-nums wrap-anywhere">
                            {player.pointsPerHour == null
                              ? t.noHours
                              : t.sample(player.completedRunPoints, player.completedRunHours)}
                          </p>
                        </div>
                      </div>
                    </td>
                    <td
                      role="cell"
                      className={`col-span-2 flex flex-wrap items-baseline gap-x-2 text-sm text-ink-soft desk:table-cell ${column}`}
                    >
                      <span aria-hidden="true" className="desk:hidden">
                        {t.lastAction}
                      </span>
                      {player.lastActionAt ? (
                        <time dateTime={player.lastActionAt}>
                          {moscowTime(player.lastActionAt)}
                        </time>
                      ) : (
                        t.noAction
                      )}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )
      }
    </AsyncState>
  );
}
