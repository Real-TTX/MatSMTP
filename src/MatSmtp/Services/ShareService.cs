using MatSmtp.Configuration;
using MatSmtp.Data;
using MatSmtp.Data.Entities;
using MatSmtp.UiKit;
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

    /// <summary>The links of one message, the newest first.</summary>
    public Task<List<ShareLink>> ForMessageAsync(long messageId, CancellationToken ct = default)
        => db.ShareLink.AsNoTracking().Where(l => l.MessageId == messageId).OrderByDescending(l => l.Id).ToListAsync(ct);

    /// <summary>The list of the page Freigaben: search, filter, sort and page of the request; each link with its message.</summary>
    public Task<ListResult<ShareLink>> ListAsync(HttpRequest request, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var spec = new ListSpec<ShareLink>
        {
            Id = "shares",
            Action = "/Admin/Shares",
            Label = "Freigaben",
            Noun = "Freigaben",
            SearchPlaceholder = "Notiz, Betreff oder Empfänger ...",
            Search = (q, text) =>
            {
                var like = Like.Contains(text);
                return q.Where(l => EF.Functions.Like(l.Note ?? "", like, Like.Escape) || EF.Functions.Like(l.Message!.Subject ?? "", like, Like.Escape) || EF.Functions.Like(l.Message!.Recipients, like, Like.Escape));
            },
            Filters =
            [
                new FilterSpec<ShareLink>("status", "Status", [new("aktiv", "Gültig"), new("abgelaufen", "Abgelaufen oder widerrufen")],
                    (q, v) => v == "aktiv" ? q.Where(l => l.RevokedDate == null && l.ExpiresDate > now) : q.Where(l => l.RevokedDate != null || l.ExpiresDate <= now)),
            ],
            Sorts = new Dictionary<string, (string, Func<IQueryable<ShareLink>, bool, IOrderedQueryable<ShareLink>>)>
            {
                ["created"] = ("Angelegt", (q, d) => d ? q.OrderBy(l => l.CreateDate) : q.OrderByDescending(l => l.CreateDate)),
                ["expires"] = ("Gültig bis", (q, d) => d ? q.OrderByDescending(l => l.ExpiresDate) : q.OrderBy(l => l.ExpiresDate)),
                ["views"] = ("Aufrufe", (q, d) => d ? q.OrderBy(l => l.Views) : q.OrderByDescending(l => l.Views)),
            },
            DefaultSort = "created",
        };

        return ListBuilder.RunAsync(db.ShareLink.AsNoTracking().Include(l => l.Message), request, spec, ct);
    }

    public async Task<bool> RevokeAsync(long id, CancellationToken ct = default)
        => await db.ShareLink.Where(l => l.Id == id && l.RevokedDate == null).ExecuteUpdateAsync(s => s.SetProperty(l => l.RevokedDate, clock.GetUtcNow().UtcDateTime), ct) > 0;

    public static string Remaining(ShareLink link, DateTime now)
    {
        var span = link.ExpiresDate - now;
        return span.TotalDays >= 2 ? $"noch {(int)span.TotalDays} Tage" : span.TotalHours >= 1 ? $"noch {(int)span.TotalHours} Stunden" : "läuft bald ab";
    }
}
