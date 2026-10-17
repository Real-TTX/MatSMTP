using MatSmtp.Configuration;
using MatSmtp.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace MatSmtp.Services;

/// <summary>
/// Creates the SQLite file when it is missing, switches it to WAL (the receiver, the spooler and the pages read while another one writes) and applies the
/// migrations. The stack therefore needs no setup script and no database server: one container, one volume.
/// </summary>
public static class DatabaseInitializer
{
    public static async Task InitializeAsync(IServiceProvider services, ConfigStore config, ILogger logger, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(config.DatabasePath)!);
        await using (var connection = new SqliteConnection(config.ConnectionString()))
        {
            await connection.OpenAsync(ct);
            await using var wal = connection.CreateCommand();
            wal.CommandText = "PRAGMA journal_mode=WAL;";
            await wal.ExecuteScalarAsync(ct);
        }

        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MsDbContext>();
        var pending = (await db.Database.GetPendingMigrationsAsync(ct)).ToList();
        if (pending.Count > 0)
        {
            logger.LogInformation("Applying {Count} migration(s) to {Path}.", pending.Count, config.DatabasePath);
        }

        await db.Database.MigrateAsync(ct);
    }
}
