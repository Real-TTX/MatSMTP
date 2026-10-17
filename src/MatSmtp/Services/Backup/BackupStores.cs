using MatSmtp.Configuration;
using MatSmtp.Data.Entities;
using MatSmtp.Storage;

namespace MatSmtp.Services.Backup;

/// <summary>A place backups are written to: a folder, or a folder on a share of a NAS. A store knows names, nothing else.</summary>
public interface IBackupStore
{
    Task<IReadOnlyList<BackupFileInfo>> ListAsync(CancellationToken ct = default);

    Task WriteAsync(string name, Stream content, CancellationToken ct = default);

    Task<Stream> OpenReadAsync(string name, CancellationToken ct = default);

    Task DeleteAsync(string name, CancellationToken ct = default);

    /// <summary>Tries the target: is it there, can a file be written? The message says what is wrong in words.</summary>
    Task<(bool Ok, string Message)> CheckAsync(CancellationToken ct = default);
}

public sealed class LocalBackupStore(string folder) : IBackupStore
{
    public Task<IReadOnlyList<BackupFileInfo>> ListAsync(CancellationToken ct = default)
    {
        if (!Directory.Exists(folder))
        {
            return Task.FromResult<IReadOnlyList<BackupFileInfo>>([]);
        }

        IReadOnlyList<BackupFileInfo> list = [.. new DirectoryInfo(folder).EnumerateFiles().Where(f => BackupNames.IsBackup(f.Name))
            .Select(f => new BackupFileInfo(f.Name, f.LastWriteTimeUtc, f.Length))];
        return Task.FromResult(list);
    }

    public async Task WriteAsync(string name, Stream content, CancellationToken ct = default)
    {
        Directory.CreateDirectory(folder);
        var path = PathOf(name);
        var partial = path + ".partial";
        await using (var file = File.Create(partial))
        {
            await content.CopyToAsync(file, ct);
        }

        File.Move(partial, path, overwrite: true);
    }

    public Task<Stream> OpenReadAsync(string name, CancellationToken ct = default) => Task.FromResult<Stream>(File.OpenRead(PathOf(name)));

    public Task DeleteAsync(string name, CancellationToken ct = default)
    {
        File.Delete(PathOf(name));
        return Task.CompletedTask;
    }

    public async Task<(bool Ok, string Message)> CheckAsync(CancellationToken ct = default)
    {
        try
        {
            Directory.CreateDirectory(folder);
            var probe = Path.Combine(folder, ".ms-probe-" + Guid.NewGuid().ToString("N")[..8]);
            await File.WriteAllTextAsync(probe, "probe", ct);
            File.Delete(probe);
            var free = new DriveInfo(Path.GetFullPath(folder)).AvailableFreeSpace;
            return (true, $"Der Ordner „{folder}“ ist da und beschreibbar. Frei: {Fmt.FileSize(free)}.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return (false, $"Der Ordner „{folder}“ kann nicht benutzt werden: {ex.Message}");
        }
    }

    private string PathOf(string name)
    {
        if (!BackupNames.IsBackup(name) || name != Path.GetFileName(name))
        {
            throw new InvalidOperationException("Das ist kein Name einer Sicherung.");
        }

        return Path.Combine(folder, name);
    }
}

public sealed class SmbBackupStore(SmbShare share) : IBackupStore
{
    public async Task<IReadOnlyList<BackupFileInfo>> ListAsync(CancellationToken ct = default)
        => [.. (await share.ListAsync(string.Empty, ct)).Where(f => BackupNames.IsBackup(f.Name)).Select(f => new BackupFileInfo(f.Name, f.ModifiedUtc, f.Size))];

    public Task WriteAsync(string name, Stream content, CancellationToken ct = default) => share.WriteAsync(Checked(name), content, ct);

    public Task<Stream> OpenReadAsync(string name, CancellationToken ct = default) => share.OpenReadAsync(Checked(name), ct);

    public Task DeleteAsync(string name, CancellationToken ct = default) => share.DeleteAsync(Checked(name), ct);

    public async Task<(bool Ok, string Message)> CheckAsync(CancellationToken ct = default)
    {
        var check = await share.CheckAsync(ct);
        return (check.Ok, check.Ok && check.FreeBytes is { } free ? $"{check.Message} Frei: {Fmt.FileSize(free)}." : check.Message);
    }

    private static string Checked(string name)
        => BackupNames.IsBackup(name) && name == Path.GetFileName(name) ? name : throw new InvalidOperationException("Das ist kein Name einer Sicherung.");
}

/// <summary>File names of backups: <c>matsmtp-{plan}-{yyyyMMdd-HHmmss}.msbackup</c>, with <c>.enc</c> added when the file is encrypted.</summary>
public static class BackupNames
{
    public const string Extension = ".msbackup";

    public const string EncryptedExtension = ".msbackup.enc";

    public static bool IsBackup(string name) => name.StartsWith("matsmtp-", StringComparison.Ordinal) && (name.EndsWith(Extension, StringComparison.Ordinal) || name.EndsWith(EncryptedExtension, StringComparison.Ordinal));

    public static bool IsEncrypted(string name) => name.EndsWith(EncryptedExtension, StringComparison.Ordinal);

    public static string Make(string label, DateTime utc, bool encrypted)
    {
        var slug = new string([.. label.ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-')]).Trim('-');
        while (slug.Contains("--", StringComparison.Ordinal))
        {
            slug = slug.Replace("--", "-", StringComparison.Ordinal);
        }

        slug = slug.Length == 0 ? "sicherung" : slug.Length > 40 ? slug[..40].TrimEnd('-') : slug;
        return $"matsmtp-{slug}-{utc:yyyyMMdd-HHmmss}" + (encrypted ? EncryptedExtension : Extension);
    }

    /// <summary>The plan label of a file name made by <see cref="Make"/>, for the files of one plan; null when the name does not follow the pattern.</summary>
    public static string? LabelOf(string name)
    {
        if (!IsBackup(name))
        {
            return null;
        }

        var core = name["matsmtp-".Length..];
        core = core.EndsWith(EncryptedExtension, StringComparison.Ordinal) ? core[..^EncryptedExtension.Length] : core[..^Extension.Length];
        var cut = core.Length - "yyyyMMdd-HHmmss".Length - 1;
        return cut > 0 ? core[..cut] : null;
    }
}

/// <summary>Makes the store of a target (the settings of a <see cref="BackupTarget"/>).</summary>
public sealed class BackupStoreFactory(ConfigStore config, SecretProtector secrets)
{
    public IBackupStore Create(BackupTarget target) => target.Kind == BackupTargetKind.Local
        ? new LocalBackupStore(LocalFolder(target.Path))
        : new SmbBackupStore(new SmbShare(new SmbOptions(target.Host ?? "", target.Share ?? "", target.Path, target.Domain, target.UserName, secrets.Unprotect(target.PasswordProtected))));

    /// <summary>A relative folder is inside the data volume, so that "backups" ends up in the volume and not somewhere in the container.</summary>
    private string LocalFolder(string path) => Path.IsPathRooted(path) ? path : Path.Combine(config.DataDir, string.IsNullOrWhiteSpace(path) ? "backups" : path);
}
