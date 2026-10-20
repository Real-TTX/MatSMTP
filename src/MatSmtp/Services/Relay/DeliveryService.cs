using System.Net.Sockets;
using MailKit;
using MailKit.Net.Smtp;
using MailKit.Security;
using MatSmtp.Configuration;
using MatSmtp.Core.Relay;
using MatSmtp.Data;
using MatSmtp.Data.Entities;
using Microsoft.EntityFrameworkCore;
using MimeKit;

namespace MatSmtp.Services.Relay;

/// <summary>The result of sending one message through an account: sent, to be tried again, or failed for good.</summary>
public sealed record SendOutcome(bool Ok, bool Permanent, string Text)
{
    /// <summary>What DKIM did before the message went out (empty when no key is meant for its sender).</summary>
    public DkimOutcome Dkim { get; init; } = DkimOutcome.None;
}

/// <summary>
/// The spooler: sends the messages of the queue through their SMTP accounts, keeps to the limits of an account, tries again after a temporary error (the waits
/// of <see cref="RetrySchedule"/>) and gives up after the maximum number of attempts or a permanent error (5xx). One message is only ever sent by one worker: the
/// switch to "Sending" is a conditional update.
/// </summary>
public sealed class DeliveryService(MsDbContext db, ConfigStore config, SpoolStore spool, SecretProtector secrets, DkimService dkim, TimeProvider clock, ILogger<DeliveryService> logger)
{
    /// <summary>The ids of the messages that are due now, oldest first.</summary>
    public Task<List<long>> DueAsync(int max, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        return db.Message.AsNoTracking()
            .Where(m => m.Action == RuleAction.Relay && (m.Status == MessageStatus.Queued || m.Status == MessageStatus.Deferred) && (m.NextAttemptDate == null || m.NextAttemptDate <= now))
            .OrderBy(m => m.NextAttemptDate ?? m.ReceivedDate).ThenBy(m => m.Id)
            .Select(m => m.Id).Take(max).ToListAsync(ct);
    }

    /// <summary>After a crash or a restart, a message that was being sent goes back into the queue.</summary>
    public Task<int> RecoverAsync(CancellationToken ct)
        => db.Message.Where(m => m.Status == MessageStatus.Sending).ExecuteUpdateAsync(s => s.SetProperty(m => m.Status, MessageStatus.Queued), ct);

