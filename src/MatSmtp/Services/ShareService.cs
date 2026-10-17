using MatSmtp.Configuration;
using MatSmtp.Data;
using MatSmtp.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace MatSmtp.Services;

public enum ShareState
{
    Active,
    Expired,
    Revoked,
    Unknown,
}

public sealed record ShareResolution(ShareState State, ShareLink? Link);

/// <summary>
/// Anonymous links to the delivery of one message (the role "Anonym"): the sender of a mail can see whether and when it went out, without an account. The page shows
/// the envelope, the subject, the status and the history, never the content or the address of the client. A link runs out after its days or when it is revoked;
/// its token is a random UUID, so it cannot be guessed.
/// </summary>
public sealed class ShareService(MsDbContext db, ConfigStore config, TimeProvider clock, PublicUrl urls)
{
    public string LinkOf(ShareLink link) => urls.Absolute($"/s/{link.Token}");

    public int DefaultDays => config.Current.Security.ShareLinkDays;

    public async Task<ShareLink> CreateAsync(long messageId, int days, string? note, CancellationToken ct = default)
    {
        var link = new ShareLink
        {
            MessageId = messageId,
            Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim(),
            ExpiresDate = clock.GetUtcNow().UtcDateTime.AddDays(Math.Clamp(days, 1, 365)),
        };
        db.ShareLink.Add(link);
        await db.SaveChangesAsync(ct);
        return link;
    }

    public async Task<ShareResolution> ResolveAsync(Guid token, CancellationToken ct = default)
    {
        var link = await db.ShareLink.Include(l => l.Message).ThenInclude(m => m!.Events).FirstOrDefaultAsync(l => l.Token == token, ct);
        if (link?.Message is null)
        {
            return new ShareResolution(ShareState.Unknown, null);
        }

        var now = clock.GetUtcNow().UtcDateTime;
        var state = link.RevokedDate is not null ? ShareState.Revoked : link.ExpiresDate <= now ? ShareState.Expired : ShareState.Active;
        return new ShareResolution(state, link);
    }

    public async Task CountViewAsync(ShareLink link, CancellationToken ct = default)
    {
        link.Views++;
        link.LastViewDate = clock.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct);
    }

    public async Task<bool> RevokeAsync(long id, CancellationToken ct = default)
        => await db.ShareLink.Where(l => l.Id == id && l.RevokedDate == null).ExecuteUpdateAsync(s => s.SetProperty(l => l.RevokedDate, clock.GetUtcNow().UtcDateTime), ct) > 0;

    public static string Remaining(ShareLink link, DateTime now)
    {
        var span = link.ExpiresDate - now;
        return span.TotalDays >= 2 ? $"noch {(int)span.TotalDays} Tage" : span.TotalHours >= 1 ? $"noch {(int)span.TotalHours} Stunden" : "läuft bald ab";
    }
}
