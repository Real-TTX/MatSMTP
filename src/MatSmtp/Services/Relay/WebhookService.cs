using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MatSmtp.Configuration;
using MatSmtp.Core.Relay;
using MatSmtp.Data;
using MatSmtp.Data.Entities;
using Microsoft.EntityFrameworkCore;
using MimeKit;

namespace MatSmtp.Services.Relay;

/// <summary>The answer of a webhook call.</summary>
public sealed record WebhookResult(bool Ok, int? StatusCode, string Text, long Milliseconds);

/// <summary>
/// Calls the webhooks the rules chose: an HTTP POST with JSON (what is in it depends on <see cref="WebhookPayload"/>), signed with the secret of the webhook
/// (<see cref="WebhookSignature"/>). A 2xx answer is a success; anything else is tried again (1, 5, 15, 60 minutes) up to the attempts of the webhook. A message
/// that only goes to webhooks is "Zugestellt" when one of its calls went through, "Fehlgeschlagen" when all of them gave up.
/// </summary>
public sealed class WebhookService(MsDbContext db, SpoolStore spool, SecretProtector secrets, IHttpClientFactory http, TimeProvider clock, ILogger<WebhookService> logger)
{
    public const string HttpClientName = "webhooks";

    private const string RetryMinutes = "1, 5, 15, 60";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public async Task RunDueAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var due = await db.WebhookDelivery.Include(d => d.Webhook).Include(d => d.Message)
            .Where(d => d.Status == WebhookDeliveryStatus.Pending && (d.NextAttemptDate == null || d.NextAttemptDate <= now))
            .OrderBy(d => d.Id).Take(20).ToListAsync(ct);
        foreach (var delivery in due)
        {
            await DeliverAsync(delivery, ct);
        }
    }

    private async Task DeliverAsync(WebhookDelivery delivery, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var message = delivery.Message!;
        var hook = delivery.Webhook;
        WebhookResult result;
        var giveUp = false;
        if (hook is null || hook.UpdateState == UpdateState.Deleted || !hook.IsEnabled)
        {
            result = new WebhookResult(false, null, "Der Webhook ist gelöscht oder abgeschaltet.", 0);
            giveUp = true;
        }
        else
        {
            result = await CallAsync(hook, await PayloadAsync(hook, message, ct), ct);
            hook.LastCallDate = now;
            hook.LastStatusCode = result.StatusCode;
            hook.LastError = result.Ok ? null : result.Text;
        }

        delivery.Attempts++;
        delivery.ResponseCode = result.StatusCode;
        var max = Math.Max(1, hook?.MaxAttempts ?? 1);
        if (result.Ok)
        {
            delivery.Status = WebhookDeliveryStatus.Done;
            delivery.DoneDate = now;
            delivery.LastError = null;
            message.Events.Add(RelayService.Event(now, EventLevel.Success, $"Webhook „{delivery.WebhookName}“ aufgerufen: HTTP {result.StatusCode} in {result.Milliseconds} ms."));
        }
        else
        {
            var final = giveUp || delivery.Attempts >= max;
            delivery.LastError = result.Text.Length > 1000 ? result.Text[..1000] : result.Text;
            delivery.Status = final ? WebhookDeliveryStatus.Failed : WebhookDeliveryStatus.Pending;
            delivery.NextAttemptDate = final ? null : now + RetrySchedule.Delay(RetryMinutes, delivery.Attempts);
            message.Events.Add(RelayService.Event(now, final ? EventLevel.Error : EventLevel.Warning,
                $"Webhook „{delivery.WebhookName}“ fehlgeschlagen{(final ? ", endgültig" : "")}: {result.Text}"));
        }

        await db.SaveChangesAsync(ct);
        if (message.Action == RuleAction.WebhookOnly)
        {
            await CompleteWebhookOnlyAsync(message.Id, ct);
        }
    }

    /// <summary>A message for webhooks only is done when none of its calls waits any more.</summary>
    private async Task CompleteWebhookOnlyAsync(long messageId, CancellationToken ct)
    {
        var message = await db.Message.Include(m => m.WebhookDeliveries).FirstAsync(m => m.Id == messageId, ct);
        if (message.IsFinal || message.WebhookDeliveries.Any(d => d.Status == WebhookDeliveryStatus.Pending))
        {
            return;
        }

        var now = clock.GetUtcNow().UtcDateTime;
        var any = message.WebhookDeliveries.Any(d => d.Status == WebhookDeliveryStatus.Done);
        message.Status = any ? MessageStatus.Delivered : MessageStatus.Failed;
        message.SentDate = any ? now : null;
        message.LastError = any ? null : "Kein Webhook hat die Nachricht angenommen.";
        message.Attempts = message.WebhookDeliveries.Sum(d => d.Attempts);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Calls a webhook with a sample message (the webhook page): shows whether the address answers and what.</summary>
    public async Task<WebhookResult> TestAsync(Webhook hook, CancellationToken ct)
    {
        var sample = new Message
        {
            Id = 0,
            ReceivedDate = clock.GetUtcNow().UtcDateTime,
            ClientIp = "127.0.0.1",
            EnvelopeFrom = "app@example.com",
            HeaderFrom = "Beispiel-App <app@example.com>",
            Recipients = "empfaenger@example.com",
            RecipientCount = 1,
            Subject = "Testaufruf von MatSMTP",
            SizeBytes = 0,
            Status = MessageStatus.Queued,
        };
        var payload = await PayloadAsync(hook, sample, ct, test: true);
        return await CallAsync(hook, payload, ct);
    }

    private async Task<WebhookResult> CallAsync(Webhook hook, string body, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(hook.TimeoutSeconds, 1, 120)));
            using var request = new HttpRequestMessage(HttpMethod.Post, hook.Url) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("MatSMTP", UiKit.AppVersion.Current.Replace(' ', '-')));
            var secret = secrets.Unprotect(hook.SecretProtected);
            if (secret.Length > 0)
            {
                request.Headers.TryAddWithoutValidation(WebhookSignature.Header, WebhookSignature.HeaderValue(secret, clock.GetUtcNow().ToUnixTimeSeconds(), body));
            }

            foreach (var (name, value) in ParseHeaders(hook.Headers))
            {
                request.Headers.TryAddWithoutValidation(name, value);
            }

            using var response = await http.CreateClient(HttpClientName).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            var code = (int)response.StatusCode;
            var text = response.IsSuccessStatusCode ? $"HTTP {code}" : $"HTTP {code} {response.ReasonPhrase}";
            return new WebhookResult(response.IsSuccessStatusCode, code, text, watch.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new WebhookResult(false, null, $"Zeitüberschreitung nach {hook.TimeoutSeconds} Sekunden.", watch.ElapsedMilliseconds);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or UriFormatException)
        {
            logger.LogWarning(ex, "Webhook {Name} failed.", hook.Name);
            return new WebhookResult(false, null, ex.Message, watch.ElapsedMilliseconds);
        }
    }

    /// <summary>The extra headers of a webhook, one "Name: value" per line.</summary>
    public static IEnumerable<(string Name, string Value)> ParseHeaders(string? text)
    {
        foreach (var line in (text ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var colon = line.IndexOf(':');
            if (colon > 0)
            {
                yield return (line[..colon].Trim(), line[(colon + 1)..].Trim());
            }
        }
    }

    /// <summary>The JSON of a call. Without content for <see cref="WebhookPayload.Metadata"/>; the text, HTML and attachment names for Content; the whole .eml as base64 for Raw.</summary>
    public async Task<string> PayloadAsync(Webhook hook, Message message, CancellationToken ct, bool test = false)
    {
        string? text = null;
        string? html = null;
        string? raw = null;
        List<object>? attachments = null;
        Dictionary<string, string>? headers = null;
        if (hook.Payload != WebhookPayload.Metadata && spool.Exists(message.StorageKey))
        {
            var bytes = await spool.ReadAllAsync(message.StorageKey!, ct);
            using var stream = new MemoryStream(bytes, writable: false);
            var mime = await MimeMessage.LoadAsync(stream, ct);
            text = mime.TextBody;
            html = mime.HtmlBody;
            attachments = [.. mime.Attachments.Select(a => (object)new { fileName = a.ContentDisposition?.FileName ?? a.ContentType.Name, contentType = a.ContentType.MimeType })];
            headers = mime.Headers.GroupBy(h => h.Field).ToDictionary(g => g.Key, g => string.Join("\n", g.Select(h => h.Value)));
            if (hook.Payload == WebhookPayload.Raw)
            {
                raw = Convert.ToBase64String(bytes);
            }
        }
        else if (test && hook.Payload != WebhookPayload.Metadata)
        {
            text = "Dies ist ein Testaufruf von MatSMTP.";
        }

        var payload = new
        {
            @event = test ? "test" : "message.received",
            id = message.Id,
            receivedAt = DateTime.SpecifyKind(message.ReceivedDate, DateTimeKind.Utc),
            client = new { ip = message.ClientIp, helo = message.HeloName, authUser = message.AuthUser, tls = message.IsTls },
            envelope = new { from = message.EnvelopeFrom, to = message.Recipients.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) },
            originalFrom = message.OriginalFrom,
            from = message.HeaderFrom,
            subject = message.Subject,
            messageId = message.MessageIdHeader,
            size = message.SizeBytes,
            rules = message.Rules?.Split(", ", StringSplitOptions.RemoveEmptyEntries),
            action = message.Action.ToString(),
            headers,
            text,
            html,
            attachments,
            raw,
        };
        return JsonSerializer.Serialize(payload, Json);
    }
}
