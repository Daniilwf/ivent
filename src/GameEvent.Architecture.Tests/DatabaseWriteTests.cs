using Mono.Cecil;
using Mono.Cecil.Cil;

namespace GameEvent.Architecture.Tests;

/// <summary>
/// CLAUDE.md invariant 2: every state change goes through the command queue. Only the queue processor writes
/// to the database and opens transactions; migrations are the one other writer. New writers (seed, import)
/// must be added here deliberately.
/// </summary>
public class DatabaseWriteTests
{
    private static readonly HashSet<string> s_allowedWriters =
    [
        "GameEvent.Infrastructure.Queue.CommandProcessor",
        "GameEvent.Infrastructure.Database.SqliteDatabase",
    ];

    private static readonly string[] s_writeMethods =
    [
        "SaveChanges",
        "SaveChangesAsync",
        "BeginTransaction",
        "BeginTransactionAsync",
        "ExecuteSqlRaw",
        "ExecuteSqlRawAsync",
        "ExecuteSqlInterpolated",
        "ExecuteSqlInterpolatedAsync",
        "ExecuteSql",
        "ExecuteSqlAsync",
        "ExecuteDelete",
        "ExecuteDeleteAsync",
        "ExecuteUpdate",
        "ExecuteUpdateAsync",
    ];

    public static TheoryData<string> Assemblies() => ["GameEvent.Infrastructure", "GameEvent.Web"];

    [Theory]
    [MemberData(nameof(Assemblies))]
    public void Only_the_queue_processor_and_migrations_write_to_the_database(string assemblyName)
    {
        var path = System.Reflection.Assembly.Load(assemblyName).Location;
        using var module = ModuleDefinition.ReadModule(path);

        var offenders = new List<string>();
        foreach (var type in module.GetTypes().Where(t => !t.Namespace.StartsWith(Architecture.Tests.Assemblies.CoverageInstrumentationNamespace, StringComparison.Ordinal)))
        {
            if (s_allowedWriters.Contains(OuterTypeName(type)))
            {
                continue;
            }

            foreach (var method in type.Methods.Where(m => m.HasBody))
            {
                foreach (var instruction in method.Body.Instructions)
                {
                    if (instruction.OpCode.OperandType == OperandType.InlineMethod
                        && instruction.Operand is MethodReference called
                        && IsDatabaseWrite(called))
                    {
                        offenders.Add($"{method.FullName} calls {called.DeclaringType.Name}.{called.Name}");
                    }
                }
            }
        }

        Assert.Empty(offenders);
    }

    private static bool IsDatabaseWrite(MethodReference called) =>
        s_writeMethods.Contains(called.Name)
        && called.DeclaringType.Namespace.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal);

    // Async methods and lambdas compile into nested types; they belong to their outer type.
    private static string OuterTypeName(TypeDefinition type)
    {
        while (type.DeclaringType is not null)
        {
            type = type.DeclaringType;
        }

        return type.FullName;
    }
}
