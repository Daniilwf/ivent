using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text;
using GameEvent.Engine.Kernel;

namespace GameEvent.Engine.Tests.Support;

/// <summary>
/// Prints values as C# for <see cref="Scenario.ToCode"/> (C13): a failing random game is shown as the builder calls that
/// replay it. Commands and nested records by their constructors (optional arguments at their defaults left out) or by
/// object initializers, a record that differs from a known one as a <c>with</c> expression, a player or game id by its
/// name in the scenario. Ids made by the scenario are sequential, so <c>Guid.Parse</c> of an id replays as well.
/// </summary>
internal static class ScenarioCode
{
    private static readonly Assembly s_engine = typeof(ICommand).Assembly;

    /// <summary>A value as a C# expression; <paramref name="name"/> names the ids it knows (null — print the id).</summary>
    public static string Value(object? value, Func<Guid, string?> name)
    {
        switch (value)
        {
            case null:
                return "null";
            case string text:
                return Literal(text);
            case bool flag:
                return flag ? "true" : "false";
            case int or long or short or byte:
                return Convert.ToString(value, CultureInfo.InvariantCulture)!;
            case decimal number:
                return number.ToString(CultureInfo.InvariantCulture) + "m";
            case double number:
                return number.ToString("R", CultureInfo.InvariantCulture);
            case Guid id:
                return name(id) ?? $"Guid.Parse(\"{id}\")";
            case DateTimeOffset time:
                return $"DateTimeOffset.Parse(\"{time.ToString("O", CultureInfo.InvariantCulture)}\", CultureInfo.InvariantCulture)";
            case TimeSpan span:
                return Span(span);
            case Enum member:
                return $"{TypeName(member.GetType())}.{member}";
            case IEnumerable items:
                return $"[{string.Join(", ", items.Cast<object?>().Select(i => Value(i, name)))}]";
            default:
                return Record(value, name);
        }
    }

    /// <summary>
    /// <paramref name="after"/> as a <c>with</c> expression on <paramref name="root"/> (the expression of
    /// <paramref name="before"/>): only the properties that differ, nested records by their own <c>with</c>.
    /// </summary>
    public static string With(string root, object before, object after, Func<Guid, string?> name)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        var parts = new List<string>();
        foreach (var property in Properties(after.GetType()))
        {
            var was = property.GetValue(before);
            var now = property.GetValue(after);
            if (Equals(was, now))
            {
                continue;
            }

            parts.Add(was is not null && now is not null && IsRecord(property.PropertyType) && was.GetType() == now.GetType()
                ? $"{property.Name} = {With($"{root}.{property.Name}", was, now, name)}"
                : $"{property.Name} = {Value(now, name)}");
        }

        return parts.Count == 0 ? root : $"{root} with {{ {string.Join(", ", parts)} }}";
    }

    public static string Literal(string text)
    {
        var builder = new StringBuilder("\"");
        foreach (var c in text)
        {
            builder.Append(c switch
            {
                '"' => "\\\"",
                '\\' => "\\\\",
                '\n' => "\\n",
                '\r' => "\\r",
                '\t' => "\\t",
                _ when char.IsControl(c) => $"\\u{(int)c:x4}",
                _ => c.ToString(),
            });
        }

        return builder.Append('"').ToString();
    }

    public static string Span(TimeSpan span) =>
        span.Ticks % TimeSpan.TicksPerHour == 0 ? $"TimeSpan.FromHours({span.Ticks / TimeSpan.TicksPerHour})"
        : span.Ticks % TimeSpan.TicksPerMinute == 0 ? $"TimeSpan.FromMinutes({span.Ticks / TimeSpan.TicksPerMinute})"
        : $"TimeSpan.FromTicks({span.Ticks})";

    private static string Record(object value, Func<Guid, string?> name)
    {
        var type = value.GetType();
        var constructor = type.GetConstructors()
            .Where(c => !(c.GetParameters() is [var only] && only.ParameterType == type))
            .MaxBy(c => c.GetParameters().Length);
        var parameters = constructor?.GetParameters() ?? [];
        if (parameters.Length > 0)
        {
            var arguments = new List<string>();
            var skipped = false;
            foreach (var parameter in parameters)
            {
                var property = type.GetProperty(parameter.Name!, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                var argument = property?.GetValue(value);
                // At its default — an empty list stands for a null default too (commands normalize one to the other)
                if (parameter.HasDefaultValue
                    && (Equals(argument, parameter.DefaultValue)
                        || (parameter.DefaultValue is null && argument is IEnumerable items and not string && !items.Cast<object?>().Any())))
                {
                    skipped = true;
                    continue;
                }

                // Positional while nothing was left out, named after that
                arguments.Add(skipped ? $"{parameter.Name}: {Value(argument, name)}" : Value(argument, name));
            }

            return $"new {TypeName(type)}({string.Join(", ", arguments)})";
        }

        var members = Properties(type).Select(p => $"{p.Name} = {Value(p.GetValue(value), name)}");
        return $"new {TypeName(type)} {{ {string.Join(", ", members)} }}";
    }

    private static IEnumerable<PropertyInfo> Properties(Type type) =>
        type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetIndexParameters().Length == 0 && p.CanWrite && p.Name != "EqualityContract");

    private static bool IsRecord(Type type) =>
        type.Assembly == s_engine && type.IsClass && !typeof(IEnumerable).IsAssignableFrom(type) && type != typeof(string);

    private static string TypeName(Type type)
    {
        var name = type.Name;
        var tick = name.IndexOf('`', StringComparison.Ordinal);
        if (tick >= 0)
        {
            name = $"{name[..tick]}<{string.Join(", ", type.GetGenericArguments().Select(TypeName))}>";
        }

        return type.DeclaringType is { } outer ? $"{TypeName(outer)}.{name}" : name;
    }
}
