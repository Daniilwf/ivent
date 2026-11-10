using GameEvent.Engine.Content;
using GameEvent.Engine.Kernel;
using GameEvent.Engine.Tests.Support;

namespace GameEvent.Engine.Tests.Economy;

/// <summary>
/// Items that pick games against games deleted from the pool (RGG 15, D-208, D-423): a deleted game — whatever its
/// deletion reason — is no option of a choice and is never rolled under a genre an item imposed.
/// </summary>
public class DeletedGameTests
{
    private static readonly TargetSpec s_self = new() { Selector = TargetSelector.Self };

    private static ObjectDefinition Picker() => new()
    {
        Id = "pick-game",
        Kind = ObjectKind.Item,
        Name = "pick-game",
        Description = "Тест.",
        Rarity = Rarity.Common,
        Window = UseWindow.Anytime,
        Effect = new EffectSpec
        {
            Target = s_self,
            Actions = [new RequestChoiceAction { Prompt = "Что?", Options = new ChoiceOptionsSpec { From = ChoiceSource.Games } }],
        },
    };

    [Fact]
    public void A_game_deleted_from_the_pool_is_no_option_of_a_choice_of_games()
    {
        var s = EconomyScenario.New().WithCategory("Horror").WithGame("Silent Hill", 3, "Horror").WithGame("Dead Space", 3, "Horror")
            .WithPlayers("Вася", "Петя");
        var pack = EconomyScenario.RepositoryPack();
        s.WithContent(pack with { Objects = [.. pack.Objects, Picker()] });
        s.Give("Вася", "pick-game");
        s.DeleteGame("Dead Space");

        s.Use("Вася", s.Held("Вася", "pick-game").InstanceId, null, s.GameId("Dead Space").ToString("N"));

        Assert.Equal(RejectionCodes.ItemInvalidChoice, s.Last.Rejection!.Code);
        s.Used("Вася", "pick-game", null, s.GameId("Silent Hill").ToString("N"));
    }

    [Fact]
    public void A_genre_imposed_by_an_item_never_rolls_a_game_deleted_from_the_pool()
    {
        // Given Вася imposes RPG on Петя, and one of the two RPG games is deleted
        var s = EconomyScenario.New().WithCategory("Horror").WithCategory("RPG")
            .WithGame("Persona", 3, "RPG").WithGame("Disco Elysium", 3, "RPG").WithGame("Silent Hill", 3, "Horror")
            .WithPlayers("Вася", "Петя").WithPoints("Петя", 10);
        s.WithContent(EconomyScenario.RepositoryPack());
        s.Give("Вася", "dirty-trick");
        s.Used("Вася", "dirty-trick", "Петя", "RPG");
        s.DeleteGame("Persona");

        // When Петя rolls
        s.Roll("Петя").Start("Петя");

        // Then the roll keeps the genre and picks the game still in the pool
        var run = s.State.Runs[s.Player("Петя").ActiveRunId!.Value];
        Assert.Equal(s.GameId("Disco Elysium"), run.GameId);
    }
}
