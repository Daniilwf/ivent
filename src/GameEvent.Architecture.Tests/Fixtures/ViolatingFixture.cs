namespace GameEvent.Architecture.Tests.Fixtures;

/// <summary>Deliberately breaks engine rules so the scanner has something to catch.</summary>
internal static class ViolatingFixture
{
    public static DateTime Now() => DateTime.UtcNow;

    public static Guid Id() => Guid.NewGuid();

#pragma warning disable CA5394 // insecure randomness: this is the point of the fixture
    public static int Roll() => new Random(1).Next(6);
#pragma warning restore CA5394
}
