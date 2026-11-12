using GameEvent.Infrastructure.Seasons;
using GameEvent.Web.Seasons;

namespace GameEvent.Web.Tests.Api;

/// <summary>
/// The tie-breaks of the Russian name order (K1O-1, D-631): «Ё» after «Е», a prefix before the longer name, case by the
/// ru-RU culture, null first, and equal names by id in the token order (D-150), so players with matching names keep
/// their token colours.
/// </summary>
public sealed class NameOrderTests
{
    private static int Sign(string? x, string? y) => Math.Sign(PlayerTokens.NameOrder.Compare(x, y));

    [Theory]
    [InlineData("Енот", "Ёж")]
    [InlineData("Ёж", "Ж")]
    [InlineData("Енот", "Ж")]
    [InlineData("Еж", "Ёж")]
    [InlineData("Ёж", "Ёжик")]
    [InlineData("заяц", "Заяц")]
    [InlineData("Аист", "аистёнок")]
    [InlineData("Ёж", "Яж")]
    public void First_name_goes_before_the_second(string first, string second)
    {
        Assert.Equal(-1, Sign(first, second));
        Assert.Equal(1, Sign(second, first));
    }

    [Fact]
    public void Same_name_is_equal() => Assert.Equal(0, Sign("Ёж", "Ёж"));

    [Fact]
    public void Null_goes_first()
    {
        Assert.Equal(0, Sign(null, null));
        Assert.Equal(-1, Sign(null, "Аист"));
        Assert.Equal(1, Sign("Аист", null));
    }

    [Fact]
    public void Distinct_names_never_compare_equal()
    {
        // The last, ordinal step makes the order total: the Kelvin sign and «K» are one letter to the culture's eye
        string[] names = ["K", "K", "k", "Ёж", "ёж", "ЁЖ", "Еж", "еж", "Ёж"];
        foreach (var x in names)
        {
            foreach (var y in names.Where(y => !string.Equals(x, y, StringComparison.Ordinal)))
            {
                Assert.True(Sign(x, y) != 0, $"{x} vs {y}");
                Assert.Equal(-Sign(x, y), Sign(y, x));
            }
        }
    }

    [Fact]
    public void Players_with_the_same_name_are_in_token_order_by_id()
    {
        var first = Player(Guid.Parse("00000000-0000-0000-0000-00000000000a"), "Ёж");
        var second = Player(Guid.Parse("00000000-0000-0000-0000-00000000000b"), "Ёж");
        var before = Player(Guid.Parse("00000000-0000-0000-0000-00000000000c"), "Енот");

        Assert.Equal([before.Id, first.Id, second.Id], new[] { second, first, before }.InTokenOrder().Select(p => p.Id));
        Assert.Equal([before.Id, first.Id, second.Id], new[] { first, before, second }.InTokenOrder().Select(p => p.Id));
    }

    private static SeasonPlayerRecord Player(Guid id, string name) =>
        new() { Id = id, Name = name, CellId = "start", ResourcesJson = "{}", PathJson = "[]" };
}
