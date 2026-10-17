using MatSmtp.Data;
using MatSmtp.Data.Entities;
using MatSmtp.Services;
using MatSmtp.UiKit;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MatSmtp.Pages.Admin;

/// <summary>Every link that shows the delivery of a message to somebody without an account: for which message, how long it holds, how often it was opened.</summary>
public sealed class SharesModel(MsDbContext db, ShareService shares, ActivityLogger log, TimeProvider clock) : PageModelBase
{
    public const string BulkForm = "share-bulk";

    public ToolbarModel Toolbar { get; private set; } = null!;

    public PagerModel Pager { get; private set; } = null!;

    public TableModel Table { get; private set; } = null!;

    public async Task OnGetAsync()
    {
        Head("Freigaben", "Links, die jemandem ohne Konto die Zustellung einer Nachricht zeigen (Rolle „Anonym“). Sie entstehen auf der Seite einer Nachricht.", ("Einstellungen", null), ("Freigaben", null));
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

        var result = await ListBuilder.RunAsync(db.ShareLink.AsNoTracking().Include(l => l.Message), Request, spec, HttpContext.RequestAborted);
        Toolbar = result.Toolbar;
        Pager = result.Pager;
        Table = TableModel.Of("share-table", "Freigaben", result.Items,
        [
            (new TableColumn("Nachricht"), l => Ui.NameWithSub(l.Message?.Subject ?? $"Nachricht {l.MessageId}", $"/Messages/Details?id={l.MessageId}", l.Note)),
            (new TableColumn("Empfänger", HideOnPhone: true), l => Ui.Text(l.Message?.Recipients)),
            (new TableColumn("Gültig bis"), l => Ui.Text(Fmt.Date(l.ExpiresDate))),
            (new TableColumn("Aufrufe", Numeric: true, HideOnPhone: true), l => Ui.Text(Fmt.Integer(l.Views))),
            (new TableColumn("Status"), l => l.RevokedDate is not null ? Ui.Pill("widerrufen", "plain") : l.ExpiresDate <= now ? Ui.Pill("abgelaufen", "plain") : Ui.Pill("gültig", "ok")),
        ],
        l => [new RowAction("eye", "Nachricht öffnen", $"/Messages/Details?id={l.MessageId}")],
        l => (l.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), "Freigabe auswählen"));
        Table = Table with
        {
            SelectForm = BulkForm,
            EmptyTitle = Toolbar.Total == 0 ? "Noch keine Freigabe" : "Nichts gefunden",
            EmptyText = Toolbar.Total == 0 ? "Links entstehen auf der Seite einer Nachricht unter „Freigabe“." : "Ändere die Suche oder den Filter.",
            EmptyIcon = "share",
        };
    }

    public async Task<IActionResult> OnPostRevokeAsync(long[] ids)
    {
        var count = 0;
        foreach (var id in ids)
        {
            if (await shares.RevokeAsync(id))
            {
                count++;
            }
        }

        await log.LogAsync("Freigaben widerrufen", nameof(ShareLink), null, $"{count} Link(s)");
        Flash(count == 0 ? "Es war nichts zu widerrufen." : $"{Ui.Plural(count, "Link", "Links")} widerrufen.");
        return RedirectToPage();
    }
}
