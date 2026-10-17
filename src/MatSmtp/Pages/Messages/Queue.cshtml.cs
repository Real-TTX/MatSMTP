using MatSmtp.Data;
using MatSmtp.Services;
using MatSmtp.Services.Relay;
using MatSmtp.UiKit;

namespace MatSmtp.Pages.Messages;

/// <summary>What still waits: new messages, the one being sent, and the ones that wait for their next attempt.</summary>
public sealed class QueueModel(MsDbContext db, DeliveryService delivery, SpoolStore spool, NavCounts counts, SpoolSignal signal, ActivityLogger log, TimeProvider clock)
    : MessageListModel(db, delivery, spool, counts, signal, log, clock)
{
    public override bool QueueOnly => true;

    protected override string Action => "/Messages/Queue";

    public async Task OnGetAsync()
    {
        Head("Warteschlange", "Nachrichten, die noch versendet werden: neu, gerade im Versand oder verzögert bis zum nächsten Versuch.", ("Nachrichten", "/Messages"), ("Warteschlange", null));
        await LoadAsync();
    }
}
