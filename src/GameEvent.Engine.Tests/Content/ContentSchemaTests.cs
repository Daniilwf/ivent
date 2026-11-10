using System.Text.Json;
using GameEvent.Engine.Content;
using GameEvent.Engine.Tests.Support;
using Json.Schema;

namespace GameEvent.Engine.Tests.Content;

/// <summary>
/// EC1, D-411: the JSON schema of content is generated from the C# types and committed as docs/content.schema.json; every
/// base action is in it with its parameters, and the content files pass it. To refresh the file after changing the types:
/// set UPDATE_CONTENT_SCHEMA=1 and run <see cref="Schema_is_up_to_date"/>.
/// </summary>
public class ContentSchemaTests
{
    private const string SchemaFile = "content.schema.json";
    private const string UpdateVariable = "UPDATE_CONTENT_SCHEMA";

    [Fact]
    public void Schema_is_up_to_date()
    {
        var path = RepositoryPaths.Docs(SchemaFile);
        var generated = ContentSchema.Generate();
        if (Environment.GetEnvironmentVariable(UpdateVariable) == "1")
        {
            File.WriteAllText(path, generated);
            return;
        }

        Assert.True(File.Exists(path), $"docs/{SchemaFile} is missing. Run the tests with {UpdateVariable}=1 to generate it.");
        Assert.True(
            generated == File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal),
            $"docs/{SchemaFile} is out of date with the content types. Run the tests with {UpdateVariable}=1 to regenerate it.");
    }

    [Fact]
    public void Every_base_action_is_described_by_its_type()
    {
        var schema = ContentSchema.Generate();

        foreach (var type in new[] { "move", "changeResource", "roll", "giveObject", "takeObject", "transformObject", "drawEvent", "spinWheel", "modifyNextRoll", "modifyDice", "teleport", "requestChoice" })
        {
            Assert.Contains($"\"{type}\"", schema, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_content_files_pass_the_schema()
    {
        var schema = JsonSchema.FromText(File.ReadAllText(RepositoryPaths.Docs(SchemaFile)));
        var pack = ContentJson.Write(EconomyScenario.RepositoryPack());
        using var document = JsonDocument.Parse(pack);

        var results = schema.Evaluate(document.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List });

        Assert.True(results.IsValid, string.Join("\n", (results.Details ?? []).Where(d => d.Errors is not null).SelectMany(d => d.Errors!.Select(e => $"{d.InstanceLocation}: {e.Value}"))));
    }

    [Fact]
    public void A_typo_in_an_action_fails_the_schema()
    {
        var schema = JsonSchema.FromText(File.ReadAllText(RepositoryPaths.Docs(SchemaFile)));
        using var document = JsonDocument.Parse("""{"objects":[{"id":"x","kind":"item","name":"x","description":"x","effect":{"actions":[{"type":"move","stepz":1}]}}],"wheels":[]}""");

        Assert.False(schema.Evaluate(document.RootElement).IsValid);
    }
}
