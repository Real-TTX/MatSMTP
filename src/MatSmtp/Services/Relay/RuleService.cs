using System.Globalization;
using MatSmtp.Core.Relay;
using MatSmtp.Data;
using MatSmtp.Data.Entities;
using MatSmtp.UiKit;
using Microsoft.EntityFrameworkCore;

namespace MatSmtp.Services.Relay;

/// <summary>
/// The rules as the pages edit them: the list in its order, one rule, saving, deleting and moving. The order is the logic (the receiver asks the rules top to
/// bottom), so positions are kept as 10, 20, 30 ... after every move and delete.
/// </summary>
public sealed class RuleService(MsDbContext db)
{
    /// <summary>The list of the page (with account, webhook and relay user of each rule): search, filter, sort and page of the request.</summary>
    public async Task<ListResult<Rule>> ListAsync(HttpRequest request, CancellationToken ct)
    {
        var accounts = await db.SmtpAccount.AsNoTracking().OrderBy(a => a.Name).Select(a => new { a.Id, a.Name }).ToListAsync(ct);
        var spec = new ListSpec<Rule>
        {
            Id = "rules",
            Action = "/Rules",
            Label = "Regeln",
            Noun = "Regeln",
            SearchPlaceholder = "Name, Absender, Empfänger, Netz ...",
            Search = (q, text) =>
            {
                var like = Like.Contains(text);
                return q.Where(r => EF.Functions.Like(r.Name, like, Like.Escape) || EF.Functions.Like(r.Description ?? "", like, Like.Escape) || EF.Functions.Like(r.MatchSenders ?? "", like, Like.Escape)
                    || EF.Functions.Like(r.MatchRecipients ?? "", like, Like.Escape) || EF.Functions.Like(r.MatchNetworks ?? "", like, Like.Escape) || EF.Functions.Like(r.RewriteFrom ?? "", like, Like.Escape));
            },
            Filters =
            [
                new FilterSpec<Rule>("aktion", "Aktion", ListBuilder.EnumOptions<RuleAction>(Labels.Of), (q, v) => q.Where(r => r.Action == Enum.Parse<RuleAction>(v))),
                new FilterSpec<Rule>("konto", "Konto", [.. accounts.Select(a => new FilterOption(a.Id.ToString(CultureInfo.InvariantCulture), a.Name))], (q, v) => long.TryParse(v, out var id) ? q.Where(r => r.AccountId == id) : q),
                new FilterSpec<Rule>("status", "Status", [new("aktiv", "Aktiv"), new("aus", "Abgeschaltet")], (q, v) => v == "aus" ? q.Where(r => !r.IsEnabled) : q.Where(r => r.IsEnabled)),
            ],
            Sorts = new Dictionary<string, (string, Func<IQueryable<Rule>, bool, IOrderedQueryable<Rule>>)>
            {
                ["position"] = ("Reihenfolge", (q, d) => d ? q.OrderByDescending(r => r.Position).ThenByDescending(r => r.Id) : q.OrderBy(r => r.Position).ThenBy(r => r.Id)),
                ["name"] = ("Name", (q, d) => d ? q.OrderByDescending(r => r.Name) : q.OrderBy(r => r.Name)),
                ["hits"] = ("Treffer", (q, d) => d ? q.OrderByDescending(r => r.HitCount) : q.OrderBy(r => r.HitCount)),
            },
            DefaultSort = "position",
            PageSize = 50,
        };

        return await ListBuilder.RunAsync(db.Rule.AsNoTracking().Include(r => r.Account).Include(r => r.Webhook).Include(r => r.MatchRelayUser), request, spec, ct);
    }

    public Task<Rule?> FindAsync(long id, CancellationToken ct = default) => db.Rule.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, ct);

    /// <summary>The relay users for the select of a rule, by name.</summary>
    public Task<List<RelayUser>> RelayUsersAsync(CancellationToken ct = default) => db.RelayUser.AsNoTracking().OrderBy(u => u.UserName).ToListAsync(ct);

    /// <summary>Creates (<paramref name="id"/> null; it goes to the end of the order) or changes a rule with <paramref name="apply"/>.</summary>
    public async Task<Rule> SaveAsync(long? id, Action<Rule> apply, CancellationToken ct = default)
    {
        Rule rule;
        if (id is { } existing)
        {
            rule = await db.Rule.FirstAsync(r => r.Id == existing, ct);
        }
        else
        {
            rule = new Rule { Position = (await db.Rule.MaxAsync(r => (int?)r.Position, ct) ?? 0) + 10 };
            db.Rule.Add(rule);
        }

        apply(rule);
        await db.SaveChangesAsync(ct);
        return rule;
    }

    /// <summary>Deletes a rule (softly) and closes the gap in the order; null when there is none.</summary>
    public async Task<Rule?> DeleteAsync(long id, CancellationToken ct = default)
    {
        var rule = await db.Rule.FirstOrDefaultAsync(r => r.Id == id, ct);
        if (rule is null)
        {
            return null;
        }

        db.Rule.Remove(rule);
        await db.SaveChangesAsync(ct);
        var rest = await db.Rule.OrderBy(r => r.Position).ThenBy(r => r.Id).ToListAsync(ct);
        Renumber(rest);
        await db.SaveChangesAsync(ct);
        return rule;
    }

    /// <summary>Moves a rule one place up or down; returns the rule and its new place (1-based), or null when it cannot move.</summary>
    public async Task<(Rule Rule, int Place)?> MoveAsync(long id, bool up, CancellationToken ct = default)
    {
        var rules = await db.Rule.OrderBy(r => r.Position).ThenBy(r => r.Id).ToListAsync(ct);
        var i = rules.FindIndex(r => r.Id == id);
        var j = up ? i - 1 : i + 1;
        if (i < 0 || j < 0 || j >= rules.Count)
        {
            return null;
        }

        (rules[i], rules[j]) = (rules[j], rules[i]);
        Renumber(rules);
        await db.SaveChangesAsync(ct);
        return (rules[j], j + 1);
    }

    /// <summary>Positions 10, 20, 30 ...: room for a rule in between, and the numbers of the list stay what the person sees.</summary>
    public static void Renumber(IList<Rule> rules)
    {
        for (var k = 0; k < rules.Count; k++)
        {
            rules[k].Position = (k + 1) * 10;
        }
    }
}
