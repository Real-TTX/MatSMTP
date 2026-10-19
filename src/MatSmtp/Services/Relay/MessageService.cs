using MatSmtp.Data;
using MatSmtp.Data.Entities;
using MatSmtp.UiKit;
using Microsoft.EntityFrameworkCore;

namespace MatSmtp.Services.Relay;

/// <summary>How deleting one message ended: deleted, not there, or being sent right now (then it stays).</summary>
public enum MessageDeleteOutcome
{
    Deleted,
    NotFound,
    Sending,
}

/// <summary>The messages of the relay as the pages read them: the lists, one message with its history and webhooks, deleting (row and content).</summary>
public sealed class MessageService(MsDbContext db, SpoolStore spool, TimeProvider clock)
{
    /// <summary>A message without its history; null when there is none.</summary>
    public Task<Message?> FindAsync(long id, CancellationToken ct = default) => db.Message.AsNoTracking().FirstOrDefaultAsync(m => m.Id == id, ct);

    /// <summary>A message with its history (in order) and its webhook deliveries; null when there is none.</summary>
    public Task<Message?> FindWithHistoryAsync(long id, CancellationToken ct = default)
        => db.Message.AsNoTracking().AsSplitQuery()
            .Include(m => m.Events.OrderBy(e => e.OccurredDate).ThenBy(e => e.Id))
            .Include(m => m.WebhookDeliveries)
            .FirstOrDefaultAsync(m => m.Id == id, ct);

    public Task<bool> ExistsAsync(long id, CancellationToken ct = default) => db.Message.AnyAsync(m => m.Id == id, ct);

