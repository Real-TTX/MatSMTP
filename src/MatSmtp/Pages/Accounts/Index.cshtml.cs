using MatSmtp.Services.Relay;
using MatSmtp.Data.Entities;
using MatSmtp.Services;
using MatSmtp.UiKit;

namespace MatSmtp.Pages.Accounts;

/// <summary>The SMTP accounts the relay sends through.</summary>
public sealed class IndexModel(AccountService accounts, TimeProvider clock) : PageModelBase
{
    public ToolbarModel Toolbar { get; private set; } = null!;

    public PagerModel Pager { get; private set; } = null!;

    public TableModel Table { get; private set; } = null!;

    public async Task OnGetAsync()
    {
        Head("SMTP-Konten", "Die Konten, über die MatSMTP versendet. Das Standardkonto nimmt alles, wofür keine Regel ein anderes wählt.", ("Relay", null), ("SMTP-Konten", null));
        var midnight = Fmt.UtcFromLocal(Fmt.Local(clock.GetUtcNow().UtcDateTime).Date);
        var result = await accounts.ListAsync(Request, HttpContext.RequestAborted);
        Toolbar = result.Toolbar;
        Pager = result.Pager;
        var today = await accounts.SentSinceAsync(result.Items.Select(a => a.Id), midnight, HttpContext.RequestAborted);
        Table = TableModel.Of("account-table", "SMTP-Konten", result.Items,
        [
            (new TableColumn("Name"), a => Ui.NameWithSub(a.Name, $"/Accounts/Edit?id={a.Id}", a.IsDefault ? "Standardkonto" : a.Description)),
            (new TableColumn("Server", HideOnPhone: true), a => Ui.Mono($"{a.Host}:{a.Port}")),
            (new TableColumn("Verschlüsselung", HideOnPhone: true), a => Ui.Text(Labels.Of(a.Security))),
            (new TableColumn("Grenze", HideOnPhone: true), a => Ui.Text(a.MaxPerMinute == 0 && a.MaxPerDay == 0 ? "keine" : string.Join(", ", new[] { a.MaxPerMinute > 0 ? $"{a.MaxPerMinute}/min" : null, a.MaxPerDay > 0 ? $"{a.MaxPerDay}/Tag" : null }.Where(s => s is not null)))),
            (new TableColumn("Heute", Numeric: true), a => Ui.Text(Fmt.Integer(today.GetValueOrDefault(a.Id)))),
            (new TableColumn("Status"), a => !a.IsEnabled ? Ui.Pill("abgeschaltet", "plain")
                : a.LastErrorDate is not null && (a.LastUsedDate is null || a.LastErrorDate > a.LastUsedDate) ? Ui.Pill("Fehler", "error", "alert") : Ui.Pill("bereit", "ok", "check")),
        ],
        a => [new RowAction("edit", "Bearbeiten", $"/Accounts/Edit?id={a.Id}")]);
        Table = Table with
        {
            EmptyTitle = Toolbar.Total == 0 ? "Noch kein Konto" : "Nichts gefunden",
            EmptyText = Toolbar.Total == 0 ? "Lege das Konto an, über das MatSMTP versendet (zum Beispiel Microsoft 365, ein Mail-Hoster oder der Server der Firma)." : "Ändere die Suche oder den Filter.",
            EmptyIcon = Toolbar.Total == 0 ? "server" : "search",
        };
    }
}
