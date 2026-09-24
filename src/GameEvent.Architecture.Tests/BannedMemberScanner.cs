using Mono.Cecil;
using Mono.Cecil.Cil;

namespace GameEvent.Architecture.Tests;

/// <summary>
/// Scans IL for members the engine must not touch. A second line of defence behind
/// BannedApiAnalyzers: it also sees code the analyzer might miss (generated code, suppressions).
/// </summary>
internal static class BannedMemberScanner
{
    private static readonly HashSet<string> s_bannedMembers =
    [
        "System.DateTime System.DateTime::get_Now()",
        "System.DateTime System.DateTime::get_UtcNow()",
        "System.DateTime System.DateTime::get_Today()",
        "System.DateTimeOffset System.DateTimeOffset::get_Now()",
        "System.DateTimeOffset System.DateTimeOffset::get_UtcNow()",
        "System.TimeProvider System.TimeProvider::get_System()",
        "System.Int32 System.Environment::get_TickCount()",
        "System.Int64 System.Environment::get_TickCount64()",
        "System.Guid System.Guid::NewGuid()",
        "System.Guid System.Guid::CreateVersion7()",
        "System.Guid System.Guid::CreateVersion7(System.DateTimeOffset)",
    ];

    private static readonly HashSet<string> s_bannedDeclaringTypes =
    [
        "System.Random",
        "System.Security.Cryptography.RandomNumberGenerator",
        "System.Diagnostics.Stopwatch",
    ];

    public static List<string> FindViolations(string assemblyPath, Func<TypeDefinition, bool>? typeFilter = null)
    {
        using var module = ModuleDefinition.ReadModule(assemblyPath);
        var violations = new List<string>();

        var types = module.GetTypes().Where(t => typeFilter is null || typeFilter(t));
        foreach (var method in types.SelectMany(t => t.Methods).Where(m => m.HasBody))
        {
            foreach (var instruction in method.Body.Instructions)
            {
                if (instruction.OpCode.OperandType != OperandType.InlineMethod
                    || instruction.Operand is not MethodReference called)
                {
                    continue;
                }

                if (s_bannedMembers.Contains(called.FullName)
                    || s_bannedDeclaringTypes.Contains(called.DeclaringType.FullName))
                {
                    violations.Add($"{method.FullName} calls {called.FullName}");
                }
            }
        }

        return violations;
    }
}
