using System.Reflection;
using GameEvent.Engine.Kernel;

namespace GameEvent.Architecture.Tests;

/// <summary>
/// SPEC «Проверка на дыры», «Подкармливать друга … прямых переводов нет»: no command moves coins, points or objects from
/// one player to another. A command that names a second player is one of a reviewed list, and each of those moves nothing to
/// that player directly: an item works through its content (filters and refusals of D-478, D-487), a bet pledges the stake
/// to the system, not to the player bet on.
/// </summary>
public class TransferCommandTests
{
    // Command → the property naming the other player, and why it is no transfer
    private static readonly Dictionary<string, string> s_reviewed = new(StringComparer.Ordinal)
    {
        ["UseItem.TargetPlayerId"] = "the item's effect decides what changes hands (D-478, D-487)",
        ["PlaceBet.OnPlayerId"] = "the stake goes to the system's pledge, the payout comes from the system (D-414)",
    };

    private static readonly string[] s_transferWords = ["Transfer", "Gift", "Donate", "Send", "Give", "Pay"];

    private static IEnumerable<Type> Commands() =>
        Assemblies.Engine.GetTypes().Where(t => typeof(ICommand).IsAssignableFrom(t) && t is { IsInterface: false, IsAbstract: false });

    private static bool NamesPlayers(PropertyInfo p) =>
        p.Name.Contains("Player", StringComparison.Ordinal)
        && (p.PropertyType == typeof(Guid) || p.PropertyType == typeof(Guid?)
            || (p.PropertyType.IsGenericType && p.PropertyType.GetGenericArguments().Any(a => a == typeof(Guid))));

    [Fact]
    public void There_are_commands_to_check()
    {
        Assert.Contains(Commands(), t => t.Name == "UseItem");
        Assert.Contains(Commands(), t => t.Name == "PlaceBet");
    }

    [Fact]
    public void No_command_is_a_transfer_between_players()
    {
        var named = Commands().Where(t => s_transferWords.Any(w => t.Name.Contains(w, StringComparison.Ordinal))).Select(t => t.Name).ToList();

        Assert.Empty(named);
    }

    [Fact]
    public void Every_command_naming_another_player_is_reviewed()
    {
        var others = Commands()
            .SelectMany(t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.Name != "PlayerId" && NamesPlayers(p))
                .Select(p => $"{t.Name}.{p.Name}"))
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Equal(s_reviewed.Keys.Order(StringComparer.Ordinal), others);
    }
}
