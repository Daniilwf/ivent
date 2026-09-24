using System.Reflection;

namespace GameEvent.Architecture.Tests;

/// <summary>
/// Code is organised by mechanic (Rolls, Runs, Map, Economy…), not by technical layer.
/// A top-level namespace named after a layer means a mechanic was split across folders.
/// </summary>
public class MechanicFolderTests
{
    private static readonly HashSet<string> s_layerNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Commands", "Events", "Handlers", "Services", "Models", "Entities",
        "Dtos", "Repositories", "Interfaces", "Helpers", "Utils", "Managers", "Controllers",
    };

    public static TheoryData<string> MechanicAssemblies() =>
        ["GameEvent.Engine", "GameEvent.Infrastructure", "GameEvent.Web"];

    [Theory]
    [MemberData(nameof(MechanicAssemblies))]
    public void Top_level_namespaces_are_mechanics_not_layers(string assemblyName)
    {
        var assembly = Assembly.Load(assemblyName);

        var offenders = assembly.GetTypes()
            .Where(t => t.Namespace is not null && !IsCompilerGenerated(t))
            .Select(t => (Type: t, Segment: TopLevelSegment(t.Namespace!, assemblyName)))
            .Where(x => x.Segment is not null && s_layerNames.Contains(x.Segment))
            .Select(x => x.Type.FullName)
            .ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    public void Engine_types_live_in_a_mechanic_folder_not_in_the_root()
    {
        var rootTypes = Assemblies.Engine.GetTypes()
            .Where(t => !IsCompilerGenerated(t) && t.Namespace == "GameEvent.Engine")
            .Select(t => t.FullName)
            .ToList();

        Assert.Empty(rootTypes);
    }

    private static string? TopLevelSegment(string ns, string assemblyName) =>
        ns.StartsWith(assemblyName + ".", StringComparison.Ordinal)
            ? ns[(assemblyName.Length + 1)..].Split('.')[0]
            : null;

    private static bool IsCompilerGenerated(Type t) =>
        t.FullName?.Contains('<', StringComparison.Ordinal) == true;
}
