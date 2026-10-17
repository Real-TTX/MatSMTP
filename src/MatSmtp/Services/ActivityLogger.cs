using MatSmtp.Data;
using MatSmtp.Data.Entities;

namespace MatSmtp.Services;

/// <summary>
/// The operational log. It writes through a scope of its own, so a half-finished save of the caller is never part of it, and it never throws:
/// a log that breaks the action it describes does more harm than good.
/// </summary>
public sealed class ActivityLogger(IServiceScopeFactory scopes, ICurrentUser user, TimeProvider clock, ILogger<ActivityLogger> logger)
{
    public async Task LogAsync(string action, string? entityType = null, long? entityId = null, string? summary = null)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MsDbContext>();
            db.ActivityLog.Add(new ActivityLog
            {
                OccurredDate = clock.GetUtcNow().UtcDateTime,
                UserId = user.UserId,
                UserName = user.Name,
                Action = action,
                EntityType = entityType,
                EntityId = entityId,
                Summary = summary is { Length: > 500 } ? summary[..500] : summary,
                IpAddress = user.IpAddress,
            });
            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "The activity log could not be written.");
        }
    }
}
