using System.Globalization;
using System.Net;
using MatSmtp.Configuration;
using MatSmtp.Core.Relay;
using MatSmtp.Data;
using MatSmtp.Data.Entities;
using MatSmtp.UiKit;
using Microsoft.EntityFrameworkCore;
using MimeKit;

namespace MatSmtp.Services.Relay;

/// <summary>Who handed a message over: what the SMTP session knows.</summary>
public sealed record SessionInfo(IPAddress? ClientIp, string? Helo, string? AuthUser, int ServerPort, bool IsTls);

/// <summary>What the receiver answers: accepted (with the id of the message) or refused with an SMTP code and a text.</summary>
public sealed record ReceiveResult(bool Accepted, int Code, string Text, long? MessageId = null);

/// <summary>The result of a dry run of the rules (the rule tester): the decision and what it means in words.</summary>
public sealed record RuleTestResult(RuleDecision Decision, string? AccountName, IReadOnlyList<string> WebhookNames, string? Problem);

/// <summary>
/// Takes a received message: asks the rules what to do (<see cref="RuleEngine"/>), rewrites the sender, keeps the content in the spool folder, writes the message
/// with its first history into the database and wakes the spooler. A rule that rejects answers the application at once (550), nothing is kept but the log line.
/// </summary>
public sealed class RelayService(MsDbContext db, ConfigStore config, SpoolStore spool, SpoolSignal signal, RelayUserService relayUsers, TimeProvider clock, ILogger<RelayService> logger)
{
    /// <summary>The enabled rules in their order, as the engine wants them.</summary>
    public async Task<List<RuleSpec>> LoadRulesAsync(CancellationToken ct = default)
    {
        var rules = await db.Rule.AsNoTracking().Include(r => r.MatchRelayUser).Where(r => r.IsEnabled).OrderBy(r => r.Position).ThenBy(r => r.Id).ToListAsync(ct);
        return [.. rules.Select(ToSpec)];
    }

    public static RuleSpec ToSpec(Rule r) => new()
    {
        Id = r.Id,
        Name = r.Name,
        Senders = r.MatchSenders,
        Recipients = r.MatchRecipients,
        Networks = r.MatchNetworks,
        Auth = r.MatchAuth,
        // A rule for a relay user that was deleted matches nobody (the user name can never be the empty marker).
        RelayUser = r.MatchRelayUserId is null ? null : r.MatchRelayUser?.UserName ?? "\0deleted",
        Subject = r.MatchSubject,
        Action = r.Action,
        AccountId = r.AccountId,
        WebhookId = r.WebhookId,
        RewriteFrom = r.RewriteFrom,
        RewriteFromName = r.RewriteFromName,
        RewriteHeader = r.RewriteHeader,
        ReplyToOriginal = r.ReplyToOriginal,
        AddBcc = r.AddBcc,
        RejectMessage = r.RejectMessage,
    };

