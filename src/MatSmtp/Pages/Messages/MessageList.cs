using MatSmtp.Data;
using MatSmtp.Data.Entities;
using MatSmtp.Services;
using MatSmtp.Services.Relay;
using MatSmtp.UiKit;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MatSmtp.Pages.Messages;

/// <summary>
/// The list of messages, shared by "Nachrichten" (everything) and "Warteschlange" (what still waits): toolbar with search, filters and sort, the table with a check per
/// row, the pager, and below it the actions for the selected messages (send again, delete).
/// </summary>
public abstract class MessageListModel(MsDbContext db, DeliveryService delivery, SpoolStore spool, NavCounts counts, SpoolSignal signal, ActivityLogger log, TimeProvider clock) : PageModelBase
{
    public const string BulkForm = "message-bulk";

    public ToolbarModel Toolbar { get; private set; } = null!;

    public PagerModel Pager { get; private set; } = null!;

    public TableModel Table { get; private set; } = null!;

    public abstract bool QueueOnly { get; }

    protected abstract string Action { get; }

    protected async Task LoadAsync()
    {
        var accounts = await db.SmtpAccount.IgnoreQueryFilters().AsNoTracking().OrderBy(a => a.Name).Select(a => new { a.Id, a.Name }).ToListAsync();
        var now = clock.GetUtcNow().UtcDateTime;
        IQueryable<Message> all = db.Message.AsNoTracking();
        if (QueueOnly)
        {
            all = all.Where(m => m.Status == MessageStatus.Queued || m.Status == MessageStatus.Sending || m.Status == MessageStatus.Deferred);
        }

        var statuses = QueueOnly
            ? new[] { MessageStatus.Queued, MessageStatus.Sending, MessageStatus.Deferred }
            : Enum.GetValues<MessageStatus>();
        var spec = new ListSpec<Message>
        {
            Id = QueueOnly ? "queue" : "messages",
            Action = Action,
            Label = QueueOnly ? "Warteschlange" : "Nachrichten",
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
            DefaultSort = QueueOnly ? "next" : "received",
            DefaultDescending = !QueueOnly,
            PageSize = 25,
        };

        var result = await ListBuilder.RunAsync(all, Request, spec, HttpContext.RequestAborted);
        Toolbar = result.Toolbar;
        Pager = result.Pager;
        Table = TableModel.Of(spec.Id + "-table", spec.Label, result.Items,
        [
            (new TableColumn("Empfangen"), m => Ui.NameWithSub(Fmt.Moment(m.ReceivedDate), $"/Messages/Details?id={m.Id}", m.AuthUser is null ? m.ClientIp : $"{m.ClientIp} · {m.AuthUser}")),
            (new TableColumn("Absender"), m => Ui.NameWithSub(m.EnvelopeFrom.Length > 0 ? m.EnvelopeFrom : "<>", null, m.OriginalFrom is null ? null : "vorher " + m.OriginalFrom)),
            (new TableColumn("Empfänger", HideOnPhone: true), m => Ui.Text(m.RecipientCount > 2 ? $"{m.Recipients.Split(',')[0]} und {m.RecipientCount - 1} weitere" : m.Recipients)),
            (new TableColumn("Betreff", HideOnPhone: true), m => m.Subject is null ? Ui.None() : Ui.Text(m.Subject.Length > 80 ? m.Subject[..80] + "…" : m.Subject)),
            (new TableColumn("Konto", HideOnPhone: true), m => m.AccountName is null ? Ui.None() : Ui.Text(m.AccountName)),
            (new TableColumn("Status"), m => Ui.Concat(Labels.Pill(m.Status), m.Status == MessageStatus.Deferred && m.NextAttemptDate is { } next ? Ui.NameWithSub("", null, "nächster Versuch " + Fmt.Time(next)) : Ui.Text(""))),
        ],
        m => [new RowAction("eye", "Ansehen", $"/Messages/Details?id={m.Id}")],
        m => (m.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), $"Nachricht {m.Id} auswählen"));
        Table = Table with
        {
            SelectForm = BulkForm,
            EmptyTitle = Toolbar.Total == 0 ? (QueueOnly ? "Die Warteschlange ist leer" : "Noch keine Nachricht") : "Nichts gefunden",
            EmptyText = Toolbar.Total == 0 ? (QueueOnly ? "Alles ist versendet." : "Sobald eine Anwendung über MatSMTP sendet, steht die Nachricht hier.") : "Ändere die Suche oder den Filter.",
            EmptyIcon = Toolbar.Total == 0 ? "inbox" : "search",
        };
    }

    public async Task<IActionResult> OnPostRetryAsync(long[] ids)
    {
        if (ids.Length == 0)
        {
            FlashError("Wähle zuerst Nachrichten aus.");
            return Redirect(Back());
        }

        var count = await delivery.RetryAsync(ids, HttpContext.RequestAborted);
        counts.Invalidate();
        signal.Wake();
        await log.LogAsync("Nachrichten erneut eingereiht", nameof(Message), null, $"{count} von {ids.Length}");
        if (count < ids.Length)
        {
            FlashError($"{Ui.CountNoun(count, "Nachrichten")} erneut in der Warteschlange. Die übrigen sind schon versendet, abgewiesen, nur für Webhooks oder haben keinen Inhalt mehr.");
        }
        else
        {
            Flash($"{Ui.CountNoun(count, "Nachrichten")} erneut in der Warteschlange.");
        }

        return Redirect(Back());
    }

    public async Task<IActionResult> OnPostDeleteAsync(long[] ids)
    {
        if (ids.Length == 0)
        {
            FlashError("Wähle zuerst Nachrichten aus.");
            return Redirect(Back());
        }

        var messages = await db.Message.Where(m => ids.Contains(m.Id) && m.Status != MessageStatus.Sending).ToListAsync();
        foreach (var m in messages)
        {
            spool.Delete(m.StorageKey);
        }

        db.Message.RemoveRange(messages);
        await db.SaveChangesAsync();
        counts.Invalidate();
        await log.LogAsync("Nachrichten gelöscht", nameof(Message), null, $"{messages.Count} Nachricht(en)");
        Flash($"{Ui.CountNoun(messages.Count, "Nachrichten")} gelöscht.");
        return Redirect(Back());
    }

    private string Back() => SafeReturn(Request.Form["returnUrl"], Action);
}
