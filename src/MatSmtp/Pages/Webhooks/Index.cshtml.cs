using MatSmtp.Data.Entities;
using MatSmtp.Services;
using MatSmtp.Services.Relay;
using MatSmtp.UiKit;

namespace MatSmtp.Pages.Webhooks;

/// <summary>The addresses that rules hand messages to (HTTP POST with JSON).</summary>
public sealed class IndexModel(WebhookAdminService webhooks) : PageModelBase
{
    public ToolbarModel Toolbar { get; private set; } = null!;

    public PagerModel Pager { get; private set; } = null!;

    public TableModel Table { get; private set; } = null!;

    public async Task OnGetAsync()
    {
        Head("Webhooks", "Adressen, an die Regeln eine Nachricht weitergeben: als HTTP-POST mit JSON, auf Wunsch signiert.", ("Relay", null), ("Webhooks", null));
        var result = await webhooks.ListAsync(Request, HttpContext.RequestAborted);
        Toolbar = result.Toolbar;
        Pager = result.Pager;
        Table = TableModel.Of("webhook-table", "Webhooks", result.Items,
        [
            (new TableColumn("Name"), w => Ui.NameWithSub(w.Name, $"/Webhooks/Edit?id={w.Id}", w.Description)),
            (new TableColumn("Adresse", HideOnPhone: true), w => Ui.Mono(w.Url)),
            (new TableColumn("Inhalt", HideOnPhone: true), w => Ui.Text(w.Payload switch { WebhookPayload.Content => "Inhalt", WebhookPayload.Raw => "Vollständig", _ => "Metadaten" })),
            (new TableColumn("Zuletzt", HideOnPhone: true), w => Ui.Text(w.LastCallDate is null ? "noch nie" : $"{Fmt.Moment(w.LastCallDate)}{(w.LastStatusCode is { } c ? $" · HTTP {c}" : "")}")),
            (new TableColumn("Status"), w => !w.IsEnabled ? Ui.Pill("abgeschaltet", "plain") : w.LastError is not null ? Ui.Pill("Fehler", "error", "alert") : Ui.Pill("bereit", "ok", "check")),
        ],
        w => [new RowAction("edit", "Bearbeiten", $"/Webhooks/Edit?id={w.Id}")]);
        Table = Table with
        {
            EmptyTitle = Toolbar.Total == 0 ? "Noch kein Webhook" : "Nichts gefunden",
            EmptyText = Toolbar.Total == 0 ? "Ein Webhook gibt Nachrichten an ein anderes System weiter, zum Beispiel ein Ticketsystem. Eine Regel bestimmt, welche." : "Ändere die Suche oder den Filter.",
            EmptyIcon = Toolbar.Total == 0 ? "plug" : "search",
        };
    }
}
