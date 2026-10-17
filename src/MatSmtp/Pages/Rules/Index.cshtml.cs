using System.Globalization;
using System.Net;
using MatSmtp.Core.Relay;
using MatSmtp.Data;
using MatSmtp.Data.Entities;
using MatSmtp.Services;
using MatSmtp.UiKit;
using Microsoft.AspNetCore.Html;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MatSmtp.Pages.Rules;

/// <summary>
/// The rules in their order: the relay asks them top to bottom, every matching rule applies its changes, the first one with a decision ends it. The order is changed
/// with the arrows of a row (one shared form below the list).
/// </summary>
public sealed class IndexModel(MsDbContext db, ActivityLogger log) : PageModelBase
{
    public const string MoveForm = "rule-move";

    public ToolbarModel Toolbar { get; private set; } = null!;

    public PagerModel Pager { get; private set; } = null!;

    public TableModel Table { get; private set; } = null!;

    public async Task OnGetAsync()
    {
        Head("Regeln", "Von oben nach unten geprüft: jede passende Regel wendet ihre Änderungen an, die erste mit einer Entscheidung beendet die Prüfung. Ohne Entscheidung geht die Nachricht über das Standardkonto.", ("Relay", null), ("Regeln", null));
        var accounts = await db.SmtpAccount.AsNoTracking().OrderBy(a => a.Name).Select(a => new { a.Id, a.Name }).ToListAsync();
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

        var result = await ListBuilder.RunAsync(db.Rule.AsNoTracking().Include(r => r.Account).Include(r => r.Webhook).Include(r => r.MatchRelayUser), Request, spec, HttpContext.RequestAborted);
        Toolbar = result.Toolbar;
        Pager = result.Pager;
        var ordered = Toolbar.Sort == "position" && !Toolbar.Descending && !Toolbar.SearchActive && Toolbar.ActiveFilters == 0;
        var index = 0;
        Table = TableModel.Of("rule-table", "Regeln", result.Items,
        [
            (new TableColumn("#", Numeric: true), r => Ui.Text((++index + (Pager.Page - 1) * Pager.PageSize).ToString(CultureInfo.InvariantCulture))),
            (new TableColumn("Name"), r => Ui.NameWithSub(r.Name, $"/Rules/Edit?id={r.Id}", r.Description)),
            (new TableColumn("Wenn", HideOnPhone: true), r => Ui.Text(Conditions(r))),
            (new TableColumn("Dann"), r => Ui.Concat(Labels.Pill(r.Action), Ui.NameWithSub("", null, Effects(r)))),
            (new TableColumn("Treffer", Numeric: true, HideOnPhone: true), r => Ui.Text(Fmt.Integer(r.HitCount))),
            (new TableColumn("Status"), r => r.IsEnabled ? Ui.Pill("aktiv", "ok") : Ui.Pill("aus", "plain")),
            (new TableColumn("Reihenfolge", HideOnPhone: true), r => ordered ? MoveButtons(r) : Ui.None()),
        ],
        r => [new RowAction("edit", "Bearbeiten", $"/Rules/Edit?id={r.Id}")]);
        Table = Table with
        {
            EmptyTitle = Toolbar.Total == 0 ? "Noch keine Regel" : "Nichts gefunden",
            EmptyText = Toolbar.Total == 0 ? "Ohne Regeln geht jede Nachricht unverändert über das Standardkonto. Mit Regeln wählst du nach Absender, Empfänger, Netz oder Zugang ein anderes Konto, schreibst den Absender um, rufst Webhooks auf oder weist ab." : "Ändere die Suche oder den Filter.",
            EmptyIcon = Toolbar.Total == 0 ? "route" : "search",
        };
    }

