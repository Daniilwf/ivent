using System.Globalization;
using System.Text;

namespace GameEvent.Simulator.Report;

/// <summary>The report as Markdown in Russian, for the customer: the same numbers as the JSON.</summary>
public static class MarkdownReport
{
    private static readonly CultureInfo s_ru = CultureInfo.GetCultureInfo("ru-RU");

    public static string Render(SimulationReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var md = new StringBuilder();
        var m = report.Meta;
        Line(md, "# Отчёт симулятора баланса");
        Line(md);
        Line(md, $"- Прогонов: {m.Runs}, дней в сезоне: {m.Days}, зерно: {m.Seed}, игроков: {m.Players}.");
        Line(md, $"- Конфиг: `{m.RulesetSource}`; карта: {(m.MapSource is null ? $"линейная, {report.Map.Cells - 1} шагов" : $"`{m.MapSource}` (граф)")}; пул: `{m.PoolSource}`.");
        if (m.ElapsedSeconds is { } elapsed)
        {
            Line(md, $"- Время счёта: {N(elapsed, 1)} с.");
        }

        var pool = report.Pool;
        Line(md, $"- Пул: {pool.Games} игр, {pool.Categories} категорий; часы сгенерированы у {pool.GeneratedHours} игр. Длина игр: медиана {N(pool.Hours.P50)} ч, 10–90% — {N(pool.Hours.P10)}…{N(pool.Hours.P90)} ч.");
        Line(md, $"- Карта: {report.Map.Cells} клеток, до финиша от старта {report.Map.StartToFinish?.ToString(s_ru) ?? "—"} шагов, развилок {report.Map.Forks}, телепортов {report.Map.Teleports}, чекпоинтов {report.Map.Checkpoints}, бонусов {report.Map.PointsBonuses}, зоны: {(report.Map.Zones.Count == 0 ? "нет" : string.Join(", ", report.Map.Zones))}.");
        Line(md);

        Line(md, "## Очки в час по длине игры");
        Line(md);
        Line(md, "Только засчитанные прохождения (без свободного режима первого). «Очки/ч игры» — на час реальной игры бота, «очки/ч HLTB» — на час длины игры из пула.");
        Line(md);
        Table(md, ["Длина", "Начато за сезон", "Пройдено", "Дропнуто", "Доля дропов", "Очки/ч игры", "Очки/ч HLTB", "Очков за прохождение", "Часов на прохождение"],
            report.LengthBuckets.Select(b => new[]
            {
                b.Label, N(b.StartedPerSeason), N(b.CompletedPerSeason), N(b.DroppedPerSeason), P(b.DropShare),
                N(b.PointsPerPlayHour, 2), N(b.PointsPerGameHour, 2), N(b.MeanPointsPerCompletion), N(b.MeanPlayHoursPerCompletion),
            }));

        Line(md, "## Выгода дропа");
        Line(md);
        Line(md, $"Дропающие боты бросают начатые игры длиннее порога после минимального времени игры; остальные доводят до конца. Дропов за сезон: {N(report.Drops.DropsPerSeason)}, штраф за дроп в среднем {N(report.Drops.MeanPenaltyPerDrop)} очка, до дропа сыграно в среднем {N(report.Drops.MeanHoursPerDrop)} ч.");
        Line(md);
        Table(md, ["Группа", "Очки дропающих", "Очки не дропающих", "Разница", "Место дропающих", "Место не дропающих", "Дропов (д / н)", "Пройдено (д / н)"],
            report.Drops.Rows.Select(r => new[]
            {
                r.Group, N(r.DropperMeanPoints), N(r.KeeperMeanPoints), Signed(r.PointsDifference), N(r.DropperMeanPlace), N(r.KeeperMeanPlace),
                $"{N(r.DropperMeanDrops)} / {N(r.KeeperMeanDrops)}", $"{N(r.DropperMeanCompleted)} / {N(r.KeeperMeanCompleted)}",
            }));

        var f = report.Finish;
        Line(md, "## Финиш");
        Line(md);
        Line(md, $"- Сезонов с первым финишировавшим: {P(f.SeasonsWithFirst)}; финишировавших за сезон в среднем {N(f.FinishersPerSeason)}.");
        if (f.FirstFinishDay is { } day)
        {
            Line(md, $"- День первого финиша: медиана {N(day.P50)}, 10% — {N(day.P10)}, 90% — {N(day.P90)} (среднее {N(day.Mean)}).");
        }

        if (f.FirstFrozenDay is { } frozen)
        {
            Line(md, $"- День заморозки первого (пруф одобрен): медиана {N(frozen.P50)}, 90% — {N(frozen.P90)}.");
        }

        if (f.FirstByProfile.Count > 0)
        {
            Line(md, $"- Кто первый, доля сезонов: {string.Join(", ", f.FirstByProfile.Select(x => $"{x.Key} {P(x.Value)}"))}.");
        }

        Line(md);

        Line(md, "## Профили игроков");
        Line(md);
        Table(md, ["Профиль", "Ботов", "Очки (ср.)", "Очки 10–50–90%", "Место (ср.)", "Побед", "Топ-3", "Финиш", "Первый", "Пройдено", "Дропов", "Рероллов (беспл. / платн.)", "Часов игры", "Своб. часов", "Ждал проверки, ч", "Не допройдено к дедлайну, ч", "Очки/ч"],
            report.Profiles.Select(p => new[]
            {
                p.Profile, p.BotsPerSeason.ToString(s_ru), N(p.MeanPoints), $"{N(p.Points.P10)}–{N(p.Points.P50)}–{N(p.Points.P90)}",
                N(p.MeanPlace), P(p.WinShare), P(p.Top3Share), P(p.FinishShare), P(p.FirstShare), N(p.MeanCompleted), N(p.MeanDrops),
                $"{N(p.MeanFreeRerolls)} / {N(p.MeanPaidRerolls)}", N(p.MeanPlayHours), N(p.MeanFreeHours), N(p.MeanBlockedHours), N(p.MeanUnfinishedHours), N(p.PointsPerPlayHour, 2),
            }));

        var g = report.Gap;
        Line(md, "## Разрыв активных и занятых");
        Line(md);
        Line(md, $"- `{g.Active}`: {N(g.ActiveMeanPoints)} очков, место {N(g.ActiveMeanPlace)}; `{g.Busy}`: {N(g.BusyMeanPoints)} очков, место {N(g.BusyMeanPlace)}.");
        Line(md, $"- Очки активного больше в {N(g.PointsRatio, 2)} раза. Лучший занятый выше худшего активного в {P(g.BusyBeatsActiveShare)} сезонов.");
        Line(md);

        var c = report.Counts;
        Line(md, "## Действия за сезон (среднее)");
        Line(md);
        Line(md, $"- Команд {N(c.CommandsPerSeason)}, событий {N(c.EventsPerSeason)}, промахов колеса {N(c.MissesPerSeason)}.");
        Line(md, $"- Пройдено {N(c.CompletedPerSeason)}, дропов {N(c.DropsPerSeason)}, тех-рероллов {N(c.TechRerollsPerSeason)}, «Уже проходил» {N(c.AlreadyPlayedPerSeason)}, рероллов бесплатных {N(c.FreeRerollsPerSeason)}, платных {N(c.PaidRerollsPerSeason)}.");
        Line(md, $"- Не допройдено к дедлайну: {N(c.UnfinishedAtDeadlinePerSeason)} игр, {N(c.UnfinishedHoursPerSeason)} ч игры впустую. Ожидание проверки пруфов (лимит непроверенных): {N(c.BlockedHoursPerSeason)} ч на сезон.");
        Line(md, $"- Отказы движка: {(c.RejectionsPerSeason.Count == 0 ? "нет" : string.Join(", ", c.RejectionsPerSeason.Select(x => $"`{x.Key}` {N(x.Value)}")))}. Срабатываний защиты от зацикливания: {c.GuardTrips}.");
        Line(md);

        if (report.Branches is { } branches)
        {
            Line(md, "## Развилки");
            Line(md);
            Line(md, "Итоги сезона ботов по их первому выбору на развилке.");
            Line(md);
            Table(md, ["Развилка", "Ветка", "Выборов", "Доля", "Ботов", "Очки (ср.)", "Место (ср.)", "Финиш", "День финиша"],
                branches.Options.Select(o => new[]
                {
                    o.Fork, o.Option, o.Chosen.ToString(s_ru), P(o.Share), o.FirstChoiceBots.ToString(s_ru), N(o.MeanPoints), N(o.MeanPlace),
                    P(o.FinishShare), o.MeanFinishDay is { } d ? N(d) : "—",
                }));
            Table(md, ["Политика выбора", "Ботов", "Очки (ср.)", "Место (ср.)", "Финиш", "Первый", "День финиша"],
                branches.Policies.Select(p => new[]
                {
                    p.Policy, p.Bots.ToString(s_ru), N(p.MeanPoints), N(p.MeanPlace), P(p.FinishShare), P(p.FirstShare), p.MeanFinishDay is { } d ? N(d) : "—",
                }));
        }

        if (report.Zones.Count > 1 || report.Zones.Any(z => z.Zone != "—"))
        {
            Line(md, "## Зоны");
            Line(md);
            Line(md, "Прохождения по зоне, где стоял игрок при ролле («—» — вне зон).");
            Line(md);
            Table(md, ["Зона", "Начато за сезон", "Длина игр (ср.)", "Пройдено", "Дропов", "Очки/ч игры", "Очков за прохождение", "Штраф за дроп"],
                report.Zones.Select(z => new[]
                {
                    z.Zone, N(z.StartedPerSeason), N(z.MeanGameHours), P(z.CompletedShare), P(z.DropShare), N(z.PointsPerPlayHour, 2),
                    N(z.MeanPointsPerCompletion), N(z.MeanPenaltyPerDrop),
                }));
        }

        Line(md, "## Длины игр пула");
        Line(md);
        Table(md, ["Длина", "Доля пула"], pool.ByBucket.Select(b => new[] { b.Bucket, P(b.Share) }));
        Line(md, "Конфиг правил и настройки ботов целиком — в `report.json` (`ruleset`, `settings`).");
        return md.ToString();
    }

    private static void Table(StringBuilder md, string[] header, IEnumerable<string[]> rows)
    {
        Line(md, $"| {string.Join(" | ", header)} |");
        Line(md, $"|{string.Concat(header.Select(_ => " --- |"))}");
        foreach (var row in rows)
        {
            Line(md, $"| {string.Join(" | ", row)} |");
        }

        Line(md);
    }

    private static void Line(StringBuilder md, string text = "") => md.Append(text).Append('\n');

    private static string N(double value, int digits = 1) => value.ToString($"F{digits}", s_ru);

    private static string Signed(double value) => (value > 0 ? "+" : "") + N(value);

    private static string P(double share) => (share * 100).ToString("F0", s_ru) + "%";
}
