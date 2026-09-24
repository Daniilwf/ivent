namespace GameEvent.Engine.Kernel;

/// <summary>
/// Why a command was refused. <see cref="Code"/> is stable and maps to a text in the interface dictionary;
/// <see cref="Detail"/> is a developer-facing explanation in English.
/// </summary>
public sealed record Rejection(string Code, string Detail);
