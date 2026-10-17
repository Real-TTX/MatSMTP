using MatSmtp.Configuration;
using MatSmtp.Data;
using MatSmtp.Data.Entities;
using MatSmtp.Services;
using MatSmtp.Services.Relay;
using Microsoft.EntityFrameworkCore;

namespace MatSmtp.Pages;

/// <summary>The overview: does the receiver run, what came in and went out today, what waits, what failed, how the accounts are doing.</summary>
public sealed class IndexModel(MsDbContext db, ConfigStore config, SmtpListener listener, CertificateService certificates, ICurrentUser current, TimeProvider clock) : PageModelBase
{
    public sealed record AccountRow(SmtpAccount Account, int SentToday, int Waiting);

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

    public List<AccountRow> Accounts { get; private set; } = [];

    public List<Message> Recent { get; private set; } = [];

    public List<HourBar> Hours { get; private set; } = [];

    public async Task OnGetAsync()
    {
        Head("Übersicht", "Empfang, Warteschlange und Versand auf einen Blick.");
        var ct = HttpContext.RequestAborted;
        var now = clock.GetUtcNow().UtcDateTime;
        var midnight = Fmt.UtcFromLocal(Fmt.Local(now).Date);
        var day = now.AddDays(-1);

        ReceivedToday = await db.Message.CountAsync(m => m.ReceivedDate >= midnight, ct);
        SentToday = await db.Message.CountAsync(m => m.SentDate >= midnight && (m.Status == MessageStatus.Sent || m.Status == MessageStatus.Delivered), ct);
        Waiting = await db.Message.CountAsync(m => m.Status == MessageStatus.Queued || m.Status == MessageStatus.Sending || m.Status == MessageStatus.Deferred, ct);
        Deferred = await db.Message.CountAsync(m => m.Status == MessageStatus.Deferred, ct);
        FailedDay = await db.Message.CountAsync(m => m.Status == MessageStatus.Failed && m.ReceivedDate >= day, ct);
        RejectedToday = await db.Message.CountAsync(m => m.Status == MessageStatus.Rejected && m.ReceivedDate >= midnight, ct);

        var accounts = await db.SmtpAccount.AsNoTracking().OrderByDescending(a => a.IsDefault).ThenBy(a => a.Name).ToListAsync(ct);
        HasAccounts = accounts.Count > 0;
        HasDefaultAccount = accounts.Any(a => a.IsDefault && a.IsEnabled);
        var sent = await db.Message.Where(m => m.SentDate >= midnight && m.AccountId != null).GroupBy(m => m.AccountId).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct);
        var waiting = await db.Message.Where(m => m.AccountId != null && (m.Status == MessageStatus.Queued || m.Status == MessageStatus.Deferred)).GroupBy(m => m.AccountId).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct);
        Accounts = [.. accounts.Select(a => new AccountRow(a, sent.FirstOrDefault(s => s.Key == a.Id)?.Count ?? 0, waiting.FirstOrDefault(w => w.Key == a.Id)?.Count ?? 0))];

        Recent = await db.Message.AsNoTracking().OrderByDescending(m => m.Id).Take(8).ToListAsync(ct);

        // The last 24 hours, hour by hour (local time): what came in, and what of it failed or was refused.
        var from = now.AddHours(-23);
        var start = new DateTime(from.Year, from.Month, from.Day, from.Hour, 0, 0, DateTimeKind.Utc);
        var received = await db.Message.Where(m => m.ReceivedDate >= start).Select(m => new { m.ReceivedDate, m.Status }).ToListAsync(ct);
        var max = 1;
        var buckets = new List<(DateTime Hour, int Received, int Failed)>();
        for (var h = 0; h < 24; h++)
        {
            var hour = start.AddHours(h);
            var inHour = received.Where(m => m.ReceivedDate >= hour && m.ReceivedDate < hour.AddHours(1)).ToList();
            buckets.Add((hour, inHour.Count, inHour.Count(m => m.Status is MessageStatus.Failed or MessageStatus.Rejected)));
            max = Math.Max(max, inHour.Count);
        }

        Hours = [.. buckets.Select(b => new HourBar(Fmt.Local(b.Hour).ToString("HH", Fmt.Culture), b.Received, b.Failed, (int)Math.Round(b.Received * 100.0 / max)))];

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
