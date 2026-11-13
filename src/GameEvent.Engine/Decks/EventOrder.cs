using GameEvent.Engine.Kernel;
using GameEvent.Engine.Rulesets;

namespace GameEvent.Engine.Decks;

/// <summary>
/// The order accumulated events apply in (SPEC «Накопление», D-505): the kinds by <c>events.applyOrder</c> (default
/// special, good, bad), an unlisted risky after them, then what has no event kind — items — last. Ties keep the order they
/// are given in, so callers pass things in the order they were received.
/// </summary>
internal static class EventOrder
{
    private static readonly EquatableArray<EventKind> s_default = [EventKind.Special, EventKind.Good, EventKind.Bad];

    /// <summary>The order of kinds of <paramref name="rules"/>: <c>events.applyOrder</c>, or the default.</summary>
    public static EquatableArray<EventKind> Of(Ruleset rules) => rules.Events?.ApplyOrder ?? s_default;

    /// <summary>The rank of <paramref name="kind"/> in <paramref name="order"/>: lower applies first; no kind is last.</summary>
    public static int Rank(EquatableArray<EventKind> order, EventKind? kind)
    {
        if (kind is not { } k)
        {
            return order.Count + 1;
        }

        for (var i = 0; i < order.Count; i++)
        {
            if (order[i] == k)
            {
                return i;
            }
        }

        return order.Count;
    }

    /// <summary><paramref name="items"/> by the rank of their kind; equal ranks keep the given order (a stable sort). With events off — as given.</summary>
    public static IEnumerable<T> Sort<T>(Ruleset rules, IEnumerable<T> items, Func<T, EventKind?> kind)
    {
        // With events off what they left keeps the order it came in: the mechanic is off (CLAUDE.md «флаги»)
        if (!rules.Features.Events)
        {
            return items;
        }

        var order = Of(rules);
        return items.OrderBy(i => Rank(order, kind(i)));
    }
}
