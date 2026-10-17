using System.Collections.Concurrent;
using MatSmtp.Data;
using MatSmtp.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace MatSmtp.Services.Auth;

/// <summary>What a request needs to know about a session: is it still valid, and who is behind it.</summary>
public sealed record SessionSnapshot(long UserId, string DisplayName, string Email, UserRole Role, string Theme, bool HasTotp);

/// <summary>
/// The cookie carries only the token of a <see cref="UserSession"/>; every request checks that the row is still valid. The check is cached
/// for a few seconds so that a page with many requests does not ask the database every time. Ending a session or changing a user calls
/// <see cref="InvalidateUser"/>, which makes every cached answer for that user stale at once.
/// </summary>
public sealed class SessionCache(IMemoryCache cache, TimeProvider clock)
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(15);

    private readonly ConcurrentDictionary<long, long> _epochs = new();

    private long _all;

    private sealed record Entry(SessionSnapshot? Snapshot, long Epoch, long All);

    public void InvalidateUser(long userId) => _epochs.AddOrUpdate(userId, 1, (_, old) => old + 1);

    /// <summary>Makes every cached answer stale: after a restore the sessions in the database are not the ones that were checked before.</summary>
    public void InvalidateAll() => Interlocked.Increment(ref _all);

    public async Task<SessionSnapshot?> GetAsync(Guid token, MsDbContext db, CancellationToken ct)
    {
        if (cache.TryGetValue(token, out Entry? entry) && entry is not null)
        {
            if (entry.All == Interlocked.Read(ref _all) && (entry.Snapshot is null || _epochs.GetValueOrDefault(entry.Snapshot.UserId) == entry.Epoch))
            {
                return entry.Snapshot;
            }
        }

        var now = clock.GetUtcNow().UtcDateTime;
        var snapshot = await db.UserSession.AsNoTracking()
            .Where(s => s.Token == token && s.RevokedDate == null && s.ExpiresDate > now && s.User!.IsActive)
            .Select(s => new SessionSnapshot(s.UserId, s.User!.DisplayName, s.User.Email, s.User.Role, s.User.Theme, s.User.Totp != null && s.User.Totp.IsConfirmed))
            .FirstOrDefaultAsync(ct);

        var epoch = snapshot is null ? 0 : _epochs.GetValueOrDefault(snapshot.UserId);
        cache.Set(token, new Entry(snapshot, epoch, Interlocked.Read(ref _all)), Lifetime);
        return snapshot;
    }
}