    /// <summary>Moves a rule one place up or down (the value is "id:up" or "id:down").</summary>
    public async Task<IActionResult> OnPostMoveAsync(string? move)
    {
        var parts = (move ?? "").Split(':');
        if (parts.Length != 2 || !long.TryParse(parts[0], out var id))
        {
            return RedirectToPage();
        }

        var rules = await db.Rule.OrderBy(r => r.Position).ThenBy(r => r.Id).ToListAsync();
        var i = rules.FindIndex(r => r.Id == id);
        var j = parts[1] == "up" ? i - 1 : i + 1;
        if (i < 0 || j < 0 || j >= rules.Count)
        {
            return RedirectToPage();
        }

        (rules[i], rules[j]) = (rules[j], rules[i]);
        Renumber(rules);
        await db.SaveChangesAsync();
        await log.LogAsync("Regel verschoben", nameof(Rule), id, $"{rules[j].Name} auf Platz {j + 1}");
        return RedirectToPage();
    }

    /// <summary>Positions 10, 20, 30 ...: room for a rule in between, and the numbers of the list stay what the person sees.</summary>
    public static void Renumber(IList<Rule> rules)
    {
        for (var k = 0; k < rules.Count; k++)
        {
            rules[k].Position = (k + 1) * 10;
        }
    }

    public static string Conditions(Rule r)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(r.MatchSenders)) { parts.Add("Absender " + Short(r.MatchSenders)); }
        if (!string.IsNullOrWhiteSpace(r.MatchRecipients)) { parts.Add("Empfänger " + Short(r.MatchRecipients)); }
        if (!string.IsNullOrWhiteSpace(r.MatchNetworks)) { parts.Add("Netz " + Short(r.MatchNetworks)); }
        if (r.MatchRelayUserId is not null) { parts.Add("Zugang " + (r.MatchRelayUser?.UserName ?? "(gelöscht)")); }
        else if (r.MatchAuth == AuthCondition.Authenticated) { parts.Add("angemeldet"); }
        else if (r.MatchAuth == AuthCondition.Anonymous) { parts.Add("ohne Anmeldung"); }
        if (!string.IsNullOrWhiteSpace(r.MatchSubject)) { parts.Add("Betreff " + Short(r.MatchSubject)); }
        return parts.Count == 0 ? "jede Nachricht" : string.Join(" · ", parts);
    }

    public static string? Effects(Rule r)
    {
        var parts = new List<string>();
        if (r.Action == RuleAction.Relay) { parts.Add(r.Account is null ? "Standardkonto" : "über " + r.Account.Name); }
        if (!string.IsNullOrWhiteSpace(r.RewriteFrom)) { parts.Add("Absender → " + r.RewriteFrom); }
        if (r.Webhook is not null && r.Action is not RuleAction.Reject and not RuleAction.Drop) { parts.Add("Webhook " + r.Webhook.Name); }
        if (!string.IsNullOrWhiteSpace(r.AddBcc) && r.Action is RuleAction.Relay or RuleAction.Continue) { parts.Add("Bcc " + Short(r.AddBcc)); }
        return parts.Count == 0 ? null : string.Join(" · ", parts);
    }

    private static string Short(string list)
    {
        var entries = AddressPattern.Split(list);
        return entries.Count <= 2 ? string.Join(", ", entries) : $"{entries[0]}, {entries[1]} und {entries.Count - 2} weitere";
    }

    private static IHtmlContent MoveButtons(Rule r)
    {
        var id = r.Id.ToString(CultureInfo.InvariantCulture);
        var name = WebUtility.HtmlEncode(r.Name);
        return new HtmlString(
            $"<div class=\"ms-move\"><button type=\"submit\" form=\"{MoveForm}\" name=\"move\" value=\"{id}:up\" class=\"mat-btn mat-btn--ghost mat-btn--icon mat-btn--sm\" aria-label=\"{name} nach oben\" data-tip=\"Nach oben\">{Ui.Icon("arrow-up").ToHtml()}</button>" +
            $"<button type=\"submit\" form=\"{MoveForm}\" name=\"move\" value=\"{id}:down\" class=\"mat-btn mat-btn--ghost mat-btn--icon mat-btn--sm\" aria-label=\"{name} nach unten\" data-tip=\"Nach unten\">{Ui.Icon("arrow-down").ToHtml()}</button></div>");
    }
}
