namespace Authagonal.Migration;

/// <summary>
/// Thrown by <see cref="NdjsonUserImportEngine"/> when <see cref="OnDuplicateAction.Fail"/> is
/// configured and a line's email (case-insensitive) already exists in the target store. Aborts the run
/// immediately — unlike an ordinary per-line validation failure, which the engine records and continues
/// past, this is a deliberate operator choice to stop rather than silently skip or overwrite.
/// </summary>
public sealed class NdjsonDuplicateUserImportException(int lineNumber, string email)
    : Exception($"Line {lineNumber}: a user with email '{email}' already exists (--on-duplicate fail).")
{
    /// <summary>1-based line number in the NDJSON input that triggered the abort.</summary>
    public int LineNumber { get; } = lineNumber;

    /// <summary>The duplicate email, as it appeared on that line.</summary>
    public string Email { get; } = email;
}
