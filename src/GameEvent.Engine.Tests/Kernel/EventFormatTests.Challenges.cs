using GameEvent.Engine.Challenges;
using GameEvent.Engine.Content;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Pool;
using GameEvent.Engine.Suggestions;
using GameEvent.Engine.Tests.Support;

namespace GameEvent.Engine.Tests.Kernel;

/// <summary>The frozen formats of the weekly challenge and the suggestion box (stage 5, D-526, D-527, D-570, D-571).</summary>
public partial class EventFormatTests
{
    private static readonly Guid s_challenge = SequentialIds.Make(0x70000000, 1);

    private static WeeklyChallengeDefinition OldSchool() =>
        new()
        {
            Id = "old-school",
            Name = "Олдскул",
            Description = "Пройди игру, вышедшую до 2005 года.",
            Condition = new ConditionSpec { Game = new GameFilterSpec { ReleaseYearBefore = 2005 } },
            Reward = new RewardSpec { Coins = 10 },
            Check = ChallengeCheck.Auto,
        };

    private static TheoryData<string, IGameEvent, int, string> WithChallenges(TheoryData<string, IGameEvent, int, string> data)
    {
        foreach (var (type, sample, json) in ChallengeSamples())
        {
            data.Add(type, sample, 1, json);
        }

        return data;
    }

    private static IEnumerable<(string Type, IGameEvent Sample, string Json)> ChallengeSamples() =>
    [
        ("weekly-challenge-set", new WeeklyChallengeSet(s_challenge, OldSchool(), s_at, s_at.AddDays(7), 10), """{"challengeId":"70000000-0000-0000-0000-000000000001","definition":{"id":"old-school","name":"Олдскул","description":"Пройди игру, вышедшую до 2005 года.","condition":{"difficultyAtLeast":null,"game":{"tags":null,"maxHours":null,"minHours":null,"releaseYearBefore":2005},"stat":null,"tag":null,"gte":null,"minDice":null},"reward":{"coins":10},"check":"auto"},"startsAt":"2026-10-01T12:30:00+00:00","endsAt":"2026-10-08T12:30:00+00:00","rewardCoins":10}"""),
        ("weekly-challenge-completed", new WeeklyChallengeCompleted(s_challenge, s_player, s_run, 10), """{"challengeId":"70000000-0000-0000-0000-000000000001","playerId":"10000000-0000-0000-0000-000000000001","runId":"00000000-0000-0000-0000-000000000001","coins":10}"""),
        ("weekly-challenge-revoked", new WeeklyChallengeRevoked(s_challenge, s_player, s_run, 0), """{"challengeId":"70000000-0000-0000-0000-000000000001","playerId":"10000000-0000-0000-0000-000000000001","runId":"00000000-0000-0000-0000-000000000001","coins":0}"""),
        (
            "suggestion-submitted",
            new SuggestionSubmitted(s_challenge, s_player, s_user, SuggestionKind.Game, "Короткий хоррор", new GameCard("Signalis", ["Horror"], 9, 2022, null, null, null, false), s_at),
            """{"suggestionId":"70000000-0000-0000-0000-000000000001","playerId":"10000000-0000-0000-0000-000000000001","userId":"40000000-0000-0000-0000-000000000001","kind":"game","text":"Короткий хоррор","game":{"title":"Signalis","tags":["Horror"],"hours":9,"year":2022,"steamAppId":null,"coverFileId":null,"note":null,"isCoop":false,"completionCondition":null},"submittedAt":"2026-10-01T12:30:00+00:00"}"""
        ),
        ("suggestion-reviewed", new SuggestionReviewed(s_challenge, SuggestionStatus.Rejected, "Уже есть", s_at), """{"suggestionId":"70000000-0000-0000-0000-000000000001","status":"rejected","comment":"Уже есть","reviewedAt":"2026-10-01T12:30:00+00:00"}"""),
    ];

    [Fact]
    public void An_undo_of_a_challenge_or_a_suggestion_writes_them_and_an_undo_of_anything_else_keeps_its_format()
    {
        var suggestion = new Suggestion(s_challenge, s_player, s_user, SuggestionKind.Event, "Ивент «Тишина»", null, s_at, SuggestionStatus.Pending, null);
        var challenge = new WeeklyChallengeState(s_challenge, OldSchool(), s_at, s_at.AddDays(7), 10, [new WeeklyChallengeCompletion(s_player, s_run, 10)]);
        var undo = new Engine.Undo.CommandUndone(s_run, "Ошибка", [], [], [], [], [], [], null, [challenge], [s_game], [suggestion], [s_game]);

        var stored = EventCodec.Encode(undo);

        Assert.Contains(
            ""","season":null,"weeklyChallenges":[{"challengeId":"70000000-0000-0000-0000-000000000001","definition":{"id":"old-school",""",
            stored.Data,
            StringComparison.Ordinal);
        Assert.EndsWith(
            ""","rewardCoins":10,"completions":[{"playerId":"10000000-0000-0000-0000-000000000001","runId":"00000000-0000-0000-0000-000000000001","coins":10}]}],"removedWeeklyChallenges":["20000000-0000-0000-0000-000000000001"],"suggestions":[{"suggestionId":"70000000-0000-0000-0000-000000000001","playerId":"10000000-0000-0000-0000-000000000001","userId":"40000000-0000-0000-0000-000000000001","kind":"event","text":"Ивент «Тишина»","game":null,"submittedAt":"2026-10-01T12:30:00+00:00","status":"pending","comment":null}],"removedSuggestions":["20000000-0000-0000-0000-000000000001"]}""",
            stored.Data,
            StringComparison.Ordinal);
        Assert.Equal(undo, EventCodec.Decode(stored));
        Assert.DoesNotContain("weeklyChallenges", EventCodec.Encode(FilledUndo()).Data, StringComparison.Ordinal);
    }
}
