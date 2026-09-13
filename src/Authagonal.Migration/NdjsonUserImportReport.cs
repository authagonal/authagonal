namespace Authagonal.Migration;

/// <summary>
/// Result of one <see cref="NdjsonUserImportEngine.RunAsync"/> call. JSON-serializable (the CLI prints
/// it via <c>System.Text.Json</c> reflection, matching how <c>Authagonal.Migration.Cli</c> already
/// prints <see cref="DuendeMigrationReport"/> — the CLI executable is not trimmed, unlike this package).
/// </summary>
public sealed class NdjsonUserImportReport
{
    public bool DryRun { get; set; }

    /// <summary>Non-blank lines read. Blank lines are skipped and not counted anywhere in this report.</summary>
    public int TotalLines { get; set; }

    public int Imported { get; set; }
    public int Updated { get; set; }
    public int Skipped { get; set; }

    /// <summary>Lines that failed to parse or validate. May exceed <see cref="Failures"/>.Count — only
    /// the first 20 failures are retained with their reason.</summary>
    public int Failed { get; set; }

    /// <summary>The first 20 failures, in line order.</summary>
    public List<NdjsonImportFailure> Failures { get; set; } = [];
}

/// <summary>One failed line: its 1-based line number in the input file and why it failed.</summary>
public sealed record NdjsonImportFailure(int LineNumber, string Reason);
