using System.ComponentModel.DataAnnotations;
using System.Net;
using MatSmtp.Configuration;
using MatSmtp.Core.Relay;
using MatSmtp.Services;
using MatSmtp.Services.Relay;
using Microsoft.AspNetCore.Mvc;
using MimeKit;

namespace MatSmtp.Pages.Tools;

/// <summary>
/// A test message through the whole relay: it is received like the mail of an application (from 127.0.0.1, without sign-in, or as a relay user), the rules decide,
/// the spooler sends it. The result is a message in the list like any other.
/// </summary>
public sealed class SendModel(RelayService relay, ConfigStore config, ActivityLogger log) : PageModelBase
{
    public sealed class InputModel
    {
        [Required(ErrorMessage = "Bitte gib den Absender ein.")]
        [EmailAddress(ErrorMessage = "Das ist keine E-Mail-Adresse.")]
        public string From { get; set; } = "";

        [Required(ErrorMessage = "Bitte gib mindestens einen Empfänger ein.")]
        public string To { get; set; } = "";

        public string Subject { get; set; } = "Testnachricht über MatSMTP";

        public string? Body { get; set; } = "Diese Nachricht wurde über das Werkzeug „Testnachricht“ von MatSMTP eingeliefert.";

        public string? ClientIp { get; set; } = "127.0.0.1";

        public string? AuthUser { get; set; }
    }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public void OnGet()
    {
        HeadUp();
        Input.From = $"test@{config.Current.Smtp.Hostname}";
    }

    public async Task<IActionResult> OnPostAsync()
    {
        HeadUp();
        var recipients = AddressPattern.Split(Input.To);
        if (recipients.Any(r => !new EmailAddressAttribute().IsValid(r)))
        {
            ModelState.AddModelError("Input.To", "Mindestens ein Empfänger ist keine E-Mail-Adresse.");
        }

        IPAddress? ip = null;
        if (!string.IsNullOrWhiteSpace(Input.ClientIp) && !IPAddress.TryParse(Input.ClientIp.Trim(), out ip))
        {
            ModelState.AddModelError("Input.ClientIp", "Das ist keine IP-Adresse.");
        }

        if (!ModelState.IsValid)
        {
            return Page();
        }

        var mime = new MimeMessage();
        mime.From.Add(MailboxAddress.Parse(Input.From.Trim()));
        foreach (var r in recipients)
        {
            mime.To.Add(MailboxAddress.Parse(r));
        }

        mime.Subject = Input.Subject;
        mime.MessageId = MimeKit.Utils.MimeUtils.GenerateMessageId(config.Current.Smtp.Hostname);
        mime.Body = new TextPart("plain") { Text = Input.Body ?? "" };
        using var stream = new MemoryStream();
        await mime.WriteToAsync(stream);

        var session = new SessionInfo(ip ?? IPAddress.Loopback, "matsmtp-web", string.IsNullOrWhiteSpace(Input.AuthUser) ? null : Input.AuthUser.Trim(), 0, false);
        var result = await relay.ReceiveAsync(session, Input.From.Trim(), recipients, stream.ToArray(), HttpContext.RequestAborted);
        await log.LogAsync("Testnachricht eingeliefert", "Message", result.MessageId, $"{Input.From} an {string.Join(", ", recipients)}");
        if (result.MessageId is { } id)
        {
            Flash(result.Accepted ? "Die Testnachricht ist angenommen. Hier siehst du, was aus ihr wird." : $"Die Testnachricht wurde abgewiesen: {result.Code} {result.Text}");
            return RedirectToPage("/Messages/Details", new { id });
        }

        FlashError($"Die Testnachricht wurde nicht angenommen: {result.Code} {result.Text}");
        return RedirectToPage();
    }

    private void HeadUp()
    {
        Head("Testnachricht", "Liefert eine Nachricht ein, als käme sie von einer Anwendung: die Regeln entscheiden, die Warteschlange versendet.", ("Werkzeuge", null), ("Testnachricht", null));
        ViewData["Width"] = "narrow";
    }
}
