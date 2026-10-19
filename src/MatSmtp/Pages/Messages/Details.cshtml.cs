using MatSmtp.Core.Relay;
using MatSmtp.Data.Entities;
using MatSmtp.Services;
using MatSmtp.Services.Relay;
using MatSmtp.UiKit;
using Microsoft.AspNetCore.Mvc;
using MimeKit;

namespace MatSmtp.Pages.Messages;

/// <summary>One message: envelope, origin, delivery, webhooks, history, its headers and text, and the share links that show its delivery to others.</summary>
public sealed class DetailsModel(MessageService messages, DeliveryService delivery, SpoolStore spool, ShareService shares, NavCounts counts, SpoolSignal signal, ActivityLogger log, TimeProvider clock) : PageModelBase
{
    public sealed record HeaderLine(string Name, string Value);

    [BindProperty(SupportsGet = true)]
    public long Id { get; set; }

    public Message Item { get; private set; } = null!;

    public bool HasContent { get; private set; }

    public List<HeaderLine> Headers { get; private set; } = [];

    public string? TextBody { get; private set; }

    public bool HasHtml { get; private set; }

    public List<string> Attachments { get; private set; } = [];

    public List<ShareLink> Links { get; private set; } = [];

    public DateTime Now => clock.GetUtcNow().UtcDateTime;

    public bool CanRetry => Item.Action == RuleAction.Relay && HasContent && Item.Status is MessageStatus.Failed or MessageStatus.Deferred or MessageStatus.Queued;

    public bool CanShare => Item.Status is not MessageStatus.Rejected;

    public string ShareLinkOf(ShareLink link) => shares.LinkOf(link);

    [BindProperty]
    public int ShareDays { get; set; }

    [BindProperty]
    public string? ShareNote { get; set; }

    public async Task<IActionResult> OnGetAsync()
    {
        if (!await LoadAsync())
        {
            return NotFound();
        }

        ShareDays = shares.DefaultDays;
        return Page();
    }

    public async Task<IActionResult> OnPostRetryAsync()
    {
        var count = await delivery.RetryAsync([Id], HttpContext.RequestAborted);
        counts.Invalidate();
        signal.Wake();
        if (count == 0)
        {
            FlashError("Diese Nachricht kann nicht erneut gesendet werden.");
        }
        else
        {
            await log.LogAsync("Nachricht erneut eingereiht", nameof(Message), Id);
            Flash("Die Nachricht ist wieder in der Warteschlange.");
        }

        return RedirectToPage(new { id = Id });
    }

    public async Task<IActionResult> OnPostShareAsync()
    {
        if (!await messages.ExistsAsync(Id, HttpContext.RequestAborted))
        {
            return NotFound();
        }

        var link = await shares.CreateAsync(Id, ShareDays <= 0 ? shares.DefaultDays : ShareDays, ShareNote);
        await log.LogAsync("Freigabe-Link angelegt", nameof(ShareLink), link.Id, $"Nachricht {Id}, {Fmt.Date(link.ExpiresDate)}");
        Flash("Der Link ist angelegt. Kopiere ihn und gib ihn weiter.");
        return RedirectToPage(new { id = Id });
    }

    public async Task<IActionResult> OnPostRevokeAsync(long linkId)
    {
        if (await shares.RevokeAsync(linkId))
        {
            await log.LogAsync("Freigabe-Link widerrufen", nameof(ShareLink), linkId);
            Flash("Der Link ist widerrufen.");
        }

        return RedirectToPage(new { id = Id });
    }

    public async Task<IActionResult> OnPostDeleteAsync()
    {
        var (outcome, subject) = await messages.DeleteOneAsync(Id, HttpContext.RequestAborted);
        if (outcome == MessageDeleteOutcome.NotFound)
        {
            return RedirectToPage("/Messages/Index");
        }

        if (outcome == MessageDeleteOutcome.Sending)
        {
            FlashError("Die Nachricht wird gerade versendet. Versuche es gleich noch einmal.");
            return RedirectToPage(new { id = Id });
        }

        counts.Invalidate();
        await log.LogAsync("Nachricht gelöscht", nameof(Message), Id, subject);
        Flash("Die Nachricht ist gelöscht.");
        return RedirectToPage("/Messages/Index");
    }

    private async Task<bool> LoadAsync()
    {
        var item = await messages.FindWithHistoryAsync(Id, HttpContext.RequestAborted);
        if (item is null)
        {
            return false;
        }

        Item = item;
        Links = await shares.ForMessageAsync(Id, HttpContext.RequestAborted);
        HasContent = spool.Exists(item.StorageKey);
        if (HasContent)
        {
            try
            {
                await using var stream = spool.OpenRead(item.StorageKey!);
                var mime = await MimeMessage.LoadAsync(stream);
                Headers = [.. mime.Headers.Select(h => new HeaderLine(h.Field, h.Value))];
                TextBody = mime.TextBody;
                HasHtml = mime.HtmlBody is not null;
                Attachments = [.. mime.Attachments.Select(a => $"{a.ContentDisposition?.FileName ?? a.ContentType.Name ?? "Anhang"} ({a.ContentType.MimeType})")];
                if (TextBody is { Length: > 20000 })
                {
                    TextBody = TextBody[..20000] + "\n…";
                }
            }
            catch (Exception ex) when (ex is FormatException or IOException)
            {
                HasContent = false;
            }
        }

        var title = string.IsNullOrWhiteSpace(item.Subject) ? $"Nachricht {item.Id}" : item.Subject;
        Head(title, $"Empfangen am {Fmt.Moment(item.ReceivedDate)}", ("Nachrichten", "/Messages"), (title, null));
        ViewData["ActiveHref"] = item.IsFinal ? "/Messages" : "/Messages/Queue";
        return true;
    }
}
