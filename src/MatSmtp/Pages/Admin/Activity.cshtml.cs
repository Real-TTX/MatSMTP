using MatSmtp.Services;
using MatSmtp.UiKit;

namespace MatSmtp.Pages.Admin;

/// <summary>What was done here and by whom: sign-ins, changes of settings, rules, accounts, messages sent again or deleted. For the administrator, and for the question "who did that?".</summary>
public sealed class ActivityModel(ActivityService activity) : PageModelBase
{
    public ToolbarModel Toolbar { get; private set; } = null!;

    public PagerModel Pager { get; private set; } = null!;

    public TableModel Table { get; private set; } = null!;

    public async Task OnGetAsync()
    {
        Head("Aktivität", "Was in MatSMTP getan wurde.", ("Einstellungen", null), ("Aktivität", null));
        var result = await activity.ListAsync(Request, HttpContext.RequestAborted);
        Toolbar = result.Toolbar;
        Pager = result.Pager;
        Table = TableModel.Of("activity-table", "Aktivität", result.Items,
        [
            (new TableColumn("Wann"), a => Ui.Text(Fmt.Moment(a.OccurredDate))),
            (new TableColumn("Person"), a => Ui.Text(a.UserName ?? "System")),
            (new TableColumn("Aktion"), a => Ui.NameWithSub(a.Action, null, a.Summary)),
            (new TableColumn("Adresse", HideOnPhone: true, Mono: true), a => Ui.Text(a.IpAddress)),
        ],
        a => []);
        Table = Table with { EmptyTitle = Toolbar.Total == 0 ? "Noch nichts protokolliert" : "Nichts gefunden", EmptyText = Toolbar.Total == 0 ? "Sobald etwas geschieht, erscheint es hier." : "Ändere die Suche oder den Filter.", EmptyIcon = "activity" };
    }
}
