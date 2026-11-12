using GameEvent.Engine.Content;
using GameEvent.Engine.Inventory;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Seasons;
using GameEvent.Engine.Tests.Support;

namespace GameEvent.Engine.Tests.Economy.ImportedItems;

/// <summary>
/// EC21 for the imported pack (D-440): every imported item's plan agrees with what a use does on sample states — an item
/// the screen offers is accepted with the answers the plan lists, for every target it offers; one it refuses is refused by
/// the engine too, with the same code when the code is the user's own (window, condition, nothing to give).
/// </summary>
public class ImportedPlansTests
{
    // What the engine says only on the use, by design (D-440): it depends on the target or on chance
    private static readonly HashSet<string> s_saidOnUse =
    [
        RejectionCodes.ItemNotStackable,
        RejectionCodes.ItemHostileCap,
        RejectionCodes.BranchChoicePending,
    ];

    public static TheoryData<string, string> Cases()
    {
        var pack = EconomyScenario.RepositoryPack().Objects.ToDictionary(o => o.Id);
        var data = new TheoryData<string, string>();
        foreach (var id in ImportedItemsCatalog.ImportedIds().Where(id => pack[id].Kind == ObjectKind.Item).Order(StringComparer.Ordinal))
        {
            foreach (var sample in new[] { "idle", "broke", "playing", "finishers" })
            {
                data.Add(id, sample);
            }
        }

        return data;
    }

    // Вася uses the item; everyone holds something to take, give or destroy
    private static (Scenario Season, Guid Item) Sample(string objectId, string sample)
    {
        var s = sample == "finishers" ? ImportedItemsSeason.WithFinishers() : ImportedItemsSeason.New();
        if (sample == "broke")
        {
            s.WithCoins("Вася", 0);
        }

        if (sample == "playing")
        {
            s.RollTitle("Вася", "Silent Hill").Start("Вася");
        }

        var item = s.Give("Вася", objectId);
        s.Give("Вася", "orange");
        s.Give("Вася", "curse-effect");
        foreach (var other in new[] { "Петя", "Коля" })
        {
            s.Give(other, "banana");
            s.Give(other, "curse-effect");
        }

        return (s, item);
    }

    // The answers the plan lists, the first of each; a pick asks as many objects as it says (all for a list of preference)
    private static List<string> Answers(ItemPlan plan, Guid user, Guid? target)
    {
        var answers = new List<string>();
        foreach (var step in plan.Steps)
        {
            if (step.Kind != ItemStepKind.PickObject)
            {
                answers.Add(step.Options[0]);
                continue;
            }

            var objects = step.ObjectsByTarget!.TryGetValue(target ?? user, out var held) ? held
                : step.ObjectsByTarget.TryGetValue(user, out var own) ? own : [];
            answers.AddRange(step.Ranked ? objects : objects.Take(step.Count));
        }

        return answers;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void The_plan_of_an_imported_item_agrees_with_its_use(string objectId, string sample)
    {
        var (s, item) = Sample(objectId, sample);
        var user = s.PlayerId("Вася");
        var plan = ItemPlans.Of(s.State, s.Context().Pool, s.Clock.UtcNow, user).Single(p => p.Item.InstanceId == item);

        if (plan.Usable)
        {
            foreach (var target in plan.ChoosesTarget ? plan.Targets.Select(t => (Guid?)t) : [null])
            {
                var use = SeasonEngine.Execute(s.State, new UseItem(user, item, target, [.. Answers(plan, user, target)]), s.Context());
                Assert.True(
                    use.IsAccepted || s_saidOnUse.Contains(use.Rejection!.Code),
                    $"{objectId} ({sample}) on {(target is { } t ? s.State.Players[t].Name : "—")}: offered, refused {use.Rejection?.Code}: {use.Rejection?.Detail}");
            }

            return;
        }

        var someone = plan.ChoosesTarget ? s.State.Players.Keys.FirstOrDefault(id => id != user && !plan.Targets.Contains(id)) : (Guid?)null;
        var refused = SeasonEngine.Execute(s.State, new UseItem(user, item, someone == Guid.Empty ? null : someone, [.. Answers(plan, user, someone)]), s.Context());
        Assert.False(refused.IsAccepted, $"{objectId} ({sample}): the plan refuses {plan.Refusal}, the use is accepted.");
        if (plan.Refusal is RejectionCodes.ItemWrongWindow or RejectionCodes.ItemConditionNotMet or RejectionCodes.ItemNothingToGive)
        {
            Assert.Equal(plan.Refusal, refused.Rejection!.Code);
        }
    }

    [Fact]
    public void The_samples_reach_every_kind_of_answer_and_usable_and_refused_plans()
    {
        var pack = EconomyScenario.RepositoryPack().Objects.ToDictionary(o => o.Id);
        var plans = ImportedItemsCatalog.ImportedIds().Where(id => pack[id].Kind == ObjectKind.Item)
            .SelectMany(id => new[] { "idle", "broke", "playing" }.Select(sample => Sample(id, sample)))
            .Select(x => ItemPlans.Of(x.Season.State, x.Season.Context().Pool, x.Season.Clock.UtcNow, x.Season.PlayerId("Вася")).Single(p => p.Item.InstanceId == x.Item))
            .ToList();

        Assert.Contains(plans, p => p.Usable);
        Assert.Contains(plans, p => p.Refusal == RejectionCodes.ItemWrongWindow);
        Assert.Contains(plans, p => p.Refusal == RejectionCodes.ItemConditionNotMet);
        Assert.Contains(plans, p => p.Steps.Any(st => st.Kind == ItemStepKind.Direction));
        Assert.Contains(plans, p => p.Steps.Any(st => st is { Kind: ItemStepKind.PickObject, Ranked: true }));
        Assert.Contains(plans, p => p.Steps.Any(st => st.Kind == ItemStepKind.Choice && st.Prompt == "Что берёшь?"));
    }
}