    /// <summary>Sends one message (when nobody else took it) and writes the result.</summary>
    public async Task SendAsync(long id, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var taken = await db.Message
            .Where(m => m.Id == id && (m.Status == MessageStatus.Queued || m.Status == MessageStatus.Deferred))
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.Status, MessageStatus.Sending), ct);
        if (taken == 0)
        {
            return;
        }

        var message = await db.Message.FirstAsync(m => m.Id == id, ct);
        var delivery = config.Current.Delivery;
        var account = await ChooseAccountAsync(message, ct);
        if (account is null)
        {
            await FinishAsync(message, new SendOutcome(false, false, "Es gibt kein aktives SMTP-Konto für diese Nachricht (kein Standardkonto, oder das Konto ist abgeschaltet)."), null, delivery, ct);
            return;
        }

        if (await OverLimitAsync(account, now, ct) is { } wait)
        {
            // Not an attempt: the message waits for the limit of its account.
            message.Status = MessageStatus.Queued;
            message.NextAttemptDate = now + wait;
            await db.SaveChangesAsync(ct);
            return;
        }

        if (!spool.Exists(message.StorageKey))
        {
            await FinishAsync(message, new SendOutcome(false, true, "Der Inhalt der Nachricht ist nicht mehr vorhanden."), account, delivery, ct);
            return;
        }

        MimeMessage mime;
        await using (var stream = spool.OpenRead(message.StorageKey!))
        {
            mime = await MimeMessage.LoadAsync(stream, ct);
        }

        var outcome = await SendThroughAsync(account, mime, message, ct);
        await FinishAsync(message, outcome, account, delivery, ct);
    }

    /// <summary>Sends a test message through an account (the account page): the answer of the server, or what went wrong.</summary>
    public async Task<SendOutcome> SendTestAsync(SmtpAccount account, string to, CancellationToken ct)
    {
        var from = account.ForceFromAddress ?? (account.UserName is { } u && u.Contains('@') ? u : null) ?? $"matsmtp@{config.Current.Smtp.Hostname}";
        var mime = new MimeMessage();
        mime.From.Add(new MailboxAddress("MatSMTP", from));
        mime.To.Add(MailboxAddress.Parse(to));
        mime.Subject = "Testnachricht von MatSMTP";
        mime.Body = new TextPart("plain") { Text = $"Diese Nachricht wurde über das Konto „{account.Name}“ ({account.Host}:{account.Port}) versendet.\n\nMatSMTP" };
        var message = new Message { EnvelopeFrom = from, Recipients = to };
        return await SendThroughAsync(account, mime, message, ct);
    }

    /// <summary>Connects and signs in without sending: is the account right?</summary>
    public async Task<SendOutcome> CheckAsync(SmtpAccount account, CancellationToken ct)
    {
        using var client = new SmtpClient { Timeout = Math.Clamp(account.TimeoutSeconds, 5, 300) * 1000 };
        try
        {
            await ConnectAsync(client, account, ct);
            var caps = string.Join(", ", new[] { client.Capabilities.HasFlag(SmtpCapabilities.StartTLS) ? "STARTTLS" : null, client.Capabilities.HasFlag(SmtpCapabilities.Authentication) ? "AUTH" : null, client.IsSecure ? "verschlüsselt" : "unverschlüsselt" }.Where(s => s is not null));
            await client.DisconnectAsync(true, ct);
            return new SendOutcome(true, false, $"Verbindung und Anmeldung erfolgreich ({caps}).");
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return Classify(ex);
        }
    }

    private async Task<SmtpAccount?> ChooseAccountAsync(Message message, CancellationToken ct)
    {
        if (message.AccountId is { } id)
        {
            var chosen = await db.SmtpAccount.FirstOrDefaultAsync(a => a.Id == id, ct);
            if (chosen is { IsEnabled: true })
            {
                return chosen;
            }

            message.Events.Add(RelayService.Event(clock.GetUtcNow().UtcDateTime, EventLevel.Warning, "Das Konto der Regel ist abgeschaltet oder gelöscht: die Nachricht geht über das Standardkonto."));
        }

        return await db.SmtpAccount.Where(a => a.IsDefault && a.IsEnabled).OrderBy(a => a.Id).FirstOrDefaultAsync(ct);
    }

    /// <summary>How long to wait when the account reached its limit for the minute or the day; null when it may send.</summary>
    private async Task<TimeSpan?> OverLimitAsync(SmtpAccount account, DateTime now, CancellationToken ct)
    {
        if (account.MaxPerMinute > 0)
        {
            var minute = now.AddMinutes(-1);
            if (await db.Message.CountAsync(m => m.AccountId == account.Id && m.SentDate > minute, ct) >= account.MaxPerMinute)
            {
                return TimeSpan.FromSeconds(15);
            }
        }

        if (account.MaxPerDay > 0)
        {
            var day = now.AddDays(-1);
            if (await db.Message.CountAsync(m => m.AccountId == account.Id && m.SentDate > day, ct) >= account.MaxPerDay)
            {
                return TimeSpan.FromMinutes(10);
            }
        }

        return null;
    }

    private async Task<SendOutcome> SendThroughAsync(SmtpAccount account, MimeMessage mime, Message message, CancellationToken ct)
    {
        var envelopeFrom = message.EnvelopeFrom;
        if (!string.IsNullOrWhiteSpace(account.ForceFromAddress))
        {
            var forced = account.ForceFromAddress.Trim();
            var old = mime.From.Mailboxes.FirstOrDefault();
            if (old is not null && !string.Equals(old.Address, forced, StringComparison.OrdinalIgnoreCase))
            {
                if (mime.ReplyTo.Count == 0)
                {
                    mime.ReplyTo.Add(new MailboxAddress(old.Name, old.Address));
                }

                mime.Headers.Replace("X-MatSMTP-Original-From", old.Address);
            }

            mime.From.Clear();
            mime.From.Add(new MailboxAddress(old?.Name ?? "", forced));
            mime.Sender = null;
            envelopeFrom = forced;
        }

        var recipients = message.Recipients.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Concat((message.Bcc ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(MailboxAddress.Parse)
            .ToList();
        if (recipients.Count == 0)
        {
            return new SendOutcome(false, true, "Die Nachricht hat keinen Empfänger.");
        }

        // The last change to the message: anything after it (a header, another encoding) would break the signature.
        var signed = await dkim.SignAsync(mime, ct);

        // The bounce address <> stays empty; MailKit wants a mailbox, so an empty sender is taken from the header.
        var sender = string.IsNullOrEmpty(envelopeFrom) ? mime.From.Mailboxes.FirstOrDefault() ?? new MailboxAddress("", "") : MailboxAddress.Parse(envelopeFrom);
        using var client = new SmtpClient { Timeout = Math.Clamp(account.TimeoutSeconds, 5, 300) * 1000 };
        try
        {
            await ConnectAsync(client, account, ct);
            var response = await client.SendAsync(mime, sender, recipients, ct);
            await client.DisconnectAsync(true, ct);
            return new SendOutcome(true, false, string.IsNullOrWhiteSpace(response) ? "250 OK" : response.Trim()) { Dkim = signed };
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return Classify(ex) with { Dkim = signed };
        }
    }

    private async Task ConnectAsync(SmtpClient client, SmtpAccount account, CancellationToken ct)
    {
        var options = account.Security switch
        {
            SmtpSecurity.StartTls => SecureSocketOptions.StartTls,
            SmtpSecurity.SslOnConnect => SecureSocketOptions.SslOnConnect,
            SmtpSecurity.None => SecureSocketOptions.None,
            _ => SecureSocketOptions.Auto,
        };
        client.LocalDomain = config.Current.Smtp.Hostname;
        await client.ConnectAsync(account.Host, account.Port, options, ct);
        if (!string.IsNullOrEmpty(account.UserName))
        {
            await client.AuthenticateAsync(account.UserName, secrets.Unprotect(account.PasswordProtected), ct);
        }
    }

    /// <summary>A 5xx answer is permanent; a 4xx answer, a network problem, a timeout or a failed sign-in is tried again.</summary>
    public static SendOutcome Classify(Exception ex) => ex switch
    {
        SmtpCommandException c => new SendOutcome(false, (int)c.StatusCode >= 500 && c.ErrorCode != SmtpErrorCode.UnexpectedStatusCode || (int)c.StatusCode >= 550,
            $"{(int)c.StatusCode} {c.Message}".Trim()),
        AuthenticationException a => new SendOutcome(false, false, "Anmeldung am Server fehlgeschlagen: " + a.Message),
        SslHandshakeException s => new SendOutcome(false, false, "TLS-Verbindung fehlgeschlagen: " + s.Message),
        SmtpProtocolException p => new SendOutcome(false, false, "Protokollfehler: " + p.Message),
        SocketException so => new SendOutcome(false, false, "Keine Verbindung zum Server: " + so.Message),
        IOException io => new SendOutcome(false, false, "Verbindung unterbrochen: " + io.Message),
        TimeoutException or OperationCanceledException => new SendOutcome(false, false, "Zeitüberschreitung: der Server antwortet nicht."),
        _ => new SendOutcome(false, false, ex.Message),
    };

    private async Task FinishAsync(Message message, SendOutcome outcome, SmtpAccount? account, DeliveryConfig delivery, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        message.Attempts++;
        if (outcome.Dkim.Problem is { } problem)
        {
            message.Events.Add(RelayService.Event(now, EventLevel.Warning, problem));
        }

        if (account is not null)
        {
            message.AccountId = account.Id;
            message.AccountName = account.Name;
        }

        if (outcome.Ok)
        {
            message.Status = MessageStatus.Sent;
            message.SentDate = now;
            message.NextAttemptDate = null;
            message.LastError = null;
            message.RemoteResponse = outcome.Text.Length > 500 ? outcome.Text[..500] : outcome.Text;
            var signedBy = outcome.Dkim.Key is { } key ? $", DKIM-signiert für {key.Domain} (Selektor {key.Selector})" : "";
            message.Events.Add(RelayService.Event(now, EventLevel.Success, $"Versendet über „{account!.Name}“ ({account.Host}){signedBy}: {message.RemoteResponse}"));
            account.LastUsedDate = now;
            if (delivery.KeepBodyDays == 0)
            {
                spool.Delete(message.StorageKey);
                message.StorageKey = null;
            }
        }
        else
        {
            var final = outcome.Permanent || message.Attempts >= Math.Max(1, delivery.MaxAttempts);
            message.LastError = outcome.Text.Length > 1000 ? outcome.Text[..1000] : outcome.Text;
            message.Status = final ? MessageStatus.Failed : MessageStatus.Deferred;
            message.NextAttemptDate = final ? null : now + RetrySchedule.Delay(delivery.RetryMinutes, message.Attempts);
            var where = account is null ? "" : $" über „{account.Name}“";
            message.Events.Add(RelayService.Event(now, final ? EventLevel.Error : EventLevel.Warning,
                final
                    ? $"Versuch {message.Attempts}{where} fehlgeschlagen, endgültig: {outcome.Text}"
                    : $"Versuch {message.Attempts}{where} fehlgeschlagen: {outcome.Text}. Nächster Versuch {Fmt.Moment(message.NextAttemptDate)}."));
            if (account is not null)
            {
                account.LastError = message.LastError;
                account.LastErrorDate = now;
            }

            logger.LogWarning("Message {Id} not sent (attempt {Attempt}): {Error}", message.Id, message.Attempts, outcome.Text);
        }

        await db.SaveChangesAsync(ct);
    }

    /// <summary>Puts failed or deferred messages back into the queue for an attempt now (the messages page).</summary>
    public async Task<int> RetryAsync(IReadOnlyCollection<long> ids, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var messages = await db.Message.Where(m => ids.Contains(m.Id) && m.Action == RuleAction.Relay && (m.Status == MessageStatus.Failed || m.Status == MessageStatus.Deferred || m.Status == MessageStatus.Queued)).ToListAsync(ct);
        var count = 0;
        foreach (var m in messages)
        {
            if (!spool.Exists(m.StorageKey))
            {
                continue;
            }

            if (m.Status == MessageStatus.Failed)
            {
                m.Attempts = 0;
            }

            m.Status = MessageStatus.Queued;
            m.NextAttemptDate = now;
            m.Events.Add(RelayService.Event(now, EventLevel.Info, "Von Hand erneut in die Warteschlange gestellt."));
            count++;
        }

        await db.SaveChangesAsync(ct);
        return count;
    }

    /// <summary>The failed messages received since <paramref name="since"/> go back into the queue (their content still there), with fresh attempts.</summary>
    public async Task<int> RetryFailedAsync(DateTime since, CancellationToken ct)
    {
        var ids = await db.Message.Where(m => m.Status == MessageStatus.Failed && m.Action == RuleAction.Relay && m.ReceivedDate >= since).Select(m => m.Id).ToListAsync(ct);
        return ids.Count == 0 ? 0 : await RetryAsync(ids, ct);
    }

    /// <summary>Every deferred message and every one that waits for the limit of its account is tried now instead of at its next attempt.</summary>
    public async Task<int> FlushDeferredAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var ids = await db.Message
            .Where(m => m.Action == RuleAction.Relay && (m.Status == MessageStatus.Deferred || (m.Status == MessageStatus.Queued && m.NextAttemptDate > now)))
            .Select(m => m.Id).ToListAsync(ct);
        return ids.Count == 0 ? 0 : await RetryAsync(ids, ct);
    }
}

