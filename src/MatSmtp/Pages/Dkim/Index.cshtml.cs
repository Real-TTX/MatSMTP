using MatSmtp.Services;
using MatSmtp.Services.Relay;
using MatSmtp.UiKit;

namespace MatSmtp.Pages.Dkim;

/// <summary>The DKIM keys of the sender domains: which domain signs, and whether its record is in the DNS.</summary>
public sealed class IndexModel(DkimService dkim) : PageModelBase
{
    public ToolbarModel Toolbar { get; private set; } = null!;

    public PagerModel Pager { get; private set; } = null!;

    public TableModel Table { get; private set; } = null!;

    public async Task OnGetAsync()
    {
        Head("DKIM", "Signaturen für die Absender-Domains: Empfänger sehen, dass die Nachricht wirklich von der Domain kommt und unterwegs nicht verändert wurde.", ("Relay", null), ("DKIM", null));
        var result = await dkim.ListAsync(Request, HttpContext.RequestAborted);
        Toolbar = result.Toolbar;
        Pager = result.Pager;
        Table = TableModel.Of("dkim-table", "DKIM-Schlüssel", result.Items,
        [
            (new TableColumn("Domain"), k => Ui.NameWithSub(k.Domain, $"/Dkim/Edit?id={k.Id}", k.IncludeSubdomains ? "mit Subdomains" + (k.Description is null ? "" : " · " + k.Description) : k.Description)),
            (new TableColumn("Selektor", HideOnPhone: true), k => Ui.Mono(k.Selector)),
            (new TableColumn("Schlüssel", HideOnPhone: true), k => Ui.Text($"RSA {k.KeyBits}")),
            (new TableColumn("Zuletzt signiert", HideOnPhone: true), k => Ui.Text(k.LastUsedDate is null ? "noch nie" : Fmt.Moment(k.LastUsedDate))),
            (new TableColumn("DNS"), k => Labels.Pill(k.DnsState)),
            (new TableColumn("Status"), k => k.IsEnabled ? Ui.Pill("signiert", "ok", "shield-check") : Ui.Pill("abgeschaltet", "plain")),
        ],
        k => [new RowAction("edit", "Bearbeiten", $"/Dkim/Edit?id={k.Id}")]);
        Table = Table with
        {
            EmptyTitle = Toolbar.Total == 0 ? "Noch kein DKIM-Schlüssel" : "Nichts gefunden",
            EmptyText = Toolbar.Total == 0 ? "Lege für jede Domain, in deren Namen MatSMTP versendet, einen Schlüssel an und trage den angezeigten Eintrag im DNS ein. Ohne Signatur landen Nachrichten schneller im Spam." : "Ändere die Suche oder den Filter.",
            EmptyIcon = Toolbar.Total == 0 ? "shield-check" : "search",
        };
    }
}
