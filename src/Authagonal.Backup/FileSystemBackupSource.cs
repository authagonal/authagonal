using System.Text.Json;

namespace Authagonal.Backup;

/// <param name="tablePrefix">
/// The multi-tenant table prefix this instance reads backups for, or null for the common
/// single-tenant case. Must match whatever <see cref="FileSystemBackupTarget"/> was constructed
/// with for the same backups — see its constructor's <c>tablePrefix</c> remarks for why the nesting
/// exists. A CLI restore that derives its source root directly from an operator-supplied
/// <c>--input</c> path (see <c>tools/Authagonal.Restore</c>) already lands one directory below the
/// prefix segment and passes null here; a caller that keeps one prefix-agnostic root across many
/// tenants (as the OSS test suite and <c>tools/Authagonal.Backup</c> do) passes the same prefix it
/// gave the target.
/// </param>
public sealed class FileSystemBackupSource(string rootDirectory, string? tablePrefix = null) : IBackupSource
{
    /// <summary>
    /// <see cref="rootDirectory"/>, nested one level under <see cref="tablePrefix"/> when one is set —
    /// mirrors <see cref="FileSystemBackupTarget.BackupRoot"/> exactly, so a source constructed with
    /// the same root and prefix reads back what the matching target wrote.
    /// </summary>
    private string BackupRoot => string.IsNullOrEmpty(tablePrefix)
        ? rootDirectory
        : Path.Combine(rootDirectory, BackupPath.Safe(tablePrefix, nameof(tablePrefix)));

    public async Task<BackupManifest?> ReadManifestAsync(string backupId, CancellationToken ct = default)
    {
        var path = Path.Combine(BackupRoot, BackupPath.Safe(backupId, nameof(backupId)), "_manifest.json");
        if (!File.Exists(path)) return null;

        var json = await File.ReadAllTextAsync(path, ct);
        return JsonSerializer.Deserialize(json, BackupJsonContext.Default.BackupManifest);
    }

    public Task<Stream?> OpenReadAsync(string backupId, string fileName, CancellationToken ct = default)
    {
        var path = Path.Combine(BackupRoot, BackupPath.Safe(backupId, nameof(backupId)), BackupPath.Safe(fileName, nameof(fileName)));
        if (!File.Exists(path)) return Task.FromResult<Stream?>(null);

        return Task.FromResult<Stream?>(new FileStream(path, FileMode.Open, FileAccess.Read));
    }

    public Task<IReadOnlyList<string>> ListBackupIdsAsync(CancellationToken ct = default)
    {
        var root = BackupRoot;
        if (!Directory.Exists(root))
            return Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());

        var dirs = Directory.GetDirectories(root)
            .Select(Path.GetFileName)
            .Where(name => name is not null && !name.StartsWith("."))
            .OrderBy(name => name)
            .ToList();

        return Task.FromResult<IReadOnlyList<string>>(dirs!);
    }

    public Task<IReadOnlyList<string>> ListFilesAsync(string backupId, CancellationToken ct = default)
    {
        var dir = Path.Combine(BackupRoot, BackupPath.Safe(backupId, nameof(backupId)));
        if (!Directory.Exists(dir))
            return Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());

        var files = Directory.GetFiles(dir)
            .Select(Path.GetFileName)
            .Where(name => name is not null)
            .ToList();

        return Task.FromResult<IReadOnlyList<string>>(files!);
    }

    public Task DeleteBackupAsync(string backupId, CancellationToken ct = default)
    {
        var dir = Path.Combine(BackupRoot, BackupPath.Safe(backupId, nameof(backupId)));
        if (Directory.Exists(dir))
            Directory.Delete(dir, recursive: true);

        return Task.CompletedTask;
    }
}
