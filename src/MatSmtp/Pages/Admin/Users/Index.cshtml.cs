using MatSmtp.Services;
using MatSmtp.Services.Auth;
using MatSmtp.UiKit;

namespace MatSmtp.Pages.Admin.Users;

/// <summary>Everybody who can sign in: administrators and users.</summary>
public sealed class IndexModel(UserService users, ICurrentUser current) : PageModelBase
{
    public ToolbarModel Toolbar { get; private set; } = null!;

    public PagerModel Pager { get; private set; } = null!;

    public TableModel Table { get; private set; } = null!;

    public async Task OnGetAsync()
    {
        Head("Benutzer", "Wer sich anmelden kann und was er darf.", ("Einstellungen", null), ("Benutzer", null));

        var result = await users.ListAsync(Request, HttpContext.RequestAborted);
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
