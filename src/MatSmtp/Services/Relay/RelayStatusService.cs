using MatSmtp.Configuration;
using MatSmtp.Data;
using MatSmtp.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace MatSmtp.Services.Relay;

/// <summary>An account on the overview: what it sent today, what waits for it, and whether its last attempt failed.</summary>
public sealed record AccountRow(SmtpAccount Account, int SentToday, int Waiting)
{
    /// <summary>The last attempt through the account failed (the error is newer than the last delivery).</summary>
    public bool Failing => Account.LastErrorDate is { } error && (Account.LastUsedDate is not { } used || error > used);
}

/// <summary>One hour of the chart on the overview (UTC): what came in, and what of it failed or was refused.</summary>
public sealed record HourCount(DateTime HourUtc, int Received, int Failed);

/// <summary>What the overview shows.</summary>
public sealed record RelayOverview(
    int ReceivedToday,
    int SentToday,
    int Waiting,
    int Deferred,
    int FailedDay,
    int RejectedToday,
    IReadOnlyList<AccountRow> Accounts,
    IReadOnlyList<Message> Recent,
    IReadOnlyList<HourCount> Hours)
{
    public bool HasAccounts => Accounts.Count > 0;

    public bool HasDefaultAccount => Accounts.Any(a => a.Account.IsDefault && a.Account.IsEnabled);
}

/// <summary>The numbers of the last 24 hours, for the platform tiles (<c>/api/state</c>).</summary>
public sealed record RelayCounts(
    int Queued,
    int Deferred,
    int Failed24h,
    int Sent24h,
    int Received24h,
    int Accounts,
    int AccountsFailing,
    bool HasDefaultAccount,
    DateTime? LastDeliveryUtc,
    int WebhooksFailing);

/// <summary>One check of the health of the relay (<c>/api/health</c>): <c>ok</c>, <c>degraded</c> or <c>down</c>, with a message for a person.</summary>
public sealed record RelayHealth(string Name, string Status, string? Message);

/// <summary>
/// The state of the relay in numbers: the overview, the tiles and the health check of the platform, and the badges of the menu read it here, so that
/// no page counts messages itself.
/// </summary>
public sealed class RelayStatusService(MsDbContext db, ConfigStore config, SmtpListener listener, TimeProvider clock)
{
    /// <summary>Everything the overview shows; "today" is the local day of the installation.</summary>
    public async Task<RelayOverview> OverviewAsync(CancellationToken ct = default)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var midnight = Fmt.UtcFromLocal(Fmt.Local(now).Date);
        var day = now.AddDays(-1);

        var receivedToday = await db.Message.CountAsync(m => m.ReceivedDate >= midnight, ct);
        var sentToday = await db.Message.CountAsync(m => m.SentDate >= midnight && (m.Status == MessageStatus.Sent || m.Status == MessageStatus.Delivered), ct);
        var waiting = await db.Message.CountAsync(m => m.Status == MessageStatus.Queued || m.Status == MessageStatus.Sending || m.Status == MessageStatus.Deferred, ct);
        var deferred = await db.Message.CountAsync(m => m.Status == MessageStatus.Deferred, ct);
        var failedDay = await db.Message.CountAsync(m => m.Status == MessageStatus.Failed && m.ReceivedDate >= day, ct);
        var rejectedToday = await db.Message.CountAsync(m => m.Status == MessageStatus.Rejected && m.ReceivedDate >= midnight, ct);

        var accounts = await AccountRowsAsync(midnight, ct);
        var recent = await db.Message.AsNoTracking().OrderByDescending(m => m.Id).Take(8).ToListAsync(ct);

        // The last 24 hours, hour by hour.
        var from = now.AddHours(-23);
        var start = new DateTime(from.Year, from.Month, from.Day, from.Hour, 0, 0, DateTimeKind.Utc);
        var received = await db.Message.Where(m => m.ReceivedDate >= start).Select(m => new { m.ReceivedDate, m.Status }).ToListAsync(ct);
        var hours = Enumerable.Range(0, 24).Select(h =>
        {
            var hour = start.AddHours(h);
            var inHour = received.Where(m => m.ReceivedDate >= hour && m.ReceivedDate < hour.AddHours(1)).ToList();
            return new HourCount(hour, inHour.Count, inHour.Count(m => m.Status is MessageStatus.Failed or MessageStatus.Rejected));
        }).ToList();

