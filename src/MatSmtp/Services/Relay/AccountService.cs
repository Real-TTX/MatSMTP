using MatSmtp.Data;
using MatSmtp.Data.Entities;
using MatSmtp.UiKit;
using Microsoft.EntityFrameworkCore;

namespace MatSmtp.Services.Relay;

/// <summary>
/// The SMTP accounts the relay sends through: the list and the edit page of the accounts and the platform settings (the default account) read and write
/// them here. There is one default account: saving one as the default takes the mark from the others.
/// </summary>
public sealed class AccountService(MsDbContext db)
{
    /// <summary>The name of the default account that the platform settings create when there is none.</summary>
    public const string DefaultName = "Standard";

    /// <summary>The list of the page: search, filter, sort and page of the request.</summary>
    public Task<ListResult<SmtpAccount>> ListAsync(HttpRequest request, CancellationToken ct)
    {
        var spec = new ListSpec<SmtpAccount>
        {
            Id = "accounts",
            Action = "/Accounts",
            Label = "SMTP-Konten",
            Noun = "Konten",
            SearchPlaceholder = "Name, Server oder Benutzer ...",
            Search = (q, text) =>
            {
                var like = Like.Contains(text);
                return q.Where(a => EF.Functions.Like(a.Name, like, Like.Escape) || EF.Functions.Like(a.Host, like, Like.Escape) || EF.Functions.Like(a.UserName ?? "", like, Like.Escape));
            },
            Filters =
            [
                new FilterSpec<SmtpAccount>("status", "Status", [new("aktiv", "Aktiv"), new("aus", "Abgeschaltet"), new("fehler", "Mit Fehler")],
                    (q, v) => v switch
                    {
                        "aus" => q.Where(a => !a.IsEnabled),
                        "fehler" => q.Where(a => a.LastErrorDate != null && (a.LastUsedDate == null || a.LastErrorDate > a.LastUsedDate)),
                        _ => q.Where(a => a.IsEnabled),
                    }),
            ],
            Sorts = new Dictionary<string, (string, Func<IQueryable<SmtpAccount>, bool, IOrderedQueryable<SmtpAccount>>)>
            {
                ["name"] = ("Name", (q, d) => d ? q.OrderByDescending(a => a.Name) : q.OrderBy(a => a.Name)),
                ["host"] = ("Server", (q, d) => d ? q.OrderByDescending(a => a.Host) : q.OrderBy(a => a.Host)),
                ["used"] = ("Zuletzt benutzt", (q, d) => d ? q.OrderByDescending(a => a.LastUsedDate) : q.OrderBy(a => a.LastUsedDate)),
            },
            DefaultSort = "name",
        };

        return ListBuilder.RunAsync(db.SmtpAccount.AsNoTracking(), request, spec, ct);
    }

    /// <summary>How many messages each of the accounts sent since <paramref name="since"/>.</summary>
    public async Task<Dictionary<long, int>> SentSinceAsync(IEnumerable<long> ids, DateTime since, CancellationToken ct = default)
    {
        var list = ids.Select(id => (long?)id).ToList();
        var counts = await db.Message.Where(m => m.SentDate >= since && list.Contains(m.AccountId)).GroupBy(m => m.AccountId).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct);
        return counts.ToDictionary(c => c.Key!.Value, c => c.Count);
    }

    public Task<SmtpAccount?> FindAsync(long id, CancellationToken ct = default) => db.SmtpAccount.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id, ct);

    public Task<bool> AnyAsync(CancellationToken ct = default) => db.SmtpAccount.AnyAsync(ct);

    /// <summary>The accounts for a select (rules), by name.</summary>
    public Task<List<SmtpAccount>> AllAsync(CancellationToken ct = default) => db.SmtpAccount.AsNoTracking().OrderBy(a => a.Name).ToListAsync(ct);

    /// <summary>How many rules send through this account.</summary>
    public Task<int> CountRulesAsync(long id, CancellationToken ct = default) => db.Rule.CountAsync(r => r.AccountId == id, ct);

    /// <summary>The default account (also a switched-off one); null when there is none.</summary>
    public Task<SmtpAccount?> DefaultAsync(CancellationToken ct = default)
        => db.SmtpAccount.AsNoTracking().Where(a => a.IsDefault).OrderByDescending(a => a.IsEnabled).ThenBy(a => a.Id).FirstOrDefaultAsync(ct);

    /// <summary>Creates (<paramref name="id"/> null) or changes an account with <paramref name="apply"/>; a default account takes the mark from the others.</summary>
    public async Task<SmtpAccount> SaveAsync(long? id, Action<SmtpAccount> apply, CancellationToken ct = default)
    {
        var account = id is { } existing ? await db.SmtpAccount.FirstAsync(a => a.Id == existing, ct) : db.SmtpAccount.Add(new SmtpAccount()).Entity;
        apply(account);
        await db.SaveChangesAsync(ct);
        if (account.IsDefault)
        {
            await db.SmtpAccount.Where(a => a.Id != account.Id && a.IsDefault).ExecuteUpdateAsync(s => s.SetProperty(a => a.IsDefault, false), ct);
        }

        return account;
    }

    /// <summary>Deletes an account (softly); rules that named it send through the default account. Null when there is none.</summary>
    public async Task<SmtpAccount?> DeleteAsync(long id, CancellationToken ct = default)
    {
        var account = await db.SmtpAccount.FirstOrDefaultAsync(a => a.Id == id, ct);
        if (account is null)
        {
            return null;
        }

        db.SmtpAccount.Remove(account);
        await db.SaveChangesAsync(ct);
        return account;
    }
}
