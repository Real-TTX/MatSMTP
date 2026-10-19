using MatSmtp.Data.Entities;
using MatSmtp.Services;
using MatSmtp.UiKit;
using Microsoft.AspNetCore.Mvc;

namespace MatSmtp.Pages.Admin;

/// <summary>Every link that shows the delivery of a message to somebody without an account: for which message, how long it holds, how often it was opened.</summary>
public sealed class SharesModel(ShareService shares, ActivityLogger log, TimeProvider clock) : PageModelBase
{
    public const string BulkForm = "share-bulk";

    public ToolbarModel Toolbar { get; private set; } = null!;

    public PagerModel Pager { get; private set; } = null!;

    public TableModel Table { get; private set; } = null!;

    public async Task OnGetAsync()
    {
        Head("Freigaben", "Links, die jemandem ohne Konto die Zustellung einer Nachricht zeigen (Rolle „Anonym“). Sie entstehen auf der Seite einer Nachricht.", ("Einstellungen", null), ("Freigaben", null));
        var now = clock.GetUtcNow().UtcDateTime;
        var result = await shares.ListAsync(Request, HttpContext.RequestAborted);
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
