using Microsoft.Extensions.Logging;

namespace Authagonal.Migration;

/// <summary>
/// Generic NDJSON → Authagonal user import engine — a new import SOURCE reusing the same
/// stores/report/CLI shape as <see cref="DuendeMigrationEngine"/>, not a new pipeline. Reads one JSON
/// object per line, validates it (<see cref="NdjsonUserImportReader"/>), maps it onto
/// <see cref="Authagonal.Core.Models.AuthUser"/> (<see cref="NdjsonUserMapper"/>), and writes it through
/// <see cref="NdjsonUserImportStores.Users"/>. Every pass is idempotent under the default
/// <see cref="OnDuplicateAction.Skip"/>: re-running with an unchanged file writes nothing on the second
/// run.
/// </summary>
public sealed class NdjsonUserImportEngine(NdjsonUserImportStores stores, ILogger<NdjsonUserImportEngine>? logger = null)
{
    /// <summary>
    /// Runs one import pass. <see cref="NdjsonUserImportOptions.DryRun"/> still reads the file, resolves
    /// every duplicate against the target store, and produces the full report — it only skips the
    /// actual <c>CreateAsync</c>/<c>UpdateAsync</c> calls. Every line is attempted regardless of earlier
    /// failures; <see cref="OnDuplicateAction.Fail"/> is the one thing that aborts the run outright, by
    /// throwing <see cref="NdjsonDuplicateUserImportException"/>.
    /// </summary>
    public async Task<NdjsonUserImportReport> RunAsync(NdjsonUserImportOptions options, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(options.Input))
            throw new ArgumentException("NdjsonUserImportOptions.Input is required.", nameof(options));

        var batchSize = options.BatchSize > 0 ? options.BatchSize : 500;
        var report = new NdjsonUserImportReport { DryRun = options.DryRun };
        var now = DateTimeOffset.UtcNow;

        using var streamReader = new StreamReader(options.Input);
        var lineNumber = 0;
        var sinceLastLog = 0;
        string? rawLine;
        while ((rawLine = await streamReader.ReadLineAsync(ct).ConfigureAwait(false)) is not null)
        {
            lineNumber++;
            var line = rawLine.Trim();
            if (line.Length == 0) continue; // blank lines: not counted anywhere in the report

            report.TotalLines++;
            await ProcessLineAsync(lineNumber, line, options, stores, report, now, ct).ConfigureAwait(false);

            sinceLastLog++;
            if (sinceLastLog >= batchSize)
            {
                LogProgress(report);
                sinceLastLog = 0;
            }
        }

        if (sinceLastLog > 0) LogProgress(report);
        return report;
    }

    private void LogProgress(NdjsonUserImportReport report) =>
        logger?.LogInformation(
            "ndjson-users: {Total} lines read, {Imported} imported, {Updated} updated, {Skipped} skipped, {Failed} failed",
            report.TotalLines, report.Imported, report.Updated, report.Skipped, report.Failed);

    private static async Task ProcessLineAsync(
        int lineNumber, string line, NdjsonUserImportOptions options, NdjsonUserImportStores stores,
        NdjsonUserImportReport report, DateTimeOffset now, CancellationToken ct)
    {
        var parsed = NdjsonUserImportReader.ParseLine(line, options.AllowUnknownFields);
        if (!parsed.IsSuccess)
        {
            RecordFailure(report, lineNumber, parsed.Error!);
            return;
        }

        var record = parsed.Record!;
        var email = record.Email!.Trim();
        var existing = await stores.Users.FindByEmailAsync(email, ct).ConfigureAwait(false);

        if (existing is null)
        {
            if (!options.DryRun)
            {
                var user = NdjsonUserMapper.ToNewAuthUser(record, now);
                await stores.Users.CreateAsync(user, ct).ConfigureAwait(false);
            }

            report.Imported++;
            return;
        }

        switch (options.OnDuplicate)
        {
            case OnDuplicateAction.Skip:
                report.Skipped++;
                break;

            case OnDuplicateAction.Update:
                if (!options.DryRun)
                {
                    NdjsonUserMapper.ApplyToExisting(existing, record, now);
                    await stores.Users.UpdateAsync(existing, ct).ConfigureAwait(false);
                }

                report.Updated++;
                break;

            case OnDuplicateAction.Fail:
                throw new NdjsonDuplicateUserImportException(lineNumber, email);

            default:
                throw new InvalidOperationException($"Unhandled {nameof(OnDuplicateAction)}: {options.OnDuplicate}");
        }
    }

    private static void RecordFailure(NdjsonUserImportReport report, int lineNumber, string reason)
    {
        report.Failed++;
        if (report.Failures.Count < 20)
            report.Failures.Add(new NdjsonImportFailure(lineNumber, reason));
    }
}
