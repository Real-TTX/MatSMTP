using MatSmtp.Data;
using MatSmtp.Data.Entities;
using MatSmtp.Services;
using MatSmtp.UiKit;
using Microsoft.EntityFrameworkCore;

namespace MatSmtp.Pages.RelayUsers;

/// <summary>The accounts with which applications sign in at the relay (SMTP AUTH).</summary>
public sealed class IndexModel(MsDbContext db) : PageModelBase
{
    public ToolbarModel Toolbar { get; private set; } = null!;

    public PagerModel Pager { get; private set; } = null!;

    public TableModel Table { get; private set; } = null!;

    public async Task OnGetAsync()
    {
        Head("Zugänge", "Mit diesen Benutzern melden sich Anwendungen am Relay an (SMTP AUTH). Aus vertrauenswürdigen Netzen geht es auch ohne.", ("Relay", null), ("Zugänge", null));
        var spec = new ListSpec<RelayUser>
        {
            Id = "relayusers",
            Action = "/RelayUsers",
            Label = "Zugänge",
            Noun = "Zugänge",
            SearchPlaceholder = "Benutzername oder Beschreibung ...",
            Search = (q, text) =>
            {
                var like = Like.Contains(text);
                return q.Where(u => EF.Functions.Like(u.UserName, like, Like.Escape) || EF.Functions.Like(u.Description ?? "", like, Like.Escape));
            },
            Filters =
            [
                new FilterSpec<RelayUser>("status", "Status", [new("aktiv", "Aktiv"), new("aus", "Gesperrt")], (q, v) => v == "aus" ? q.Where(u => !u.IsEnabled) : q.Where(u => u.IsEnabled)),
            ],
            Sorts = new Dictionary<string, (string, Func<IQueryable<RelayUser>, bool, IOrderedQueryable<RelayUser>>)>
            {
                ["name"] = ("Benutzername", (q, d) => d ? q.OrderByDescending(u => u.UserName) : q.OrderBy(u => u.UserName)),
                ["used"] = ("Zuletzt benutzt", (q, d) => d ? q.OrderByDescending(u => u.LastUsedDate) : q.OrderBy(u => u.LastUsedDate)),
            },
            DefaultSort = "name",
        };

        var result = await ListBuilder.RunAsync(db.RelayUser.AsNoTracking(), Request, spec, HttpContext.RequestAborted);
        Toolbar = result.Toolbar;
        Pager = result.Pager;
        Table = TableModel.Of("relayuser-table", "Zugänge", result.Items,
        [
            (new TableColumn("Benutzername"), u => Ui.NameWithSub(u.UserName, $"/RelayUsers/Edit?id={u.Id}", u.Description)),
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
