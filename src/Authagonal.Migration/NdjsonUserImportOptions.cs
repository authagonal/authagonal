namespace Authagonal.Migration;

/// <summary>
/// Bound from the <c>NdjsonImport</c> configuration section (or the CLI's command-line binder).
/// Controls a single run of the generic NDJSON → Authagonal user import.
/// </summary>
public sealed class NdjsonUserImportOptions
{
    public const string SectionName = "NdjsonImport";

    /// <summary>Path to the NDJSON file (one JSON object per line).</summary>
    public string? Input { get; set; }

    /// <summary>Parse + validate + report counts; write nothing.</summary>
    public bool DryRun { get; set; }

    /// <summary>How to treat a line whose email (case-insensitive) already exists in the target.</summary>
    public OnDuplicateAction OnDuplicate { get; set; } = OnDuplicateAction.Skip;

    /// <summary>
    /// How many lines to read between progress log lines. Not a write-batching mechanism —
    /// <see cref="Authagonal.Core.Stores.IUserStore"/> has no bulk API, so every import/update is still
    /// one store call; this only bounds how often <see cref="NdjsonUserImportEngine"/> reports progress.
    /// </summary>
    public int BatchSize { get; set; } = 500;

    /// <summary>
    /// When false (default), any top-level JSON property not in the documented schema fails that line
    /// with the unknown field names listed in the reason. Set true to ignore unrecognised fields instead.
    /// </summary>
    public bool AllowUnknownFields { get; set; }

    /// <summary>
    /// When true, a run that had one or more failed lines still exits 0. Does not affect
    /// <see cref="OnDuplicateAction.Fail"/>, which always aborts the run — this only changes the exit
    /// code for ordinary per-line parse/validation failures that the engine already skipped past.
    /// </summary>
    public bool ContinueOnError { get; set; }

    public TargetOptions Target { get; set; } = new();

    public sealed class TargetOptions
    {
        public string? ConnectionString { get; set; }
    }
}

/// <summary>How the import treats a line whose email already exists in the target store.</summary>
public enum OnDuplicateAction
{
    /// <summary>Leave the existing user untouched; count as skipped. Default — makes re-running idempotent.</summary>
    Skip,

    /// <summary>Abort the whole run by throwing <see cref="NdjsonDuplicateUserImportException"/>.</summary>
    Fail,

    /// <summary>Merge the line's present fields onto the existing user and write it back.</summary>
    Update,
}
