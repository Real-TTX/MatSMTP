using MatSmtp.Configuration;

namespace MatSmtp.Services.Relay;

/// <summary>
/// The content of the messages: one .eml file per message in <c>{data}/spool/yyyy/MM/dd/</c>. The database only knows the key (the relative path). A key that
/// comes from the database is checked before it becomes a path, so a damaged row can never point outside the folder.
/// </summary>
public sealed class SpoolStore(ConfigStore config)
{
    public string Root => config.SpoolDir;

    /// <summary>Writes the bytes of a new message and returns its key. The file is written under another name first, so a half written file is never seen.</summary>
    public async Task<string> SaveAsync(ReadOnlyMemory<byte> content, DateTime receivedUtc, CancellationToken ct = default)
    {
        var key = $"{receivedUtc:yyyy}/{receivedUtc:MM}/{receivedUtc:dd}/{Guid.NewGuid():N}.eml";
        var path = PathOf(key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var partial = path + ".partial";
        await File.WriteAllBytesAsync(partial, content.ToArray(), ct);
        File.Move(partial, path, overwrite: true);
        return key;
    }

    /// <summary>Puts a message back under its old key (a restore).</summary>
    public async Task RestoreAsync(string key, Stream content, CancellationToken ct = default)
    {
        var path = PathOf(key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var file = File.Create(path);
        await content.CopyToAsync(file, ct);
    }

    public Stream OpenRead(string key) => File.OpenRead(PathOf(key));

    public Task<byte[]> ReadAllAsync(string key, CancellationToken ct = default) => File.ReadAllBytesAsync(PathOf(key), ct);

    public bool Exists(string? key) => key is not null && File.Exists(PathOf(key));

    public void Delete(string? key)
    {
        if (key is null)
        {
            return;
        }

        try
        {
            File.Delete(PathOf(key));
        }
        catch (IOException)
        {
            // gone already or in use: the maintenance tries again
        }
    }

    /// <summary>How much the spool folder holds (files and bytes), for the system page.</summary>
    public (int Files, long Bytes) Size()
    {
        if (!Directory.Exists(Root))
        {
            return (0, 0);
        }

        var files = new DirectoryInfo(Root).EnumerateFiles("*.eml", SearchOption.AllDirectories).ToList();
        return (files.Count, files.Sum(f => f.Length));
    }

    /// <summary>Removes empty day folders (after the maintenance deleted their messages).</summary>
    public void PruneEmptyFolders()
    {
        if (!Directory.Exists(Root))
        {
            return;
        }

        foreach (var dir in Directory.EnumerateDirectories(Root, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length))
        {
            try
            {
                if (!Directory.EnumerateFileSystemEntries(dir).Any())
                {
                    Directory.Delete(dir);
                }
            }
            catch (IOException)
            {
            }
        }
    }

    private string PathOf(string key)
    {
        var root = Path.GetFullPath(Root);
        var full = Path.GetFullPath(Path.Combine(root, key));
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal) || !full.EndsWith(".eml", StringComparison.Ordinal))
        {
            throw new IOException("Der Speicherort der Nachricht ist ungültig.");
        }

        return full;
    }
}
