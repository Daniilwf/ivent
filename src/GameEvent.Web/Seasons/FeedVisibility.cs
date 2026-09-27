using System.Collections.Frozen;
using System.Text.Json.Nodes;

namespace GameEvent.Web.Seasons;

/// <summary>
/// What of each season event the feed shows to whom (D-124, after the security review). Every event type has an entry —
/// a new type without one fails its test instead of becoming public by default. Public: everyone signed in sees the whole
/// event. A proof's links, note and files and the admin's comment on it are the player's and the admin's: others see that
/// a proof was sent and checked. An undo shows others which command it undid and why, not the state it put back.
/// </summary>
public static class FeedVisibility
{
    public enum Access
    {
        Public,

        /// <summary>The listed fields only for the admin and the event's own player (<c>playerId</c>).</summary>
        OwnerFields,

        /// <summary>Only <c>commandId</c> and <c>comment</c> for everyone but the admin.</summary>
        UndoSummary,

        /// <summary>The global log's (accounts, files, pool, bug reports): never in a season's feed.</summary>
        NotInFeed,
    }

    private static readonly string[] s_proofDetails = ["links", "note", "files", "comment"];

    public static readonly FrozenDictionary<string, Access> Types = new Dictionary<string, Access>
    {
        ["season-created"] = Access.Public,
        ["season-status-changed"] = Access.Public,
        ["season-deadline-set"] = Access.Public,
        ["season-result-recorded"] = Access.Public,
        ["ruleset-changed"] = Access.Public,
        ["season-player-added"] = Access.Public,
        ["player-adjusted"] = Access.Public,
        ["player-inactivity-set"] = Access.Public,
        ["offer-discarded"] = Access.Public,
        ["choice-discarded"] = Access.Public,
        ["game-rolled"] = Access.Public,
        ["game-choice-rolled"] = Access.Public,
        ["game-rerolled"] = Access.Public,
        ["game-excluded"] = Access.Public,
        ["choice-made"] = Access.Public,
        ["run-started"] = Access.Public,
        ["run-completed"] = Access.Public,
        ["completion-rolled"] = Access.Public,
        ["run-reviewed"] = Access.Public,
        ["run-dropped"] = Access.Public,
        ["run-tech-rerolled"] = Access.Public,
        ["tech-reroll-converted-to-drop"] = Access.Public,
        ["run-hours-corrected"] = Access.Public,
        ["run-difficulty-changed"] = Access.Public,
        ["proof-submitted"] = Access.OwnerFields,
        ["proof-approved"] = Access.OwnerFields,
        ["proof-rejected"] = Access.OwnerFields,
        ["points-changed"] = Access.Public,
        ["coins-changed"] = Access.Public,
        ["resource-changed"] = Access.Public,
        ["player-moved"] = Access.Public,
        ["branch-choice-requested"] = Access.Public,
        ["map-published"] = Access.Public,
        ["player-finished"] = Access.Public,
        ["player-frozen"] = Access.Public,
        ["player-finish-revoked"] = Access.Public,
        ["finish-surplus-changed"] = Access.Public,
        ["finish-bonus-rules-refreshed"] = Access.Public,
        ["manual-effect-created"] = Access.Public,
        ["manual-effect-resolved"] = Access.Public,
        ["effect-chain-cut"] = Access.Public,
        ["command-undone"] = Access.UndoSummary,

        // The economy (stage 4): every move of coins and objects between players is visible (SPEC «Переводы … всё видно в логе»)
        ["content-published"] = Access.Public,
        ["object-given"] = Access.Public,
        ["object-removed"] = Access.Public,
        ["object-transferred"] = Access.Public,
        ["object-changed"] = Access.Public,
        ["object-lost"] = Access.Public,
        ["item-used"] = Access.Public,
        ["effect-triggered"] = Access.Public,
        ["effect-rolled"] = Access.Public,
        ["hostile-intercepted"] = Access.Public,
        ["hostile-received"] = Access.Public,
        ["wheel-spun"] = Access.Public,
        ["next-roll-modified"] = Access.Public,
        ["roll-modifiers-applied"] = Access.Public,
        ["next-dice-modified"] = Access.Public,
        ["inventory-adjusted"] = Access.Public,
        ["run-dice-modified"] = Access.Public,
        ["run-dice-rerolled"] = Access.Public,
        ["shop-rolled"] = Access.Public,
        ["lot-bought"] = Access.Public,
        ["shop-offer-expired"] = Access.Public,
        ["shop-price-restarted"] = Access.Public,
        ["bet-placed"] = Access.Public,
        ["bet-settled"] = Access.Public,
        ["account-created"] = Access.NotInFeed,
        ["account-password-reset"] = Access.NotInFeed,
        ["account-password-changed"] = Access.NotInFeed,
        ["account-changed"] = Access.NotInFeed,
        ["account-deleted"] = Access.NotInFeed,
        ["account-restored"] = Access.NotInFeed,
        ["account-avatar-changed"] = Access.NotInFeed,
        ["file-stored"] = Access.NotInFeed,
        ["game-added"] = Access.NotInFeed,
        ["game-changed"] = Access.NotInFeed,
        ["game-deleted"] = Access.NotInFeed,
        ["game-restored"] = Access.NotInFeed,
        ["category-set"] = Access.NotInFeed,
        ["category-removed"] = Access.NotInFeed,
        ["bug-reported"] = Access.NotInFeed,
        ["bug-report-status-changed"] = Access.NotInFeed,
    }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>
    /// The event's data as this viewer may see it, or null when the event is not the feed's. An unknown type is not
    /// shown: the test catches it before it ships.
    /// </summary>
    public static JsonObject? Shown(string type, JsonObject data, bool admin, Guid? viewerPlayerId)
    {
        ArgumentNullException.ThrowIfNull(data);
        switch (Types.GetValueOrDefault(type, Access.NotInFeed))
        {
            case Access.Public:
                return data;

            case Access.OwnerFields:
                if (admin || (viewerPlayerId is { } me && data["playerId"]?.GetValue<Guid>() == me))
                {
                    return data;
                }

                foreach (var field in s_proofDetails)
                {
                    data.Remove(field);
                }

                return data;

            case Access.UndoSummary:
                return admin ? data : new JsonObject { ["commandId"] = data["commandId"]?.DeepClone(), ["comment"] = data["comment"]?.DeepClone() };

            default:
                return null;
        }
    }
}
