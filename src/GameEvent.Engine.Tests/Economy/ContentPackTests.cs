using GameEvent.Engine.Content;
using GameEvent.Engine.Inventory;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Runs;
using GameEvent.Engine.Seasons;
using GameEvent.Engine.Tests.Support;
using GameEvent.Engine.Undo;

namespace GameEvent.Engine.Tests.Economy;

/// <summary>
/// EC1: content in the season (D-400, D-402) — the files of /content and the examples of CONTENT.md load and pass the pack
/// check, a pack is checked whole with every problem at once, it is published into the log like a map, and a definition
/// left out of a later pack is soft-deleted (invariant 11).
/// </summary>
public class ContentPackTests
{
    private static Scenario Season() =>
        EconomyScenario.New().WithCategory("Horror").WithGame("Silent Hill", 6, "Horror").WithPlayers("Вася", "Петя");

    [Fact]
    public void Every_content_file_loads_and_the_pack_passes_the_check()
    {
        var pack = EconomyScenario.RepositoryPack();

        Assert.Empty(ContentPublishing.Check(pack));
        Assert.InRange(pack.Objects.Count(o => o.Kind == ObjectKind.Item), 20, 30);
        Assert.Contains(pack.Wheels, w => w.Id == "lootbox");
    }

    [Fact]
    public void Content_files_keep_the_ten_examples_of_the_doc_as_written()
    {
        var files = EconomyScenario.RepositoryPack().Objects.ToDictionary(o => o.Id);

        foreach (var example in EconomyScenario.ExamplePack().Objects)
        {
            Assert.Equal(example, files[example.Id]);
        }
    }

    [Fact]
    public void Every_item_and_effect_example_of_the_doc_passes_the_pack_check()
    {
        Assert.Empty(ContentPublishing.Check(EconomyScenario.ExamplePack()));
    }

