namespace GameEvent.Infrastructure.Site;

/// <summary>
/// Maintenance mode (SPEC «Режим обслуживания», E5, D-121): while a deploy runs the site only reads. The mode is a flag
/// file next to the database, so the deploy script turns it on and off with <c>touch</c> and <c>rm</c>, it survives the
/// restart in the middle of a deploy, and the new version starts in it. The file is read on every check: a script's
/// change is seen at once, without a restart.
/// </summary>
public sealed class MaintenanceMode(string flagPath)
{
    /// <summary>The refusal of a command or a request while the site only reads.</summary>
    public const string Code = "site.maintenance";

    public string FlagPath { get; } = Path.GetFullPath(flagPath);

    public bool IsOn => File.Exists(FlagPath);

    public void TurnOn()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FlagPath)!);
        File.WriteAllText(FlagPath, "The site is under maintenance: it only reads. Delete this file to end it.\n");
    }

    public void TurnOff() => File.Delete(FlagPath);
}
