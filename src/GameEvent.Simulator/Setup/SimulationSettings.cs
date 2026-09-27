using System.Text.Json;
using System.Text.Json.Serialization;

namespace GameEvent.Simulator.Setup;

/// <summary>
/// How the bots and the admin bot behave (D-352). Not game rules: those come from the ruleset. Every field has a default;
/// a settings file (<c>--settings</c>) overrides the fields it names. Probabilities are 0…1, hours are hours.
/// </summary>
public sealed record SimulationSettings
{
    /// <summary>The players: how many bots of each profile, in this order.</summary>
    public IReadOnlyList<PlayerGroup> Players { get; init; } =
        [new("active", 4), new("average", 8), new("busy", 4)];

    /// <summary>Free time per profile.</summary>
    public IReadOnlyDictionary<string, BotProfile> Profiles { get; init; } = new Dictionary<string, BotProfile>
    {
        ["active"] = new() { HoursPerDay = 4.0, DayNoise = 0.35, SkipDayChance = 0.05 },
        ["average"] = new() { HoursPerDay = 2.0, DayNoise = 0.45, SkipDayChance = 0.12 },
        ["busy"] = new() { HoursPerDay = 0.8, DayNoise = 0.6, SkipDayChance = 0.3 },
    };

    public BotBehaviour Behaviour { get; init; } = new();

    public AdminBehaviour Admin { get; init; } = new();

    /// <summary>Lengths for games of the pool without hours.</summary>
    public MissingHours MissingHours { get; init; } = new();

    /// <summary>Upper bounds of the game-length buckets of the report, in hours; the last bucket is open.</summary>
    public IReadOnlyList<double> LengthBuckets { get; init; } = [2, 4, 7, 10, 15, 20, 30];

    /// <summary>When the season starts (UTC). A Monday: weekends are days 5–6 of each week.</summary>
    public DateTimeOffset Start { get; init; } = new(2026, 10, 5, 0, 0, 0, TimeSpan.Zero);

