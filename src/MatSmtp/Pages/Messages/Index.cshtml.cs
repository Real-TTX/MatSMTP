using MatSmtp.Services;
using MatSmtp.Services.Relay;
using MatSmtp.UiKit;

namespace MatSmtp.Pages.Messages;

/// <summary>Every message the relay received, with what became of it.</summary>
public sealed class IndexModel(MessageService messages, DeliveryService delivery, NavCounts counts, SpoolSignal signal, ActivityLogger log)
    : MessageListModel(messages, delivery, counts, signal, log)
{
    public override bool QueueOnly => false;

    protected override string Action => "/Messages";

    public async Task OnGetAsync()
    {
        Head("Nachrichten", "Alles, was MatSMTP empfangen hat, und was daraus wurde.", ("Nachrichten", null));
        await LoadAsync();
    }
}