    /// <summary>Receives a message. <paramref name="recipients"/> are the RCPT TO addresses of the envelope.</summary>
    public async Task<ReceiveResult> ReceiveAsync(SessionInfo session, string envelopeFrom, IReadOnlyList<string> recipients, ReadOnlyMemory<byte> raw, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        MimeMessage mime;
        try
        {
            using var stream = new MemoryStream(raw.ToArray(), writable: false);
            mime = await MimeMessage.LoadAsync(stream, ct);
        }
        catch (FormatException ex)
        {
            logger.LogWarning(ex, "A message from {Ip} could not be parsed.", session.ClientIp);
            return new ReceiveResult(false, 554, "5.6.0 Die Nachricht ist kein gültiges E-Mail-Format.");
        }

        // A sender without a real domain (root, app@localhost) gets the default domain first: the relay user and the rules see the address that leaves.
        var givenFrom = envelopeFrom;
        var headerFrom = mime.From.Mailboxes.FirstOrDefault();
        var givenHeaderFrom = headerFrom?.Address;
        var defaultDomain = config.Current.Smtp.DefaultSenderDomain;
        envelopeFrom = SenderRewrite.CompleteDomain(envelopeFrom, defaultDomain);
        if (headerFrom is not null)
        {
            headerFrom.Address = SenderRewrite.CompleteDomain(headerFrom.Address, defaultDomain);
        }

        var completed = envelopeFrom != givenFrom || headerFrom?.Address != givenHeaderFrom;
        var facts = new MessageFacts
        {
            EnvelopeFrom = envelopeFrom,
            HeaderFrom = headerFrom?.Address,
            HeaderFromName = headerFrom?.Name,
            Recipients = recipients,
            ClientIp = session.ClientIp,
            AuthUser = session.AuthUser,
            Subject = mime.Subject,
        };

        var message = new Message
        {
            ReceivedDate = now,
            ClientIp = session.ClientIp is null ? null : IpNetwork.Normalize(session.ClientIp).ToString(),
            HeloName = Cut(session.Helo, 200),
            ServerPort = session.ServerPort,
            IsTls = session.IsTls,
            AuthUser = session.AuthUser,
            EnvelopeFrom = envelopeFrom,
            HeaderFrom = Cut(headerFrom?.ToString(), 300),
            Recipients = string.Join(", ", recipients),
            RecipientCount = recipients.Count,
            Subject = Cut(mime.Subject, 500),
            MessageIdHeader = Cut(mime.MessageId, 300),
            SizeBytes = raw.Length,
        };

        if (completed)
        {
            message.OriginalFrom = Cut(givenHeaderFrom ?? givenFrom, 300);
            mime.Headers.Replace("X-MatSMTP-Original-From", givenHeaderFrom ?? givenFrom);
        }

        // A relay user may be limited to some senders: an application that signed in must not send as anybody, neither in the envelope nor in the header.
        if (session.AuthUser is not null && await relayUsers.DisallowedSenderAsync(session.AuthUser, envelopeFrom, headerFrom?.Address, ct, givenFrom, givenHeaderFrom) is { } refused)
        {
            var text = $"Der Zugang {session.AuthUser} darf nicht als {refused} senden.";
            await RecordRefusedAsync(message, MessageStatus.Rejected, text, null, ct);
            return new ReceiveResult(false, 550, "5.7.1 " + text);
        }

        var decision = RuleEngine.Evaluate(await LoadRulesAsync(ct), facts);
        message.Rules = decision.Matched.Count == 0 ? null : Cut(string.Join(", ", decision.Matched), 1000);
        message.Action = decision.Action;
        await CountHitsAsync(decision.MatchedIds, now, ct);

        if (decision.Action == RuleAction.Reject)
        {
            await RecordRefusedAsync(message, MessageStatus.Rejected, $"Abgewiesen durch die Regel „{decision.DecidedBy}“: {decision.RejectMessage}", decision, ct);
            return new ReceiveResult(false, 550, "5.7.1 " + decision.RejectMessage);
        }

        if (decision.Action == RuleAction.Drop)
        {
            await RecordRefusedAsync(message, MessageStatus.Dropped, $"Verworfen durch die Regel „{decision.DecidedBy}“.", decision, ct);
            return new ReceiveResult(true, 250, "2.0.0 OK");
        }

        Rewrite(mime, decision, message);
        if (config.Current.Delivery.AddReceivedHeader)
        {
            mime.Headers.Insert(0, HeaderId.Received, ReceivedHeader(session, now));
        }

        byte[] bytes;
        using (var output = new MemoryStream())
        {
            await mime.WriteToAsync(FormatOptions.Default, output, ct);
            bytes = output.ToArray();
        }

        message.StorageKey = await spool.SaveAsync(bytes, now, ct);
        message.SizeBytes = bytes.Length;
        message.AccountId = decision.AccountId;
        message.Bcc = decision.Bcc.Count == 0 ? null : string.Join(", ", decision.Bcc);
        message.Status = MessageStatus.Queued;
        message.Events.Add(Event(now, EventLevel.Info, $"Empfangen von {message.ClientIp ?? "unbekannt"}{(session.AuthUser is null ? "" : $" (Zugang {session.AuthUser})")}{(session.IsTls ? " über TLS" : "")}, {Fmt.FileSize(bytes.Length)}, {Ui.CountNoun(recipients.Count, "Empfänger")}."));
        message.Events.Add(Event(now, EventLevel.Info, Describe(decision)));
        if (completed)
        {
            message.Events.Add(Event(now, EventLevel.Info, $"Absender um die Standarddomain ergänzt: {givenHeaderFrom ?? givenFrom} → {headerFrom?.Address ?? envelopeFrom}."));
        }

        if (decision.WebhookIds.Count > 0)
        {
            var hooks = await db.Webhook.AsNoTracking().Where(w => decision.WebhookIds.Contains(w.Id)).ToListAsync(ct);
            foreach (var hook in hooks)
            {
                if (!hook.IsEnabled)
                {
                    message.Events.Add(Event(now, EventLevel.Warning, $"Webhook „{hook.Name}“ ist abgeschaltet und wird nicht aufgerufen."));
                    continue;
                }

                message.WebhookDeliveries.Add(new WebhookDelivery { WebhookId = hook.Id, WebhookName = hook.Name, NextAttemptDate = now });
            }
        }

        if (decision.Action == RuleAction.WebhookOnly && message.WebhookDeliveries.Count == 0)
        {
            message.Status = MessageStatus.Failed;
            message.LastError = "Die Regel will die Nachricht nur an einen Webhook geben, aber es gibt keinen aktiven.";
            message.Events.Add(Event(now, EventLevel.Error, message.LastError));
        }

        db.Message.Add(message);
        await db.SaveChangesAsync(ct);
        signal.Wake();
        return new ReceiveResult(true, 250, $"2.0.0 OK, angenommen als {message.Id.ToString(CultureInfo.InvariantCulture)}", message.Id);
    }