    public static JsonSerializerOptions JsonOptions { get; } = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver(),
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            WriteIndented = true,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        options.MakeReadOnly();
        return options;
    }

    public static SimulationSettings Parse(string json) =>
        JsonSerializer.Deserialize<SimulationSettings>(json, JsonOptions) ?? throw new JsonException("Settings JSON is null.");

    /// <summary>Problems of the settings, empty when they are usable.</summary>
    public IReadOnlyList<string> Problems()
    {
        var problems = new List<string>();
        if (Players.Count == 0 || Players.Sum(g => g.Count) is < 1 or > 64)
        {
            problems.Add("players: 1…64 bots in all");
        }

        foreach (var group in Players)
        {
            if (group.Count < 0)
            {
                problems.Add($"players.{group.Profile}: the count is negative");
            }

            if (!Profiles.ContainsKey(group.Profile))
            {
                problems.Add($"players.{group.Profile}: no such profile");
            }
        }

        foreach (var (name, profile) in Profiles)
        {
            if (profile.HoursPerDay is < 0 or > 16 || profile.DayNoise is < 0 or > 3 || profile.WeekendFactor is < 0 or > 4)
            {
                problems.Add($"profiles.{name}: hoursPerDay 0…16, dayNoise 0…3, weekendFactor 0…4");
            }

            if (profile.MaxSessionHours is <= 0 or > 24)
            {
                problems.Add($"profiles.{name}.maxSessionHours: above 0, at most 24");
            }

            if (profile.SessionStartHour is < 0 or > 23)
            {
                problems.Add($"profiles.{name}.sessionStartHour: 0…23");
            }

            CheckChance(problems, $"profiles.{name}.skipDayChance", profile.SkipDayChance);
        }

        var b = Behaviour;
        CheckChance(problems, "behaviour.alreadyPlayedChance", b.AlreadyPlayedChance);
        CheckChance(problems, "behaviour.techRerollChance", b.TechRerollChance);
        CheckChance(problems, "behaviour.dropperShare", b.DropperShare);
        CheckChance(problems, "behaviour.randomDropChance", b.RandomDropChance);
        CheckChance(problems, "admin.skipCheckChance", Admin.SkipCheckChance);
        CheckChance(problems, "admin.rejectChance", Admin.RejectChance);
        if (b.PlayTimeNoise is < 0 or > 3)
        {
            problems.Add("behaviour.playTimeNoise: 0…3");
        }

        string[] difficulties = ["easy", "normal", "hard", "extreme"];
        if (b.Difficulty.Keys.Any(k => !difficulties.Contains(k, StringComparer.Ordinal))
            || b.Difficulty.Values.Any(w => w < 0) || b.Difficulty.Values.Sum() <= 0)
        {
            problems.Add("behaviour.difficulty: weights of easy, normal, hard, extreme — not negative, at least one positive");
        }

        if (b.RandomDropFrom < 0 || b.RandomDropTo > 1 || b.RandomDropFrom > b.RandomDropTo || b.TechRerollAfterHours < 0)
        {
            problems.Add("behaviour: 0 ≤ randomDropFrom ≤ randomDropTo ≤ 1, techRerollAfterHours not negative");
        }

        if (b.BranchPolicies.Count == 0)
        {
            problems.Add("behaviour.branchPolicies: at least one policy");
        }

        if (Admin.CheckHours.Count == 0 || Admin.CheckHours.Any(h => h is < 0 or >= 24) || Admin.MinDelayHours < 0 || Admin.FinalReviewHours < 0)
        {
            problems.Add("admin: checkHours 0…23 (at least one), minDelayHours and finalReviewHours not negative");
        }

        if (MissingHours.Median <= 0 || MissingHours.Min <= 0 || MissingHours.Max < MissingHours.Min || MissingHours.Sigma < 0)
        {
            problems.Add("missingHours: median and min positive, max ≥ min, sigma not negative");
        }

        if (LengthBuckets.Count == 0 || LengthBuckets.Zip(LengthBuckets.Skip(1)).Any(p => p.First >= p.Second) || LengthBuckets[0] <= 0)
        {
            problems.Add("lengthBuckets: positive and increasing");
        }

        return problems;
    }

    private static void CheckChance(List<string> problems, string field, double value)
    {
        if (value is < 0 or > 1)
        {
            problems.Add($"{field}: 0…1");
        }
    }
}

/// <summary>A number of bots of one profile.</summary>
public sealed record PlayerGroup(string Profile, int Count);

/// <summary>
/// A bot's free time for games. A day's hours are <see cref="HoursPerDay"/> × a log-normal noise with mean 1
/// (<see cref="DayNoise"/> is its σ) × <see cref="WeekendFactor"/> on Saturday and Sunday; with
/// <see cref="SkipDayChance"/> the bot does not play that day. The session starts around <see cref="SessionStartHour"/>.
/// </summary>
public sealed record BotProfile
{
    public double HoursPerDay { get; init; } = 2;

    public double DayNoise { get; init; } = 0.4;

    public double SkipDayChance { get; init; }

    public double WeekendFactor { get; init; } = 1.5;

    public int SessionStartHour { get; init; } = 19;

    /// <summary>A day's session is never longer than this.</summary>
    public double MaxSessionHours { get; init; } = 14;
}

/// <summary>How the bot picks a branch at a fork.</summary>
public enum BranchPolicy
{
    /// <summary>The branch with the fewest cells to the finish (along the arrows), the default on a tie.</summary>
    Shortest,

    /// <summary>The default branch.</summary>
    Default,

    /// <summary>Any branch, evenly.</summary>
    Random,
}

/// <summary>What the bots do at each step of the turn (D-352).</summary>
public sealed record BotBehaviour
{
    /// <summary>σ of the log-normal factor between a game's hours and the bot's real play time (median — the hours).</summary>
    public double PlayTimeNoise { get; init; } = 0.3;