    [Fact]
    public void A_file_that_does_not_parse_is_named()
    {
        var e = Assert.Throws<System.Text.Json.JsonException>(
            () => ContentFiles.Pack([("broken.json", """{ "id": "x", "kind": "item", "nme": "?" }""")], []));

        Assert.StartsWith("broken.json:", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Content_files_are_taken_in_the_order_of_their_names()
    {
        var examples = EconomyScenario.Examples();
        var pack = ContentFiles.Pack(
            [("b.json", ContentJson.Write(examples["orange"])), ("a.json", ContentJson.Write(examples["shield"]))], []);

        Assert.Equal(["shield", "orange"], pack.Objects.Select(o => o.Id));
    }

    [Fact]
    public void A_pack_reports_every_problem_at_once_with_its_path()
    {
        var examples = EconomyScenario.Examples();
        var pack = new ContentPack
        {
            Objects =
            [
                examples["orange"],
                examples["orange"],
                examples["shield"], // gives shield-effect, which is not in the pack
                examples["karaoke"], // an event: stage 5
                examples["not-even-sweating"], // an event with a run condition
                examples["forced-genre"] with { Effect = examples["forced-genre"].Effect! with { Trigger = Trigger.AfterRoll } },
                examples["curse-effect"] with { Effect = examples["curse-effect"].Effect! with { Trigger = Trigger.Time } },
                examples["dirty-trick"] with { Id = "auto-trick", Kind = ObjectKind.Effect, Window = null, Price = null, Effect = examples["dirty-trick"].Effect! with { Trigger = Trigger.AfterRoll, Target = new TargetSpec { Selector = TargetSelector.Self } } },
                examples["orange"] with { Id = "box", Effect = new EffectSpec { Target = new TargetSpec { Selector = TargetSelector.Self }, Actions = [new SpinWheelAction { Wheel = "nowhere" }] } },
            ],
            Wheels = [new WheelDefinition { Id = "w", Name = "Колесо", Entries = [new WheelEntrySpec { ObjectId = "ghost", Weight = 1 }] }],
        };

        var paths = ContentPublishing.Check(pack).Select(e => e.Path).ToList();

        Assert.Contains("$.objects", paths); // orange twice
        Assert.Contains("$.objects[2].effect.actions[0].objectId", paths);
        Assert.Contains("$.objects[3].kind", paths);
        Assert.Contains("$.objects[4].kind", paths);
        Assert.Contains("$.objects[5].effect.trigger", paths);
        Assert.Contains("$.objects[6].effect.trigger", paths);
        Assert.Contains("$.objects[7].effect.actions[0]", paths);
        Assert.Contains("$.objects[8].effect.actions[0].wheel", paths);
        Assert.Contains("$.wheels[0].entries[0].objectId", paths);
    }

    [Theory]
    [InlineData("runCondition")]
    [InlineData("deck")]
    [InlineData("hostileIncoming")]
    [InlineData("chosenTarget")]
    [InlineData("chosenPick")]
    [InlineData("choiceAfterRoll")]
    [InlineData("negativeCount")]
    [InlineData("nextReroll")]
    [InlineData("emptyWheel")]
    [InlineData("wheelName")]
    public void What_stage_four_does_not_play_is_refused(string what)
    {
        var examples = EconomyScenario.Examples();
        var self = new TargetSpec { Selector = TargetSelector.Self };
        EffectSpec Acts(params ActionSpec[] actions) => new() { Target = self, Actions = [.. actions] };
        var item = examples["orange"];
        var effect = examples["curse-effect"];
        var (definition, wheel) = what switch
        {
            "runCondition" => (item with { Effect = Acts(new ModifyNextRollAction { RunCondition = new ConditionSpec { DifficultyAtLeast = Difficulty.Hard } }) }, null),
            "deck" => (item with { Effect = Acts(new DrawEventAction { Deck = "risky" }) }, null),
            "hostileIncoming" => (effect with { Effect = effect.Effect! with { Trigger = Trigger.HostileIncoming } }, null),
            "chosenTarget" => (effect with { Effect = effect.Effect! with { Target = new TargetSpec { Selector = TargetSelector.Chosen } } }, null),
            "chosenPick" => (effect with { Effect = effect.Effect! with { Actions = [new TakeObjectAction { Mode = TakeMode.Destroy, Pick = Pick.Chosen }] } }, null),
            "choiceAfterRoll" => (item with { Effect = Acts(new RollAction { Dice = ContentValue.TryParse("1d6")! }, new RequestChoiceAction { Prompt = "Что?", Options = new ChoiceOptionsSpec { List = ["a", "b"] } }) }, null),
            "negativeCount" => (item with { Effect = Acts(new ModifyDiceAction { When = DiceWhen.Next, Stage = DiceStage.Count, Value = ContentValue.Of(-1) }) }, null),
            "nextReroll" => (item with { Effect = Acts(new ModifyDiceAction { When = DiceWhen.Next, Stage = DiceStage.Reroll, Value = ContentValue.Of(1) }) }, null),
            "emptyWheel" => (item, new WheelDefinition { Id = "w", Name = "Пусто", Entries = [] }),
            _ => (item, new WheelDefinition { Id = "w", Name = " ", Entries = [new WheelEntrySpec { ObjectId = "orange", Weight = 1 }] }),
        };
        var pack = new ContentPack { Objects = [definition], Wheels = wheel is null ? [] : [wheel] };

        Assert.NotEmpty(ContentPublishing.Check(pack));
    }

    [Fact]
    public void Content_is_published_into_the_log_and_read_back_by_a_replay()
    {
        var s = Season().WithContent(EconomyScenario.RepositoryPack());

        var published = Assert.Single(s.LastEvents<ContentPublished>());
        Assert.Equal(1, published.Version);
        Assert.Equal("Контент сезона", published.Comment);
        Assert.Equal(s.State, SeasonEngine.Replay(s.Log));
        Assert.Equal(published, EventCodec.Decode(EventCodec.Encode(published)));
        Assert.NotNull(s.State.Catalog.Live("shield"));
    }

    [Fact]
    public void Content_needs_the_items_flag_a_comment_and_a_draft_or_running_season()
    {
        var off = Scenario.New().WithPlayers("Вася");
        off.Act(new PublishContent(EconomyScenario.ExamplePack(), "Контент"));
        Assert.Equal(RejectionCodes.FeatureDisabled, off.Last.Rejection!.Code);

        var s = Season();
        s.Act(new PublishContent(EconomyScenario.ExamplePack(), " "));
        Assert.Equal(RejectionCodes.CommentRequired, s.Last.Rejection!.Code);
        s.Act(new PublishContent(EconomyScenario.ExamplePack(), new string('я', Limits.MaxCommentLength + 1)));
        Assert.Equal(RejectionCodes.CommentTooLong, s.Last.Rejection!.Code);

        s.MoveStatusTo(SeasonStatus.Closing);
        s.Act(new PublishContent(EconomyScenario.ExamplePack(), "Поздно"));
        Assert.Equal(RejectionCodes.SeasonClosed, s.Last.Rejection!.Code);
    }

    [Fact]
    public void Content_is_published_in_a_draft_too()
    {
        var s = EconomyScenario.New().AsDraft().WithPlayers("Вася");

        s.Act(new PublishContent(EconomyScenario.ExamplePack("orange"), "Черновик"));

        Assert.True(s.Last.IsAccepted);
    }

    [Fact]
    public void An_invalid_pack_is_refused_with_its_problems()
    {
        var s = Season();
        s.Act(new PublishContent(EconomyScenario.ExamplePack("shield"), "Без эффекта щита"));

        Assert.Equal(RejectionCodes.ContentInvalid, s.Last.Rejection!.Code);
        Assert.Contains("shield-effect", s.Last.Rejection.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void The_same_pack_again_is_refused_as_unchanged()
    {
        var s = Season().WithContent(EconomyScenario.ExamplePack("orange", "shield", "shield-effect"));

        s.Act(new PublishContent(EconomyScenario.ExamplePack("shield-effect", "orange", "shield"), "Ещё раз"));

        Assert.Equal(RejectionCodes.ContentUnchanged, s.Last.Rejection!.Code);
    }

    [Fact]
    public void A_definition_left_out_is_soft_deleted_and_the_objects_held_keep_working()
    {
        var s = Season().WithContent(EconomyScenario.ExamplePack("orange", "reroll-coupon"));
        var orange = s.Give("Вася", "orange");

        s.WithContent(EconomyScenario.ExamplePack("reroll-coupon"));

        var entry = s.State.Catalog.Objects["orange"];
        Assert.True(entry.Deleted);
        Assert.Null(s.State.Catalog.Live("orange"));
        Assert.Equal(2, s.State.Catalog.Version);
        s.Use("Вася", orange);
        Assert.True(s.Last.IsAccepted);
        Assert.Equal("c1", s.Player("Вася").CellId);

        // A deleted definition is not given any more
        s.Act(new AdjustInventory(s.PlayerId("Петя"), "orange", null, "Выдать"));
        Assert.Equal(RejectionCodes.ObjectUnknown, s.Last.Rejection!.Code);

        // It comes back when a later pack has it again
        s.WithContent(EconomyScenario.ExamplePack("orange", "reroll-coupon"));
        Assert.False(s.State.Catalog.Objects["orange"].Deleted);
    }

    [Fact]
    public void A_publication_is_not_undone()
    {
        var s = Season().WithContent(EconomyScenario.ExamplePack("orange"));

        s.Act(new UndoCommand(s.LastCommandId, "Отменить"));

        Assert.Equal(RejectionCodes.UndoNotUndoable, s.Last.Rejection!.Code);
    }
}