    /// <summary>The rule tester: what the rules would do with a message like this, without receiving one.</summary>
    public async Task<RuleTestResult> TestAsync(MessageFacts facts, CancellationToken ct = default)
    {
        var decision = RuleEngine.Evaluate(await LoadRulesAsync(ct), facts);
        string? accountName = null;
        string? problem = null;
        if (decision.Action == RuleAction.Relay)
        {
            var account = decision.AccountId is { } id
                ? await db.SmtpAccount.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id, ct)
                : await DefaultAccountAsync(db, ct);
            accountName = account?.Name;
            if (account is null)
            {
                problem = "Es gibt kein Standardkonto: die Nachricht würde nicht versendet.";
            }
            else if (!account.IsEnabled)
            {
                problem = $"Das Konto „{account.Name}“ ist abgeschaltet.";
            }
        }

        var hooks = decision.WebhookIds.Count == 0 ? [] : await db.Webhook.AsNoTracking().Where(w => decision.WebhookIds.Contains(w.Id)).Select(w => w.Name).ToListAsync(ct);
        return new RuleTestResult(decision, accountName, hooks, problem);
    }

    /// <summary>The account for messages that no rule sent elsewhere: the enabled default account.</summary>
    public static Task<SmtpAccount?> DefaultAccountAsync(MsDbContext db, CancellationToken ct = default)
        => db.SmtpAccount.AsNoTracking().Where(a => a.IsDefault && a.IsEnabled).OrderBy(a => a.Id).FirstOrDefaultAsync(ct);

    /// <summary>The decision in one sentence for the history of the message.</summary>
    public static string Describe(RuleDecision d)
    {
        var rules = d.Matched.Count == 0 ? "Keine Regel passt" : $"Regeln: {string.Join(", ", d.Matched)}";
        var action = d.Action switch
        {
            RuleAction.WebhookOnly => "nur an Webhook",
            RuleAction.Reject => "abweisen",
            RuleAction.Drop => "verwerfen",
            _ => d.AccountId is null ? "Versand über das Standardkonto" : "Versand über das Konto der Regel",
        };
        var extra = new List<string>();
        if (d.SenderChanged)
        {
            extra.Add($"Absender umgeschrieben auf {d.EnvelopeFrom}");
        }

        if (d.Bcc.Count > 0)
        {
            extra.Add($"Blindkopie an {string.Join(", ", d.Bcc)}");
        }

        if (d.WebhookIds.Count > 0)
        {
            extra.Add(Ui.CountNoun(d.WebhookIds.Count, "Webhooks"));
        }

        return $"{rules}: {action}{(extra.Count == 0 ? "" : " · " + string.Join(" · ", extra))}.";
    }

    private static void Rewrite(MimeMessage mime, RuleDecision decision, Message message)
    {
        if (!decision.SenderChanged)
        {
            return;
        }

        var before = message.OriginalFrom ?? mime.From.Mailboxes.FirstOrDefault()?.Address ?? message.EnvelopeFrom;
        message.OriginalFrom = before;
        message.EnvelopeFrom = decision.EnvelopeFrom;
        if (decision.HeaderFrom is not null || decision.HeaderFromName is not null)
        {
            var old = mime.From.Mailboxes.FirstOrDefault();
            var address = decision.HeaderFrom ?? old?.Address ?? decision.EnvelopeFrom;
            var name = decision.HeaderFromName ?? old?.Name ?? "";
            mime.From.Clear();
            mime.From.Add(new MailboxAddress(name, address));
            mime.Sender = null;
            message.HeaderFrom = Cut(mime.From.ToString(), 300);
        }

        if (decision.ReplyTo is not null && mime.ReplyTo.Count == 0)
        {
            mime.ReplyTo.Add(MailboxAddress.Parse(decision.ReplyTo));
        }

        mime.Headers.Replace("X-MatSMTP-Original-From", before);
    }

    private string ReceivedHeader(SessionInfo session, DateTime now)
    {
        var protocol = "ESMTP" + (session.IsTls ? "S" : "") + (session.AuthUser is null ? "" : "A");
        var helo = string.IsNullOrWhiteSpace(session.Helo) ? "unknown" : session.Helo;
        return $"from {helo} ([{session.ClientIp}]) by {config.Current.Smtp.Hostname} (MatSMTP) with {protocol}; {MimeKit.Utils.DateUtils.FormatDate(new DateTimeOffset(now, TimeSpan.Zero))}";
    }

    private async Task RecordRefusedAsync(Message message, MessageStatus status, string text, RuleDecision? decision, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        message.Status = status;
        message.LastError = status == MessageStatus.Rejected ? Cut(text, 1000) : null;
        message.Events.Add(Event(now, EventLevel.Info, $"Empfangen von {message.ClientIp ?? "unbekannt"}{(message.AuthUser is null ? "" : $" (Zugang {message.AuthUser})")}."));
        if (decision is not null)
        {
            message.Events.Add(Event(now, EventLevel.Info, Describe(decision)));
        }

        message.Events.Add(Event(now, status == MessageStatus.Rejected ? EventLevel.Warning : EventLevel.Info, text));
        db.Message.Add(message);
        await db.SaveChangesAsync(ct);
    }

    private async Task CountHitsAsync(IReadOnlyList<long> ids, DateTime now, CancellationToken ct)
    {
        if (ids.Count > 0)
        {
            await db.Rule.IgnoreQueryFilters().Where(r => ids.Contains(r.Id)).ExecuteUpdateAsync(s => s.SetProperty(r => r.HitCount, r => r.HitCount + 1).SetProperty(r => r.LastHitDate, now), ct);
        }
    }

    public static MessageEvent Event(DateTime when, EventLevel level, string text) => new() { OccurredDate = when, Level = level, Text = Cut(text, 2000)! };

    private static string? Cut(string? text, int max) => text is null ? null : text.Length <= max ? text : text[..max];
}

/// <summary>Wakes the spooler when there is something new, so a message does not wait for the next round.</summary>
public sealed class SpoolSignal
{
    private readonly SemaphoreSlim _signal = new(0, 1);

    public void Wake()
    {
        if (_signal.CurrentCount == 0)
        {
            try
            {
                _signal.Release();
            }
            catch (SemaphoreFullException)
            {
                // woken already
            }
        }
    }

    public Task<bool> WaitAsync(TimeSpan timeout, CancellationToken ct) => _signal.WaitAsync(timeout, ct);
}
