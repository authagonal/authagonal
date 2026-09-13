using System.Text.Json;

namespace Authagonal.Backup;

/// <param name="tablePrefix">
/// The multi-tenant table prefix this instance writes backups for, or null for the common
/// single-tenant case. Nests every backupId-keyed path (data files, the manifest) one directory
/// level under it — <c>{rootDirectory}/{tablePrefix}/{backupId}/...</c> — mirroring how
/// <c>BlobBackupTarget</c> already nests every path under its constructor-supplied tenant slug.
/// <para>
/// Without it, <c>backupId</c> alone named the directory: a bare <c>yyyyMMdd-HHmmss</c> timestamp
/// with one-second resolution and no prefix in it. Two DIFFERENT prefixes writing into the same
/// <c>rootDirectory</c> within the same wall-clock second — the documented purpose of
/// <c>--prefix</c> is exactly running more than one tenant into one target — got the identical
/// directory, so tenant B's manifest and data files could land on top of tenant A's.
/// </para>
/// <para>
/// The watermark and chain-root files below are NOT nested under it: those already carry their own
/// <c>scope</c> parameter (a digest of the prefix and table set, from
/// <see cref="BackupOptions.WatermarkScope"/>), which has isolated them correctly since it was
/// added. Only the backupId-keyed paths lacked an equivalent, since <see cref="IBackupTarget"/>
/// has no scope parameter for those — the constructor is where <c>BlobBackupTarget</c> supplies it,
/// so that is where this does too.
/// </para>
/// </param>
public sealed class FileSystemBackupTarget(string rootDirectory, string? tablePrefix = null) : IBackupTarget
{
    /// <summary>
    /// Owner-only, on both the directory and every file inside it.
    /// </summary>
    /// <remarks>
    /// These files were created with the process umask, which on a typical host means world-readable.
    /// A backup is not ordinary data: it carries MFA TOTP seeds — directly replayable second factors —
    /// alongside every password hash, client secret hash and recovery-code hash in the deployment, all
    /// offline-crackable at the attacker's leisure. Anyone with a shell on the box could read the lot
    /// without touching the identity provider or leaving a trace in it.
    /// <para>
    /// Not encryption. Envelope encryption of the archive is the real answer and is a format change;
    /// this is the part that costs nothing and removes the most common way these files get read.
    /// </para>
    /// </remarks>
    private const UnixFileMode OwnerOnlyFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private const UnixFileMode OwnerOnlyDirectory =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    /// <summary>
    /// <see cref="rootDirectory"/>, nested one level under <see cref="tablePrefix"/> when one is set.
    /// The base for every backupId-keyed path; unaffected watermark/chain-root paths use
    /// <see cref="rootDirectory"/> directly (see the constructor's <c>tablePrefix</c> remarks).
    /// </summary>
    private string BackupRoot => string.IsNullOrEmpty(tablePrefix)
        ? rootDirectory
        : Path.Combine(rootDirectory, BackupPath.Safe(tablePrefix, nameof(tablePrefix)));

    private static string EnsureDirectory(string root, string backupId)
    {
        var dir = Path.Combine(root, BackupPath.Safe(backupId, nameof(backupId)));
        if (!Directory.Exists(dir))
        {
            // Set at creation rather than after: a chmod that follows the mkdir leaves a window in
            // which the directory is readable.
            if (OperatingSystem.IsWindows()) Directory.CreateDirectory(dir);
            else Directory.CreateDirectory(dir, OwnerOnlyDirectory);
        }
        return dir;
    }

    private static Stream CreateFile(string path)
    {
        if (OperatingSystem.IsWindows())
            return new FileStream(path, FileMode.Create, FileAccess.Write);

        // Same reasoning: the mode is part of the create, so the file is never briefly world-readable.
        return new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.Create,
            Access = FileAccess.Write,
            UnixCreateMode = OwnerOnlyFile,
        });
    }

    public Task<Stream> OpenWriteAsync(string backupId, string fileName, CancellationToken ct = default)
    {
        var dir = EnsureDirectory(BackupRoot, backupId);
        var stream = CreateFile(Path.Combine(dir, BackupPath.Safe(fileName, nameof(fileName))));
        return Task.FromResult(stream);
    }

    public async Task WriteManifestAsync(string backupId, BackupManifest manifest, CancellationToken ct = default)
    {
        var dir = EnsureDirectory(BackupRoot, backupId);
        var json = JsonSerializer.Serialize(manifest, BackupJsonContext.Default.BackupManifest);

        // Through the same owner-only create as the data files. The manifest carries the file hashes
        // and, when configured, their MAC — writing it world-readable would hand an attacker the
        // integrity metadata for the archive it sits beside.
        await using var stream = CreateFile(Path.Combine(dir, "_manifest.json"));
        await using var writer = new StreamWriter(stream, System.Text.Encoding.UTF8);
        await writer.WriteAsync(json.AsMemory(), ct);
    }

    /// <summary>
    /// The watermark file for a scope. Unscoped runs keep <c>.lastbackup</c>, so a target written by an
    /// earlier version is read by this one and an unscoped incremental is unaffected.
    /// </summary>
    private string WatermarkPath(string? scope) =>
        Path.Combine(rootDirectory, scope is null ? ".lastbackup" : $".lastbackup-{scope}");

    public Task<DateTimeOffset?> GetLastWatermarkAsync(CancellationToken ct = default, string? scope = null)
    {
        var path = WatermarkPath(scope);

        // A scoped run with no watermark of its own falls back to the unscoped file rather than to "no
        // watermark at all": a deployment upgrading mid-schedule would otherwise take one full backup per
        // scope, which is correct but surprising. A scope that has never run reads null from both and takes a
        // full backup, which is the behaviour the multi-tenant case needed.
        if (!File.Exists(path) && scope is not null)
            path = WatermarkPath(null);

        if (!File.Exists(path))
            return Task.FromResult<DateTimeOffset?>(null);

        var text = File.ReadAllText(path).Trim();
        if (DateTimeOffset.TryParse(text, out var parsed))
            return Task.FromResult<DateTimeOffset?>(parsed);

        return Task.FromResult<DateTimeOffset?>(null);
    }

    public async Task SetLastWatermarkAsync(
        DateTimeOffset watermark, CancellationToken ct = default, string? scope = null)
    {
        Directory.CreateDirectory(rootDirectory);
        await File.WriteAllTextAsync(WatermarkPath(scope), watermark.ToString("O"), ct);
    }

    /// <summary>
    /// A file of its own rather than a second line in the watermark file, which is read as a bare
    /// timestamp — anything appended to it parses as nothing and silently disables incrementals.
    /// </summary>
    private string ChainRootPath(string? scope) =>
        Path.Combine(rootDirectory, scope is null ? ".lastfull" : $".lastfull-{scope}");

    public Task<string?> GetLastFullBackupIdAsync(CancellationToken ct = default, string? scope = null)
    {
        var path = ChainRootPath(scope);

        // Same fallback as the watermark, for the same reason: a target written before this file existed,
        // or by an unscoped run, still names a parent rather than reverting to "unknown".
        if (!File.Exists(path) && scope is not null)
            path = ChainRootPath(null);

        if (!File.Exists(path))
            return Task.FromResult<string?>(null);

        var text = File.ReadAllText(path).Trim();
        return Task.FromResult(string.IsNullOrEmpty(text) ? null : text);
    }

    public async Task SetLastFullBackupIdAsync(
        string backupId, CancellationToken ct = default, string? scope = null)
    {
        Directory.CreateDirectory(rootDirectory);
        await File.WriteAllTextAsync(ChainRootPath(scope), backupId, ct);
    }
}