        return new RelayOverview(receivedToday, sentToday, waiting, deferred, failedDay, rejectedToday, accounts, recent, hours);
    }

    /// <summary>The accounts with what they sent since <paramref name="since"/> and what waits for them, the default account first.</summary>
    public async Task<List<AccountRow>> AccountRowsAsync(DateTime since, CancellationToken ct = default)
    {
        var accounts = await db.SmtpAccount.AsNoTracking().OrderByDescending(a => a.IsDefault).ThenBy(a => a.Name).ToListAsync(ct);
        var sent = await db.Message.Where(m => m.SentDate >= since && m.AccountId != null).GroupBy(m => m.AccountId).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct);
        var waiting = await db.Message.Where(m => m.AccountId != null && (m.Status == MessageStatus.Queued || m.Status == MessageStatus.Deferred))
            .GroupBy(m => m.AccountId).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct);
        return [.. accounts.Select(a => new AccountRow(a, sent.FirstOrDefault(s => s.Key == a.Id)?.Count ?? 0, waiting.FirstOrDefault(w => w.Key == a.Id)?.Count ?? 0))];
    }

    /// <summary>The numbers of the platform tiles: the queue now, the last 24 hours, the accounts and the webhooks.</summary>
    public async Task<RelayCounts> CountsAsync(CancellationToken ct = default)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var day = now.AddDays(-1);
        var accounts = await AccountRowsAsync(day, ct);
        var lastDelivery = await db.Message.Where(m => m.SentDate != null && (m.Status == MessageStatus.Sent || m.Status == MessageStatus.Delivered))
            .MaxAsync(m => (DateTime?)m.SentDate, ct);
        var hooksFailing = await db.WebhookDelivery.Where(d => d.Status == WebhookDeliveryStatus.Failed && d.DoneDate >= day && d.WebhookId != null)
            .Select(d => d.WebhookId).Distinct().CountAsync(ct);

        return new RelayCounts(
            await db.Message.CountAsync(m => m.Status == MessageStatus.Queued || m.Status == MessageStatus.Sending, ct),
            await db.Message.CountAsync(m => m.Status == MessageStatus.Deferred, ct),
            await db.Message.CountAsync(m => m.Status == MessageStatus.Failed && m.ReceivedDate >= day, ct),
            await db.Message.CountAsync(m => m.SentDate >= day && (m.Status == MessageStatus.Sent || m.Status == MessageStatus.Delivered), ct),
            await db.Message.CountAsync(m => m.ReceivedDate >= day, ct),
            accounts.Count(a => a.Account.IsEnabled),
            accounts.Count(a => a.Account.IsEnabled && a.Failing),
            accounts.Any(a => a.Account.IsDefault && a.Account.IsEnabled),
            lastDelivery,
            hooksFailing);
    }

    /// <summary>The badges of the menu: what waits, what was deferred, what failed.</summary>
    public async Task<(int Queue, int Deferred, int Failed)> BadgesAsync(CancellationToken ct = default) => (
        await db.Message.CountAsync(m => m.Status == MessageStatus.Queued || m.Status == MessageStatus.Sending, ct),
        await db.Message.CountAsync(m => m.Status == MessageStatus.Deferred, ct),
        await db.Message.CountAsync(m => m.Status == MessageStatus.Failed, ct));

    /// <summary>
    /// The checks of <c>/api/health</c>: the database, the SMTP receiver and the accounts. Cheap and without a call outside: the accounts are judged by
    /// their last attempt. A receiver that cannot listen is <c>down</c> (the relay cannot take mail); one that is switched off, no default account or a
    /// failing account is <c>degraded</c>.
    /// </summary>
    public async Task<IReadOnlyList<RelayHealth>> HealthAsync(Func<string, string, string> t, CancellationToken ct = default)
    {
        var checks = new List<RelayHealth>();
        try
        {
            var ok = await db.Database.CanConnectAsync(ct);
            checks.Add(ok ? new("database", "ok", null) : new("database", "down", t("Die Datenbank ist nicht erreichbar.", "The database cannot be reached.")));
            if (!ok)
            {
                return checks;
            }
        }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or InvalidOperationException)
        {
            checks.Add(new("database", "down", ex.Message));
            return checks;
        }

        checks.Add(ListenerHealth(t));

        var accounts = await db.SmtpAccount.AsNoTracking().Where(a => a.IsEnabled).ToListAsync(ct);
        var rows = accounts.Select(a => new AccountRow(a, 0, 0)).ToList();
        var failing = rows.Where(r => r.Failing).Select(r => r.Account.Name).ToList();
        if (!accounts.Any(a => a.IsDefault))
        {
            checks.Add(new("accounts", "degraded", t("Es gibt kein aktives Standardkonto: Nachrichten ohne Regel werden nicht versendet.", "There is no enabled default account: messages without a rule are not sent.")));
        }
        else if (failing.Count > 0)
        {
            checks.Add(new("accounts", "degraded", t($"Letzter Versuch fehlgeschlagen: {string.Join(", ", failing)}.", $"Last attempt failed: {string.Join(", ", failing)}.")));
        }
        else
        {
            checks.Add(new("accounts", "ok", null));
        }

        return checks;
    }

    private RelayHealth ListenerHealth(Func<string, string, string> t)
    {
        var smtp = config.Current.Smtp;
        var state = listener.State;
        if (state.Running)
        {
            return new("smtp", "ok", null);
        }

        if (!smtp.Enabled)
        {
            return new("smtp", "degraded", t("Der Empfang ist abgeschaltet.", "Receiving is switched off."));
        }

        // Not running and no error yet: it is starting.
        return state.Error is null
            ? new("smtp", "degraded", t("Der Empfang startet.", "Receiving is starting."))
            : new("smtp", "down", t($"Der Empfang läuft nicht: {state.Error}", $"Receiving is not running: {state.Error}"));
    }
}
