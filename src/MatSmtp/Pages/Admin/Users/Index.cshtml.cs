using MatSmtp.Data;
using MatSmtp.Data.Entities;
using MatSmtp.Services;
using MatSmtp.UiKit;
using Microsoft.EntityFrameworkCore;

namespace MatSmtp.Pages.Admin.Users;

/// <summary>Everybody who can sign in: administrators and users.</summary>
public sealed class IndexModel(MsDbContext db, ICurrentUser current) : PageModelBase
{
    public ToolbarModel Toolbar { get; private set; } = null!;

    public PagerModel Pager { get; private set; } = null!;

    public TableModel Table { get; private set; } = null!;

    public async Task OnGetAsync()
    {
        Head("Benutzer", "Wer sich anmelden kann und was er darf.", ("Einstellungen", null), ("Benutzer", null));

        var spec = new ListSpec<User>
        {
            Id = "users",
            Action = "/Admin/Users",
            Label = "Benutzer",
            Noun = "Benutzer",
            SearchPlaceholder = "Name oder E-Mail-Adresse ...",
            Search = (q, text) =>
            {
                var like = Like.Contains(text);
                return q.Where(u => EF.Functions.Like(u.DisplayName, like, Like.Escape) || EF.Functions.Like(u.Email, like, Like.Escape));
            },
            Filters =
            [
                new FilterSpec<User>("rolle", "Rolle", ListBuilder.EnumOptions<UserRole>(Labels.Of), (q, v) => q.Where(u => u.Role == Enum.Parse<UserRole>(v))),
                new FilterSpec<User>("status", "Status", [new("aktiv", "Aktiv"), new("gesperrt", "Gesperrt")], (q, v) => v == "gesperrt" ? q.Where(u => !u.IsActive) : q.Where(u => u.IsActive)),
            ],
            Sorts = new Dictionary<string, (string, Func<IQueryable<User>, bool, IOrderedQueryable<User>>)>
            {
                ["name"] = ("Name", (q, d) => d ? q.OrderByDescending(u => u.DisplayName) : q.OrderBy(u => u.DisplayName)),
                ["email"] = ("E-Mail", (q, d) => d ? q.OrderByDescending(u => u.Email) : q.OrderBy(u => u.Email)),
                ["role"] = ("Rolle", (q, d) => d ? q.OrderByDescending(u => u.Role).ThenBy(u => u.DisplayName) : q.OrderBy(u => u.Role).ThenBy(u => u.DisplayName)),
                ["login"] = ("Letzte Anmeldung", (q, d) => d ? q.OrderByDescending(u => u.LastLoginDate) : q.OrderBy(u => u.LastLoginDate)),
            },
            DefaultSort = "name",
        };

        var result = await ListBuilder.RunAsync(db.User.AsNoTracking().Include(u => u.Totp), Request, spec, HttpContext.RequestAborted);
        Toolbar = result.Toolbar;
        Pager = result.Pager;
        var me = current.UserId;
        Table = TableModel.Of("user-table", "Benutzer", result.Items,
        [
            (new TableColumn("Name"), u => Ui.NameWithSub(u.DisplayName, $"/Admin/Users/Edit?id={u.Id}", u.Id == me ? "das bist du" : null)),
            (new TableColumn("E-Mail", HideOnPhone: true), u => Ui.Text(u.Email)),
            (new TableColumn("Rolle"), u => Ui.Text(Labels.Of(u.Role))),
            (new TableColumn("Zwei Faktoren", HideOnPhone: true), u => Ui.Text(u.Totp is { IsConfirmed: true } ? "an" : "aus")),
            (new TableColumn("Letzte Anmeldung", HideOnPhone: true), u => Ui.Text(u.LastLoginDate is null ? "noch nie" : Fmt.Moment(u.LastLoginDate))),
            (new TableColumn("Status"), u => u.IsActive ? Ui.Pill("aktiv", "ok") : Ui.Pill("gesperrt", "error")),
        ],
        u => [new RowAction("edit", "Bearbeiten", $"/Admin/Users/Edit?id={u.Id}")]);
        Table = Table with { EmptyTitle = "Nichts gefunden", EmptyText = "Ändere die Suche oder den Filter.", EmptyIcon = "users" };
    }
}
