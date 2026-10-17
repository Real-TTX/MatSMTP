using System.IO.Compression;
using MatSmtp.Configuration;
using Microsoft.Data.Sqlite;

namespace MatSmtp.Services.Backup;

/// <summary>A table in a backup: its columns, how many rows, and where its data is in the archive (the one database file for all tables).</summary>
public sealed record TableInfo(string Name, string[] Columns, long Rows, string Entry);

/// <summary>
/// The database in a backup: a consistent copy of the SQLite file made with the online backup of SQLite (the receiver and the spooler go on writing meanwhile),
/// as one entry of the zip. Restoring copies the file of the backup back into the live database with the same backup function, page by page under a lock:
/// either the whole database is the one of the backup, or nothing changed.
/// </summary>
public sealed class DatabaseArchive(ConfigStore config)
{
    public const string Entry = "database/matsmtp.db";

    private const string History = "__EFMigrationsHistory";

    public static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    public async Task<List<TableInfo>> ExportAsync(ZipArchive zip, CancellationToken ct)
    {
        var temp = Path.Combine(TempDirectory(), $"ms-db-{Guid.NewGuid():N}.db");
        try
        {
            await using (var live = new SqliteConnection(config.ConnectionString()))
            await using (var copy = new SqliteConnection(config.ConnectionString(temp)))
            {
                await live.OpenAsync(ct);
                await copy.OpenAsync(ct);
                live.BackupDatabase(copy);
            }

            List<TableInfo> tables;
            await using (var copy = new SqliteConnection(config.ConnectionString(temp)))
            {
                await copy.OpenAsync(ct);
                tables = await ListTablesAsync(copy, ct);
            }

            SqliteConnection.ClearAllPools();
            zip.CreateEntryFromFile(temp, Entry, CompressionLevel.Optimal);
            return tables;
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            TryDelete(temp);
        }
    }

    /// <summary>The newest migration the database has applied, for the check that a backup is not from a newer version than the application.</summary>
    public async Task<string?> LatestMigrationAsync(CancellationToken ct) => await LatestMigrationAsync(config.ConnectionString(), ct);

    /// <summary>Replaces the database with the one of the backup. Throws without changing anything if it is not a database of MatSMTP or from a newer version.</summary>
    public async Task RestoreAsync(ZipArchive zip, IReadOnlyList<TableInfo> tables, CancellationToken ct)
    {
        var entry = zip.GetEntry(Entry) ?? throw new InvalidOperationException($"Die Sicherung ist unvollständig: {Entry} fehlt.");
        var temp = Path.Combine(TempDirectory(), $"ms-restore-db-{Guid.NewGuid():N}.db");
        try
        {
            entry.ExtractToFile(temp);
            await using (var source = new SqliteConnection(config.ConnectionString(temp)))
            {
                await source.OpenAsync(ct);
                await using var check = source.CreateCommand();
                check.CommandText = "PRAGMA integrity_check;";
                if (await check.ExecuteScalarAsync(ct) is not "ok")
                {
                    throw new InvalidOperationException("Die Datenbank in der Sicherung ist beschädigt.");
                }

                var names = (await ListTablesAsync(source, ct)).Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
                if (!names.Contains("User") || !names.Contains("Message"))
                {
                    throw new InvalidOperationException("Die Datei enthält keine Datenbank von MatSMTP.");
                }

                await using var live = new SqliteConnection(config.ConnectionString());
                await live.OpenAsync(ct);
                source.BackupDatabase(live);
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            TryDelete(temp);
        }
    }

    private static async Task<string?> LatestMigrationAsync(string connectionString, CancellationToken ct)
    {
        await using var conn = new SqliteConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT max(\"MigrationId\") FROM {Quote(History)}";
        return await cmd.ExecuteScalarAsync(ct) as string;
    }

    private static async Task<List<TableInfo>> ListTablesAsync(SqliteConnection conn, CancellationToken ct)
    {
        var names = new List<string>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' AND name <> '__EFMigrationsHistory' ORDER BY name";
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                names.Add(reader.GetString(0));
            }
        }

        var result = new List<TableInfo>();
        foreach (var name in names)
        {
            var columns = new List<string>();
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = $"PRAGMA table_info({Quote(name)})";
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    columns.Add(reader.GetString(1));
                }
            }

            await using var count = conn.CreateCommand();
            count.CommandText = $"SELECT count(*) FROM {Quote(name)}";
            result.Add(new TableInfo(name, [.. columns], Convert.ToInt64(await count.ExecuteScalarAsync(ct)), Entry));
        }

        return result;
    }

    private string TempDirectory()
    {
        var configured = config.Current.Backup.TempDirectory;
        var dir = string.IsNullOrWhiteSpace(configured) ? Path.Combine(config.DataDir, "tmp") : configured;
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void TryDelete(string path)
    {
        foreach (var file in new[] { path, path + "-wal", path + "-shm" })
        {
            try
            {
                File.Delete(file);
            }
            catch (IOException)
            {
            }
        }
    }
}
