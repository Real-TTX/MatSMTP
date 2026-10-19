using MatSmtp.Configuration;
using MatSmtp.Data.Entities;
using MatSmtp.Services;
using MatSmtp.Services.Relay;

namespace MatSmtp.Pages;

/// <summary>The overview: does the receiver run, what came in and went out today, what waits, what failed, how the accounts are doing.</summary>
public sealed class IndexModel(RelayStatusService status, ConfigStore config, SmtpListener listener, CertificateService certificates, ICurrentUser current) : PageModelBase
{
    public sealed record HourBar(string Label, int Received, int Failed, int Percent);

    public bool IsAdmin => current.IsAdmin;

    public ListenerState Listener => listener.State;

    public CertificateInfo? Certificate { get; private set; }

    public SmtpConfig Smtp => config.Current.Smtp;

    public bool Paused => config.Current.Delivery.Paused;

    public int ReceivedToday { get; private set; }

    public int SentToday { get; private set; }

    public int Waiting { get; private set; }

    public int Deferred { get; private set; }

    public int FailedDay { get; private set; }

    public int RejectedToday { get; private set; }

    public bool HasDefaultAccount { get; private set; }

    public bool HasAccounts { get; private set; }

    public IReadOnlyList<AccountRow> Accounts { get; private set; } = [];

    public IReadOnlyList<Message> Recent { get; private set; } = [];

    public List<HourBar> Hours { get; private set; } = [];

    public async Task OnGetAsync()
    {
        Head("Übersicht", "Empfang, Warteschlange und Versand auf einen Blick.");
        var o = await status.OverviewAsync(HttpContext.RequestAborted);
        (ReceivedToday, SentToday, Waiting, Deferred, FailedDay, RejectedToday) = (o.ReceivedToday, o.SentToday, o.Waiting, o.Deferred, o.FailedDay, o.RejectedToday);
        (Accounts, Recent, HasAccounts, HasDefaultAccount) = (o.Accounts, o.Recent, o.HasAccounts, o.HasDefaultAccount);

        // The bars in local hours, each as a share of the busiest hour.
        var max = Math.Max(1, o.Hours.Max(h => h.Received));
        Hours = [.. o.Hours.Select(h => new HourBar(Fmt.Local(h.HourUtc).ToString("HH", Fmt.Culture), h.Received, h.Failed, (int)Math.Round(h.Received * 100.0 / max)))];

        try
        {
            Certificate = certificates.Info();
        }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or IOException or UnauthorizedAccessException)
        {
            Certificate = null;
        }
    }
}
