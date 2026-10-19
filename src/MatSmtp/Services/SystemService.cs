using MatSmtp.Configuration;
using MatSmtp.Data;
using MatSmtp.Services.Relay;
using Microsoft.EntityFrameworkCore;

namespace MatSmtp.Services;

/// <summary>What the database says about itself: SQLite version, size of the file.</summary>
public sealed record DatabaseInfo(string? Version, long Bytes, string Path);

/// <summary>What the system page shows about the database and the spool folder, and the maintenance it can start. The page itself never touches the database.</summary>
public sealed class SystemService(MsDbContext db, SpoolStore spool, ConfigStore config, TimeProvider clock)
{
    public async Task<DatabaseInfo> GetDatabaseAsync(CancellationToken ct)
    {
        var connection = db.Database.GetDbConnection();
        await connection.OpenAsync(ct);
        try
        {
            await using var version = connection.CreateCommand();
            version.CommandText = "SELECT sqlite_version()";
            var text = "SQLite " + (string?)await version.ExecuteScalarAsync(ct);
            var bytes = new[] { config.DatabasePath, config.DatabasePath + "-wal" }.Where(File.Exists).Sum(f => new FileInfo(f).Length);
            return new DatabaseInfo(text, bytes, config.DatabasePath);
        }
        finally
        {
            await connection.CloseAsync();
        }
    }

    /// <summary>Whether the database answers (the plain check <c>/health</c>).</summary>
    public Task<bool> CanConnectAsync(CancellationToken ct = default) => db.Database.CanConnectAsync(ct);

    public (int Files, long Bytes) SpoolSize() => spool.Size();

    /// <summary>How many rows the main tables hold, in the order the page lists them.</summary>
    public async Task<Dictionary<string, int>> CountRowsAsync(CancellationToken ct) => new()
    {
        ["Benutzer"] = await db.User.CountAsync(ct),
        ["SMTP-Konten"] = await db.SmtpAccount.CountAsync(ct),
        ["Zugänge"] = await db.RelayUser.CountAsync(ct),
        ["Regeln"] = await db.Rule.CountAsync(ct),
        ["Webhooks"] = await db.Webhook.CountAsync(ct),
        ["Nachrichten"] = await db.Message.CountAsync(ct),
        ["Verlaufseinträge"] = await db.MessageEvent.CountAsync(ct),
        ["Freigabe-Links"] = await db.ShareLink.CountAsync(ct),
    };

    /// <summary>Removes old messages, sessions, run-out links and old logs now (the hourly maintenance does the same).</summary>
    public Task<MaintenanceResult> MaintainAsync(CancellationToken ct) => MaintenanceService.RunAsync(db, spool, config, clock, ct);

    /// <summary>Gives the free pages of the database file back to the disk (after many messages were removed).</summary>
    public Task VacuumAsync(CancellationToken ct) => db.Database.ExecuteSqlRawAsync("VACUUM;", ct);
}
