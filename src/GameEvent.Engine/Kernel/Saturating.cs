namespace GameEvent.Engine.Kernel;

/// <summary>
/// Whole-number arithmetic that stops at the ends of <see cref="int"/> instead of wrapping around (SEC-6, D-652): points,
/// coins and resources never change sign through an overflow, however big the content's numbers.
/// </summary>
public static class Saturating
{
    public static int Add(int a, int b) => Clamp((long)a + b);

    public static int Multiply(int a, int b) => Clamp((long)a * b);

    public static int Clamp(long value) => (int)Math.Clamp(value, int.MinValue, int.MaxValue);

    public static int Clamp(decimal value) => (int)Math.Clamp(value, int.MinValue, int.MaxValue);
}
