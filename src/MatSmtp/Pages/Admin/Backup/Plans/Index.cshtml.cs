using MatSmtp.Data;
using MatSmtp.Data.Entities;
using MatSmtp.Services;
using MatSmtp.Services.Backup;
using MatSmtp.UiKit;
using Microsoft.EntityFrameworkCore;

namespace MatSmtp.Pages.Admin.Backup.Plans;

/// <summary>The backup plans: what is saved, where to, how often, how long the old ones are kept.</summary>
public sealed class IndexModel(MsDbContext db) : PageModelBase
{
    public ToolbarModel Toolbar { get; private set; } = null!;

    public PagerModel Pager { get; private set; } = null!;

    public TableModel Table { get; private set; } = null!;

    public static string Of(BackupScope s) => s == BackupScope.SystemOnly ? "Nur das System" : "System und Dateien";

    public async Task OnGetAsync()
    {
        Head("Sicherungspläne", "Was gesichert wird, wohin und wann.", ("Einstellungen", null), ("Sicherung", "/Admin/Backup"), ("Pläne", null));
        ViewData["ActiveHref"] = "/Admin/Backup/Plans";

        var spec = new ListSpec<BackupPlan>
        {
            Id = "plans",
            Action = "/Admin/Backup/Plans",
            Label = "Pläne",
            Noun = "Pläne",
            SearchPlaceholder = "Name ...",
            Search = (q, text) => q.Where(p => EF.Functions.Like(p.Name, Like.Contains(text), Like.Escape)),
            Filters =
            [
                new FilterSpec<BackupPlan>("umfang", "Umfang", ListBuilder.EnumOptions<BackupScope>(Of), (q, v) => q.Where(p => p.Scope == Enum.Parse<BackupScope>(v))),
                new FilterSpec<BackupPlan>("status", "Status", [new("aktiv", "Aktiv"), new("aus", "Ausgeschaltet")], (q, v) => v == "aus" ? q.Where(p => !p.IsEnabled) : q.Where(p => p.IsEnabled)),
            ],
            Sorts = new Dictionary<string, (string, Func<IQueryable<BackupPlan>, bool, IOrderedQueryable<BackupPlan>>)>
            {
                ["name"] = ("Name", (q, d) => d ? q.OrderByDescending(p => p.Name) : q.OrderBy(p => p.Name)),
                ["next"] = ("Nächste Sicherung", (q, d) => d ? q.OrderByDescending(p => p.NextRunDate) : q.OrderBy(p => p.NextRunDate)),
            },
            DefaultSort = "name",
        };

        var result = await ListBuilder.RunAsync(db.BackupPlan.AsNoTracking().Include(p => p.Target), Request, spec, HttpContext.RequestAborted);
        Toolbar = result.Toolbar;
        Pager = result.Pager;
        Table = TableModel.Of("plan-table", "Sicherungspläne", result.Items,
        [
            (new TableColumn("Name"), p => Ui.NameWithSub(p.Name, $"/Admin/Backup/Plans/Edit?id={p.Id}", Of(p.Scope) + (string.IsNullOrEmpty(p.PassphraseProtected) ? "" : " · verschlüsselt"))),
            (new TableColumn("Ziel", HideOnPhone: true), p => Ui.Text(p.Target?.Name)),
            (new TableColumn("Zeitplan", HideOnPhone: true), p => Ui.Text(BackupSchedule.Describe(p))),
            (new TableColumn("Nächste", HideOnPhone: true), p => Ui.Text(p.IsEnabled ? Fmt.Moment(p.NextRunDate) : "")),
            (new TableColumn("Zuletzt"), p => p.LastRunDate is null ? Ui.Text("noch nie") : p.LastStatus?.StartsWith("Fehl", StringComparison.Ordinal) == true ? Ui.Pill("fehlgeschlagen", "error") : Ui.Pill("ok", "ok")),
            (new TableColumn("Status"), p => p.IsEnabled ? Ui.Pill("aktiv", "ok") : Ui.Pill("aus", "plain")),
        ],
        p => [new RowAction("edit", "Bearbeiten", $"/Admin/Backup/Plans/Edit?id={p.Id}")]);
        Table = Table with { EmptyTitle = Toolbar.Total == 0 ? "Noch kein Plan" : "Nichts gefunden", EmptyText = Toolbar.Total == 0 ? "Lege fest, was wann gesichert wird." : "Ändere die Suche oder den Filter.", EmptyIcon = "calendar" };
    }
}
