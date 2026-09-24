using System.Xml.Linq;
using NetArchTest.Rules;

namespace GameEvent.Architecture.Tests;

public class ProjectDependencyTests
{
    private static readonly string[] s_engineForbiddenNamespaces =
    [
        "Microsoft.AspNetCore",
        "Microsoft.EntityFrameworkCore",
        "Microsoft.Data",
        "System.Data",
        "System.Net",
        "Serilog",
        "GameEvent.Infrastructure",
        "GameEvent.Web",
    ];

    private static readonly string[] s_engineForbiddenTypes =
    [
        "System.IO.File",
        "System.IO.FileInfo",
        "System.IO.FileStream",
        "System.IO.Directory",
        "System.IO.DirectoryInfo",
        "System.Random",
        "System.Diagnostics.Stopwatch",
        "System.Threading.Timer",
    ];

    [Fact]
    public void Engine_does_not_depend_on_web_database_network_or_other_projects()
    {
        var result = Assemblies.EngineTypes()
            .ShouldNot()
            .HaveDependencyOnAny(s_engineForbiddenNamespaces)
            .GetResult();

        Assert.True(result.IsSuccessful, Describe(result));
    }

    [Fact]
    public void Engine_does_not_use_file_system_timer_or_random_types()
    {
        var result = Assemblies.EngineTypes()
            .ShouldNot()
            .HaveDependencyOnAny(s_engineForbiddenTypes)
            .GetResult();

        Assert.True(result.IsSuccessful, Describe(result));
    }

    [Fact]
    public void Engine_references_no_assemblies_besides_the_runtime()
    {
        var references = Assemblies.Engine.GetReferencedAssemblies()
            .Select(a => a.Name!)
            .Where(name => !IsRuntimeAssembly(name))
            .ToList();

        Assert.Empty(references);
    }

    [Fact]
    public void Infrastructure_does_not_depend_on_web()
    {
        var result = Types.InAssembly(Assemblies.Infrastructure)
            .ShouldNot()
            .HaveDependencyOnAny("GameEvent.Web", "Microsoft.AspNetCore")
            .GetResult();

        Assert.True(result.IsSuccessful, Describe(result));
        Assert.DoesNotContain("GameEvent.Web", ProjectReferences("GameEvent.Infrastructure"));
    }

    [Fact]
    public void Simulator_uses_only_the_engine()
    {
        Assert.Equal(["GameEvent.Engine"], ProjectReferences("GameEvent.Simulator"));
    }

    [Fact]
    public void Import_tool_does_not_depend_on_web()
    {
        Assert.DoesNotContain("GameEvent.Web", ProjectReferences("GameEvent.Tools.Import"));
    }

    [Fact]
    public void Engine_has_no_project_or_package_references_except_analyzers()
    {
        var project = LoadProject("GameEvent.Engine");

        Assert.Empty(project.Descendants("ProjectReference"));
        Assert.All(
            project.Descendants("PackageReference"),
            p => Assert.Equal("all", p.Attribute("PrivateAssets")?.Value));
    }

    // Project references are read from csproj: the compiler drops unused references from assembly metadata.
    private static List<string> ProjectReferences(string projectName) =>
        LoadProject(projectName).Descendants("ProjectReference")
            .Select(e => Path.GetFileNameWithoutExtension(e.Attribute("Include")!.Value.Replace('\\', '/')))
            .Order(StringComparer.Ordinal)
            .ToList();

    private static XDocument LoadProject(string projectName) =>
        XDocument.Load(Path.Combine(RepositoryRoot(), "src", projectName, projectName + ".csproj"));

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "GameEvent.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("GameEvent.slnx not found above the test output.");
    }

    private static bool IsRuntimeAssembly(string name) =>
        name is "System" or "netstandard" or "mscorlib"
        || name.StartsWith("System.", StringComparison.Ordinal)
        || name.StartsWith("Microsoft.Win32.", StringComparison.Ordinal);

    private static string Describe(NetArchTest.Rules.TestResult result) =>
        result.IsSuccessful
            ? string.Empty
            : "Violations: " + string.Join(", ", result.FailingTypeNames ?? []);
}