    /// <summary>A rolled game is one the bot played before the event: «Уже проходил».</summary>
    public double AlreadyPlayedChance { get; init; } = 0.03;

    /// <summary>A started game does not run: the bot tech-rerolls it after <see cref="TechRerollAfterHours"/> of play.</summary>
    public double TechRerollChance { get; init; } = 0.02;

    public double TechRerollAfterHours { get; init; } = 0.5;

    /// <summary>A free reroll is used when the offered game is longer than this; null — never.</summary>
    public double? RerollAboveHours { get; init; } = 15;

    /// <summary>
    /// A free reroll is also used when the offered game is longer than the bot's free time left before the deadline: what
    /// the length limit of the last days (<c>roll.lastDaysLengthFilter</c>, not in the engine yet) would do (D-357).
    /// </summary>
    public bool DeadlineAware { get; init; }

    /// <summary>A paid reroll (coins) is used when the offered game is longer than this; null — never.</summary>
    public double? PaidRerollAboveHours { get; init; }

    /// <summary>At most this many rerolls of one roll.</summary>
    public int MaxRerollsPerRoll { get; init; } = 3;

    /// <summary>The share of the bots of each profile that drop long games (the rest keep them).</summary>
    public double DropperShare { get; init; } = 0.5;

    /// <summary>A dropper drops a started game longer than this after <c>roll.minPlayMinutesBeforeDrop</c>.</summary>
    public double DropAboveHours { get; init; } = 15;

    /// <summary>Any bot gives up a game midway (at 20–80% of it): the game turned out not to be for them.</summary>
    public double RandomDropChance { get; init; } = 0.03;

    /// <summary>Where in the game a random drop comes: a share of its play time, evenly between these two.</summary>
    public double RandomDropFrom { get; init; } = 0.2;

    public double RandomDropTo { get; init; } = 0.8;

    /// <summary>Weights of the difficulty the bot plays on.</summary>
    public IReadOnlyDictionary<string, double> Difficulty { get; init; } = new Dictionary<string, double>
    {
        ["easy"] = 0.15,
        ["normal"] = 0.6,
        ["hard"] = 0.2,
        ["extreme"] = 0.05,
    };

    /// <summary>Branch policies, given to the bots of each profile in turn.</summary>
    public IReadOnlyList<BranchPolicy> BranchPolicies { get; init; } = [BranchPolicy.Shortest, BranchPolicy.Default, BranchPolicy.Random];
}

/// <summary>
/// The admin bot: checks proofs at <see cref="CheckHours"/> (UTC) every day, approving those sent at least
/// <see cref="MinDelayHours"/> earlier; skips a check with <see cref="SkipCheckChance"/>; rejects with
/// <see cref="RejectChance"/>. After the deadline it approves everything left within <see cref="FinalReviewHours"/> and
/// finishes the season.
/// </summary>
public sealed record AdminBehaviour
{
    public IReadOnlyList<int> CheckHours { get; init; } = [10, 21];

    public double MinDelayHours { get; init; } = 1;

    public double SkipCheckChance { get; init; } = 0.1;

    public double RejectChance { get; init; }

    public double FinalReviewHours { get; init; } = 24;
}

/// <summary>
/// Lengths for games of the pool without hours (D-353): log-normal with the given median and σ, clamped to
/// <see cref="Min"/>…<see cref="Max"/> and rounded to half an hour. Defaults are fitted to the games of
/// content/pool.dev.json with hours (median 10.6 h, σ 0.64). <see cref="Seed"/> is fixed: every run of a simulation
/// plays the same pool.
/// </summary>
public sealed record MissingHours
{
    public double Median { get; init; } = 10.6;

    public double Sigma { get; init; } = 0.64;

    public double Min { get; init; } = 1;

    public double Max { get; init; } = 80;

    public ulong Seed { get; init; } = 20261001;
}
