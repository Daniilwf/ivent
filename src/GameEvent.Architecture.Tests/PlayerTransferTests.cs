using System.Reflection;

namespace GameEvent.Architecture.Tests;

/// <summary>
/// SPEC «Проверка на дыры», «Подкармливать друга»: прямых переводов нет (AU12-4). No engine command moves points, coins or
/// objects from one player to another at the player's word: a command names at most its own actor, and a command that
/// names another player is on the list below with what it does to them. Item effects act by the item's data (a theft, a
/// push, D-478 `takeObject.mode: give` — the item's own text says it gives, see D-634), never by an amount the player types.
/// </summary>
public class PlayerTransferTests
{
    /// <summary>Commands that name a player besides their actor, and why that is not a transfer between players.</summary>
    private static readonly Dictionary<string, string> s_namingAnotherPlayer = new()
    {
        // The target of an item: what happens to them is the item's data (content), not the user's choice of an amount
        ["UseItem"] = "TargetPlayerId",
        // A bet on a player: the stake goes to the bank and the win comes from it; the player bet on gets nothing
        ["PlaceBet"] = "OnPlayerId",
    };

    private static readonly string[] s_transferWords = ["Transfer", "Give", "Gift", "Send", "Donate", "Pay", "Lend", "Share"];

    private static IEnumerable<Type> Commands()
    {
        var command = Assemblies.Engine.GetTypes().Single(t => t.Name == "ICommand" && t.IsInterface);
        return Assemblies.Engine.GetTypes().Where(t => t.IsClass && !t.IsAbstract && command.IsAssignableFrom(t));
    }

    [Fact]
    public void No_command_is_a_transfer_between_players()
    {
        var named = Commands()
            .Where(t => s_transferWords.Any(w => t.Name.Contains(w, StringComparison.Ordinal)))
            .Select(t => t.FullName)
            .ToList();

        Assert.Empty(named);
    }

    [Fact]
    public void Only_listed_commands_name_another_player()
    {
        // A player id besides the actor's own PlayerId: OnPlayerId, TargetPlayerId, ToPlayerId…
        var naming = Commands()
            .SelectMany(t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.Name != "PlayerId" && p.Name.EndsWith("PlayerId", StringComparison.Ordinal)
                    && (p.PropertyType == typeof(Guid) || p.PropertyType == typeof(Guid?)))
                .Select(p => $"{t.Name}.{p.Name}"))
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Equal(s_namingAnotherPlayer.Select(x => $"{x.Key}.{x.Value}").Order(StringComparer.Ordinal), naming);
    }

    [Fact]
    public void A_command_naming_another_player_carries_no_amount_for_them()
    {
        // The bet's stake is the only number next to another player, and it is paid to the bank (see the list)
        var amounts = Commands()
            .Where(t => s_namingAnotherPlayer.ContainsKey(t.Name) && t.Name != "PlaceBet")
            .SelectMany(t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.PropertyType == typeof(int) || p.PropertyType == typeof(decimal) || p.PropertyType == typeof(long))
                .Select(p => $"{t.Name}.{p.Name}"))
            .ToList();

        Assert.Empty(amounts);
    }
}
