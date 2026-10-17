using MatSmtp.Data;
using MatSmtp.Services;
using MatSmtp.Services.Relay;
using MatSmtp.UiKit;

namespace MatSmtp.Pages.Messages;

/// <summary>Every message the relay received, with what became of it.</summary>
public sealed class IndexModel(MsDbContext db, DeliveryService delivery, SpoolStore spool, NavCounts counts, SpoolSignal signal, ActivityLogger log, TimeProvider clock)
    : MessageListModel(db, delivery, spool, counts, signal, log, clock)
{
    public override bool QueueOnly => false;

    protected override string Action => "/Messages";

    public async Task OnGetAsync()
    {
        Head("Nachrichten", "Alles, was MatSMTP empfangen hat, und was daraus wurde.", ("Nachrichten", null));
        await LoadAsync();
    }
}