/// <summary>Runs the spooler and the webhooks: wakes up when a message comes in and at least every five seconds.</summary>
public sealed class DeliveryWorker(IServiceScopeFactory scopes, ConfigStore config, SpoolSignal signal, ILogger<DeliveryWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            using (var scope = scopes.CreateScope())
            {
                var recovered = await scope.ServiceProvider.GetRequiredService<DeliveryService>().RecoverAsync(stoppingToken);
                if (recovered > 0)
                {
                    logger.LogInformation("{Count} message(s) that were being sent are back in the queue.", recovered);
                }
            }

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await RoundAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "A round of the spooler failed.");
                }

                await signal.WaitAsync(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // the application is stopping
        }
    }

    private async Task RoundAsync(CancellationToken ct)
    {
        var delivery = config.Current.Delivery;
        if (!delivery.Paused)
        {
            List<long> due;
            using (var scope = scopes.CreateScope())
            {
                due = await scope.ServiceProvider.GetRequiredService<DeliveryService>().DueAsync(Math.Clamp(delivery.Parallel, 1, 32) * 5, ct);
            }

            await Parallel.ForEachAsync(due, new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(delivery.Parallel, 1, 32), CancellationToken = ct }, async (id, token) =>
            {
                using var scope = scopes.CreateScope();
                try
                {
                    await scope.ServiceProvider.GetRequiredService<DeliveryService>().SendAsync(id, token);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Message {Id} could not be processed.", id);
                }
            });
        }

        using (var scope = scopes.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<WebhookService>().RunDueAsync(ct);
        }
    }
}
