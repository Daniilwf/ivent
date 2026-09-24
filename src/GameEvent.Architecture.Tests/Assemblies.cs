using System.Reflection;
using GameEvent.Engine.Kernel;

namespace GameEvent.Architecture.Tests;

internal static class Assemblies
{
    public static readonly Assembly Engine = typeof(IClock).Assembly;
    public static readonly Assembly Infrastructure = Assembly.Load("GameEvent.Infrastructure");
    public static readonly Assembly Web = typeof(Program).Assembly;
    public static readonly Assembly Simulator = Assembly.Load("GameEvent.Simulator");
    public static readonly Assembly Import = Assembly.Load("GameEvent.Tools.Import");
}
