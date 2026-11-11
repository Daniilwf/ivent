using System.Text.Json;
using GameEvent.Engine.Kernel;

namespace GameEvent.Engine.Tests.Kernel;

/// <summary>
/// The value types of the log and state (<see cref="EquatableArray{T}"/>, <see cref="ResourceBag"/>): records that hold
/// them compare by content, so a replayed state equals the stored one (invariant 4 «Повтор по логу детерминирован»).
/// </summary>
public class ValueSemanticsTests
{
    [Fact]
    public void Lists_with_the_same_items_are_equal_and_hash_alike()
    {
        EquatableArray<int> a = [1, 2, 3];
        var b = new[] { 1, 2, 3 }.ToEquatableArray();

        Assert.True(a == b);
        Assert.False(a != b);
        Assert.True(a.Equals((object)b));
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void Lists_differ_by_order_or_length_and_never_equal_another_type()
    {
        EquatableArray<int> a = [1, 2, 3];

        Assert.True(a != [3, 2, 1]);
        Assert.True(a != [1, 2]);
        Assert.False(a.Equals((object)new[] { 1, 2, 3 }));
        Assert.False(a.Equals(null));
    }

    [Fact]
    public void Default_list_is_an_empty_list()
    {
        var none = default(EquatableArray<string>);

        Assert.Empty(none);
        Assert.Equal(EquatableArray.Create<string>([]), none);
        Assert.Equal("[]", none.ToString());
        Assert.Throws<ArgumentOutOfRangeException>(() => none[0]);
    }

    [Fact]
    public void List_prints_its_items_and_indexes_them()
    {
        var list = EquatableArray.Create<string>(["a", "b"]);

        Assert.Equal("[a, b]", list.ToString());
        Assert.Equal("b", list[1]);
        Assert.Equal(2, list.Count);
    }

    [Fact]
    public void List_round_trips_through_json_and_null_reads_as_empty()
    {
        EquatableArray<int> list = [4, 5];

        var json = JsonSerializer.Serialize(list, EngineJson.Options);

        Assert.Equal("[4,5]", json);
        Assert.Equal(list, JsonSerializer.Deserialize<EquatableArray<int>>(json, EngineJson.Options));
        Assert.Empty(JsonSerializer.Deserialize<EquatableArray<int>>("null", EngineJson.Options));
    }

    [Fact]
    public void Bad_list_item_names_the_list_item()
    {
        var error = Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<EquatableArray<int>>("[1,\"x\"]", EngineJson.Options));

        Assert.StartsWith("Invalid list item:", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Resources_compare_by_content_whatever_the_order_of_adding()
    {
        var a = ResourceBag.Empty.Add("freeRerolls", 1).Add("keys", 2);
        var b = ResourceBag.From([new("keys", 2), new("freeRerolls", 1)]);

        Assert.True(a == b);
        Assert.False(a != b);
        Assert.True(a.Equals((object)b));
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.True(a != ResourceBag.Empty.Add("keys", 2));
        Assert.False(a.Equals("keys"));
    }

    [Fact]
    public void Resource_spent_to_zero_is_not_stored()
    {
        var bag = ResourceBag.Empty.Add("keys", 2).Add("keys", -2);

        Assert.Equal(0, bag.Count);
        Assert.Equal(ResourceBag.Empty, bag);
        Assert.Equal(0, bag["keys"]);
    }

    [Fact]
    public void Default_resources_are_empty_resources()
    {
        var none = default(ResourceBag);

        Assert.Equal(ResourceBag.Empty, none);
        Assert.Equal(0, none.Count);
        Assert.Equal(1, none.Add("keys", 1)["keys"]);
        Assert.Equal("{}", none.ToString());
    }

    [Fact]
    public void Resources_print_and_enumerate_in_name_order()
    {
        var bag = ResourceBag.From([new("keys", 2), new("coins2", -1)]);

        Assert.Equal("{coins2: -1, keys: 2}", bag.ToString());
        Assert.Equal(["coins2", "keys"], bag.Select(p => p.Key));
        Assert.Equal(2, ((System.Collections.IEnumerable)bag).Cast<object>().Count());
    }

    [Fact]
    public void Resources_round_trip_through_json_and_null_reads_as_empty()
    {
        var bag = ResourceBag.From([new("keys", 2)]);

        var json = JsonSerializer.Serialize(bag, EngineJson.Options);

        Assert.Equal("{\"keys\":2}", json);
        Assert.Equal(bag, JsonSerializer.Deserialize<ResourceBag>(json, EngineJson.Options));
        Assert.Equal(ResourceBag.Empty, JsonSerializer.Deserialize<ResourceBag>("null", EngineJson.Options));
    }
}
