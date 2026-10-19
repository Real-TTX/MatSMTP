using MatSmtp.Data.Entities;
using MatSmtp.Services;
using MatSmtp.Services.Relay;
using MatSmtp.UiKit;

namespace MatSmtp.Pages.RelayUsers;

/// <summary>The accounts with which applications sign in at the relay (SMTP AUTH).</summary>
public sealed class IndexModel(RelayUserService relayUsers) : PageModelBase
{
    public ToolbarModel Toolbar { get; private set; } = null!;

    public PagerModel Pager { get; private set; } = null!;

    public TableModel Table { get; private set; } = null!;

    public async Task OnGetAsync()
    {
        Head("Zugänge", "Mit diesen Benutzern melden sich Anwendungen am Relay an (SMTP AUTH). Aus vertrauenswürdigen Netzen geht es auch ohne.", ("Relay", null), ("Zugänge", null));
        var result = await relayUsers.ListAsync(Request, HttpContext.RequestAborted);
        Toolbar = result.Toolbar;
        Pager = result.Pager;
        Table = TableModel.Of("relayuser-table", "Zugänge", result.Items,
        [
            (new TableColumn("Benutzername"), u => Ui.NameWithSub(u.UserName, $"/RelayUsers/Edit?id={u.Id}", u.Description)),
            (new TableColumn("Verwaltet", HideOnPhone: true), u => u.ManagedBy is null ? Ui.Text("hier") : Ui.Pill(Labels.ManagedBy(u.ManagedBy), "info", "link")),
            (new TableColumn("Erlaubte Absender", HideOnPhone: true), u => string.IsNullOrWhiteSpace(u.AllowedSenders) ? Ui.Text("alle") : Ui.Mono(u.AllowedSenders.Replace("\n", ", "))),
            (new TableColumn("Zuletzt benutzt", HideOnPhone: true), u => Ui.Text(u.LastUsedDate is null ? "noch nie" : Fmt.Moment(u.LastUsedDate))),
            (new TableColumn("Status"), u => u.IsEnabled ? Ui.Pill("aktiv", "ok") : Ui.Pill("gesperrt", "error")),
        ],
        u => [new RowAction("edit", "Bearbeiten", $"/RelayUsers/Edit?id={u.Id}")]);
        Table = Table with
        {
            EmptyTitle = Toolbar.Total == 0 ? "Noch kein Zugang" : "Nichts gefunden",
            EmptyText = Toolbar.Total == 0 ? "Lege einen Zugang an, wenn sich Anwendungen am Relay anmelden sollen, zum Beispiel aus einem Netz, dem MatSMTP nicht vertraut." : "Ändere die Suche oder den Filter.",
            EmptyIcon = Toolbar.Total == 0 ? "key" : "search",
        };
    }
}