    /// <summary>
    /// The list of the messages (<paramref name="queueOnly"/>: only what still waits): search, filters (status, account, period, sign-in), sort and page of the request.
    /// <paramref name="action"/> is the address of the page the toolbar sends to.
    /// </summary>
    public async Task<ListResult<Message>> ListAsync(HttpRequest request, bool queueOnly, string action, CancellationToken ct = default)
    {
        var accounts = await db.SmtpAccount.IgnoreQueryFilters().AsNoTracking().OrderBy(a => a.Name).Select(a => new { a.Id, a.Name }).ToListAsync(ct);
        var now = clock.GetUtcNow().UtcDateTime;
        IQueryable<Message> all = db.Message.AsNoTracking();
        if (queueOnly)
        {
            all = all.Where(m => m.Status == MessageStatus.Queued || m.Status == MessageStatus.Sending || m.Status == MessageStatus.Deferred);
        }

        var statuses = queueOnly
            ? new[] { MessageStatus.Queued, MessageStatus.Sending, MessageStatus.Deferred }
            : Enum.GetValues<MessageStatus>();
        var spec = new ListSpec<Message>
        {
            Id = queueOnly ? "queue" : "messages",
            Action = action,
            Label = queueOnly ? "Warteschlange" : "Nachrichten",
            Noun = "Nachrichten",
            SearchPlaceholder = "Absender, Empfänger, Betreff, Message-ID ...",
            Search = (q, text) =>
            {
                var like = Like.Contains(text);
                return q.Where(m => EF.Functions.Like(m.EnvelopeFrom, like, Like.Escape) || EF.Functions.Like(m.Recipients, like, Like.Escape)
                    || EF.Functions.Like(m.Subject ?? "", like, Like.Escape) || EF.Functions.Like(m.MessageIdHeader ?? "", like, Like.Escape)
                    || EF.Functions.Like(m.HeaderFrom ?? "", like, Like.Escape) || EF.Functions.Like(m.OriginalFrom ?? "", like, Like.Escape)
                    || EF.Functions.Like(m.ClientIp ?? "", like, Like.Escape) || EF.Functions.Like(m.AuthUser ?? "", like, Like.Escape));
            },
            Filters =
            [
                new FilterSpec<Message>("status", "Status", [.. statuses.Select(s => new FilterOption(s.ToString(), Labels.Of(s)))], (q, v) => q.Where(m => m.Status == Enum.Parse<MessageStatus>(v))),
                new FilterSpec<Message>("konto", "Konto", [.. accounts.Select(a => new FilterOption(a.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), a.Name))],
                    (q, v) => long.TryParse(v, out var id) ? q.Where(m => m.AccountId == id) : q),
                new FilterSpec<Message>("zeit", "Zeitraum", [new("1h", "Letzte Stunde"), new("24h", "Letzte 24 Stunden"), new("7d", "Letzte 7 Tage"), new("30d", "Letzte 30 Tage")],
                    (q, v) =>
                    {
                        var since = v switch { "1h" => now.AddHours(-1), "24h" => now.AddDays(-1), "7d" => now.AddDays(-7), _ => now.AddDays(-30) };
                        return q.Where(m => m.ReceivedDate >= since);
                    }),
                new FilterSpec<Message>("anmeldung", "Anmeldung", [new("mit", "Mit Zugang"), new("ohne", "Ohne Anmeldung")],
                    (q, v) => v == "mit" ? q.Where(m => m.AuthUser != null) : q.Where(m => m.AuthUser == null)),
            ],
            Sorts = new Dictionary<string, (string, Func<IQueryable<Message>, bool, IOrderedQueryable<Message>>)>
            {
                ["received"] = ("Empfangen", (q, d) => d ? q.OrderByDescending(m => m.ReceivedDate).ThenByDescending(m => m.Id) : q.OrderBy(m => m.ReceivedDate).ThenBy(m => m.Id)),
                ["from"] = ("Absender", (q, d) => d ? q.OrderByDescending(m => m.EnvelopeFrom) : q.OrderBy(m => m.EnvelopeFrom)),
                ["subject"] = ("Betreff", (q, d) => d ? q.OrderByDescending(m => m.Subject) : q.OrderBy(m => m.Subject)),
                ["status"] = ("Status", (q, d) => d ? q.OrderByDescending(m => m.Status).ThenByDescending(m => m.Id) : q.OrderBy(m => m.Status).ThenByDescending(m => m.Id)),
                ["next"] = ("Nächster Versuch", (q, d) => d ? q.OrderByDescending(m => m.NextAttemptDate) : q.OrderBy(m => m.NextAttemptDate)),
                ["size"] = ("Größe", (q, d) => d ? q.OrderByDescending(m => m.SizeBytes) : q.OrderBy(m => m.SizeBytes)),
            },
            DefaultSort = queueOnly ? "next" : "received",
            DefaultDescending = !queueOnly,
            PageSize = 25,
        };

        return await ListBuilder.RunAsync(all, request, spec, ct);
    }

    /// <summary>Deletes messages with their content; one that is being sent right now stays. Returns how many were deleted.</summary>
    public async Task<int> DeleteAsync(IReadOnlyCollection<long> ids, CancellationToken ct = default)
    {
        var messages = await db.Message.Where(m => ids.Contains(m.Id) && m.Status != MessageStatus.Sending).ToListAsync(ct);
        foreach (var m in messages)
        {
            spool.Delete(m.StorageKey);
        }

        db.Message.RemoveRange(messages);
        await db.SaveChangesAsync(ct);
        return messages.Count;
    }

    /// <summary>Deletes one message with its content, unless it is being sent right now; its subject comes back for the log.</summary>
    public async Task<(MessageDeleteOutcome Outcome, string? Subject)> DeleteOneAsync(long id, CancellationToken ct = default)
    {
        var message = await db.Message.FirstOrDefaultAsync(m => m.Id == id, ct);
        if (message is null)
        {
            return (MessageDeleteOutcome.NotFound, null);
        }

        if (message.Status == MessageStatus.Sending)
        {
            return (MessageDeleteOutcome.Sending, message.Subject);
        }

        spool.Delete(message.StorageKey);
        db.Message.Remove(message);
        await db.SaveChangesAsync(ct);
        return (MessageDeleteOutcome.Deleted, message.Subject);
    }
}
