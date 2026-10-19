using MatSmtp.Data;
using MatSmtp.Data.Entities;
using MatSmtp.UiKit;
using Microsoft.EntityFrameworkCore;

namespace MatSmtp.Services.Relay;

/// <summary>The webhooks as the pages edit them: list, one webhook, saving and deleting. Calling them is <see cref="WebhookService"/>'s.</summary>
public sealed class WebhookAdminService(MsDbContext db)
{
    /// <summary>The list of the page: search, filter, sort and page of the request.</summary>
    public Task<ListResult<Webhook>> ListAsync(HttpRequest request, CancellationToken ct)
    {
        var spec = new ListSpec<Webhook>
        {
            Id = "webhooks",
            Action = "/Webhooks",
            Label = "Webhooks",
            Noun = "Webhooks",
            SearchPlaceholder = "Name oder Adresse ...",
            Search = (q, text) =>
            {
                var like = Like.Contains(text);
                return q.Where(w => EF.Functions.Like(w.Name, like, Like.Escape) || EF.Functions.Like(w.Url, like, Like.Escape));
            },
            Filters =
            [
                new FilterSpec<Webhook>("status", "Status", [new("aktiv", "Aktiv"), new("aus", "Abgeschaltet"), new("fehler", "Letzter Aufruf fehlgeschlagen")],
                    (q, v) => v switch { "aus" => q.Where(w => !w.IsEnabled), "fehler" => q.Where(w => w.LastError != null), _ => q.Where(w => w.IsEnabled) }),
            ],
            Sorts = new Dictionary<string, (string, Func<IQueryable<Webhook>, bool, IOrderedQueryable<Webhook>>)>
            {
                ["name"] = ("Name", (q, d) => d ? q.OrderByDescending(w => w.Name) : q.OrderBy(w => w.Name)),
                ["called"] = ("Zuletzt aufgerufen", (q, d) => d ? q.OrderByDescending(w => w.LastCallDate) : q.OrderBy(w => w.LastCallDate)),
            },
            DefaultSort = "name",
        };

        return ListBuilder.RunAsync(db.Webhook.AsNoTracking(), request, spec, ct);
    }

    public Task<Webhook?> FindAsync(long id, CancellationToken ct = default) => db.Webhook.AsNoTracking().FirstOrDefaultAsync(w => w.Id == id, ct);

    /// <summary>The webhooks for a select (rules), by name.</summary>
    public Task<List<Webhook>> AllAsync(CancellationToken ct = default) => db.Webhook.AsNoTracking().OrderBy(w => w.Name).ToListAsync(ct);

    /// <summary>How many rules call this webhook.</summary>
    public Task<int> CountRulesAsync(long id, CancellationToken ct = default) => db.Rule.CountAsync(r => r.WebhookId == id, ct);

    /// <summary>Creates (<paramref name="id"/> null) or changes a webhook with <paramref name="apply"/>.</summary>
    public async Task<Webhook> SaveAsync(long? id, Action<Webhook> apply, CancellationToken ct = default)
    {
        var hook = id is { } existing ? await db.Webhook.FirstAsync(w => w.Id == existing, ct) : db.Webhook.Add(new Webhook()).Entity;
        apply(hook);
        await db.SaveChangesAsync(ct);
        return hook;
    }

    /// <summary>Deletes a webhook (softly); null when there is none.</summary>
    public async Task<Webhook?> DeleteAsync(long id, CancellationToken ct = default)
    {
        var hook = await db.Webhook.FirstOrDefaultAsync(w => w.Id == id, ct);
        if (hook is null)
        {
            return null;
        }

        db.Webhook.Remove(hook);
        await db.SaveChangesAsync(ct);
        return hook;
    }
}
