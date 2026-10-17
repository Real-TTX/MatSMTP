using MatSmtp.Configuration;
using MatSmtp.Data;
using MatSmtp.Data.Entities;
using MatSmtp.Services.Relay;
using Microsoft.EntityFrameworkCore;

namespace MatSmtp.Services;

/// <summary>What was cleaned up: how many rows and files of each kind were removed.</summary>
public sealed record MaintenanceResult(int Sessions, int ActivityEntries, int BackupRuns, int ExpiredLinks, int Messages, int Bodies)
{
    public int Total => Sessions + ActivityEntries + BackupRuns + ExpiredLinks + Messages + Bodies;
}

/// <summary>
/// Keeps the tables and the spool folder small: the content of delivered messages after the days of the settings, the log of messages after its days, sessions
/// and share links that ended long ago, the logs after a year. Runs every hour (and on request on the system page). It never touches a message that still waits
/// in the queue.
/// </summary>
public sealed class MaintenanceService(IServiceScopeFactory scopes, ILogger<MaintenanceService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    using var scope = scopes.CreateScope();
                    var sp = scope.ServiceProvider;
                    var result = await RunAsync(sp.GetRequiredService<MsDbContext>(), sp.GetRequiredService<SpoolStore>(), sp.GetRequiredService<ConfigStore>(), sp.GetRequiredService<TimeProvider>(), stoppingToken);
                    if (result.Total > 0)
                    {
                        logger.LogInformation("Maintenance removed {Total} old rows and files.", result.Total);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "The maintenance run failed.");
                }

                await Task.Delay(Interval, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // the application is stopping
        }
    }

    public static async Task<MaintenanceResult> RunAsync(MsDbContext db, SpoolStore spool, ConfigStore config, TimeProvider clock, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var delivery = config.Current.Delivery;
        var month = now.AddDays(-30);
        var year = now.AddDays(-365);
        var final = new[] { MessageStatus.Sent, MessageStatus.Failed, MessageStatus.Rejected, MessageStatus.Dropped, MessageStatus.Delivered };

        // The content of finished messages goes after KeepBodyDays (a failed one stays as long as the message, so it can be sent again).
        var bodyLimit = now.AddDays(-Math.Max(0, delivery.KeepBodyDays));
        var withBody = await db.Message
            .Where(m => m.StorageKey != null && (m.Status == MessageStatus.Sent || m.Status == MessageStatus.Delivered || m.Status == MessageStatus.Dropped) && m.ReceivedDate < bodyLimit)
            .Select(m => new { m.Id, m.StorageKey }).Take(5000).ToListAsync(ct);
        foreach (var m in withBody)
        {
            spool.Delete(m.StorageKey);
        }

        var ids = withBody.Select(m => m.Id).ToList();
        var bodies = ids.Count == 0 ? 0 : await db.Message.Where(m => ids.Contains(m.Id)).ExecuteUpdateAsync(s => s.SetProperty(m => m.StorageKey, (string?)null), ct);

        // The log of messages after KeepDays: the content goes with it.
        var logLimit = now.AddDays(-Math.Max(1, delivery.KeepDays));
        var old = await db.Message.Where(m => final.Contains(m.Status) && m.ReceivedDate < logLimit).Select(m => new { m.Id, m.StorageKey }).Take(5000).ToListAsync(ct);
        foreach (var m in old)
        {
            spool.Delete(m.StorageKey);
        }

        var oldIds = old.Select(m => m.Id).ToList();
        var messages = oldIds.Count == 0 ? 0 : await db.Message.Where(m => oldIds.Contains(m.Id)).ExecuteDeleteAsync(ct);
        if (bodies + messages > 0)
        {
            spool.PruneEmptyFolders();
        }

        var sessions = await db.UserSession.Where(s => s.ExpiresDate < month || (s.RevokedDate != null && s.RevokedDate < month)).ExecuteDeleteAsync(ct);
        var activity = await db.ActivityLog.Where(a => a.OccurredDate < year).ExecuteDeleteAsync(ct);
        var runs = await db.BackupRun.Where(r => r.EndDate < year).ExecuteDeleteAsync(ct);
        var links = await db.ShareLink.Where(l => l.ExpiresDate < month || (l.RevokedDate != null && l.RevokedDate < month)).ExecuteDeleteAsync(ct);
        return new MaintenanceResult(sessions, activity, runs, links, messages, bodies);
    }
}
