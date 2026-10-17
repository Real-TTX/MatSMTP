using MatSmtp.Data;
using MatSmtp.Data.Entities;
using MatSmtp.Services;
using MatSmtp.UiKit;
using Microsoft.EntityFrameworkCore;

namespace MatSmtp.Pages.Accounts;

/// <summary>The SMTP accounts the relay sends through.</summary>
public sealed class IndexModel(MsDbContext db, TimeProvider clock) : PageModelBase
{
    public ToolbarModel Toolbar { get; private set; } = null!;

    public PagerModel Pager { get; private set; } = null!;

    public TableModel Table { get; private set; } = null!;

    public async Task OnGetAsync()
    {
        Head("SMTP-Konten", "Die Konten, über die MatSMTP versendet. Das Standardkonto nimmt alles, wofür keine Regel ein anderes wählt.", ("Relay", null), ("SMTP-Konten", null));
        var midnight = Fmt.UtcFromLocal(Fmt.Local(clock.GetUtcNow().UtcDateTime).Date);
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

        var result = await ListBuilder.RunAsync(db.SmtpAccount.AsNoTracking(), Request, spec, HttpContext.RequestAborted);
        Toolbar = result.Toolbar;
        Pager = result.Pager;
        var ids = result.Items.Select(a => (long?)a.Id).ToList();
        var today = await db.Message.Where(m => m.SentDate >= midnight && ids.Contains(m.AccountId)).GroupBy(m => m.AccountId).Select(g => new { g.Key, Count = g.Count() }).ToListAsync();
        Table = TableModel.Of("account-table", "SMTP-Konten", result.Items,
        [
            (new TableColumn("Name"), a => Ui.NameWithSub(a.Name, $"/Accounts/Edit?id={a.Id}", a.IsDefault ? "Standardkonto" : a.Description)),
            (new TableColumn("Server", HideOnPhone: true), a => Ui.Mono($"{a.Host}:{a.Port}")),
            (new TableColumn("Verschlüsselung", HideOnPhone: true), a => Ui.Text(Labels.Of(a.Security))),
            (new TableColumn("Grenze", HideOnPhone: true), a => Ui.Text(a.MaxPerMinute == 0 && a.MaxPerDay == 0 ? "keine" : string.Join(", ", new[] { a.MaxPerMinute > 0 ? $"{a.MaxPerMinute}/min" : null, a.MaxPerDay > 0 ? $"{a.MaxPerDay}/Tag" : null }.Where(s => s is not null)))),
            (new TableColumn("Heute", Numeric: true), a => Ui.Text(Fmt.Integer(today.FirstOrDefault(t => t.Key == a.Id)?.Count ?? 0))),
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
