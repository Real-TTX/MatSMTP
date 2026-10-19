using System.Text.Json;
using MatPlatform;
using MatSmtp.Data.Entities;
using MatSmtp.Services;
using MatSmtp.Services.Relay;
using MatSmtp.UiKit;

namespace MatSmtp.Platform;

/// <summary>
/// MatSMTP as an app of the platform contract (MatDesign docs/PLATFORM.md) on the shared package MatPlatform (the folder <c>MatPlatform/</c>, a copy):
/// how Mattiverse shows it (<see cref="Describe"/>: tiles, the few settings, buttons), its values and what the buttons do. The values come from the
/// services (<see cref="RelayStatusService"/>, <see cref="RelaySettingsService"/>, <see cref="DeliveryService"/>); this class is the web layer and never
/// touches the database. Texts are German, or English when the caller asks for it with <c>Accept-Language</c>.
/// </summary>
public sealed class SmtpPlatform(
    RelayStatusService status,
    RelaySettingsService settings,
    DeliveryService delivery,
    AccountService accounts,
    SpoolSignal signal,
    ActivityLogger log,
    ICurrentUser current,
    TimeProvider clock) : IMatPlatformState, IMatPlatformConfiguration, IMatPlatformActions, IMatPlatformHealth
{
    /// <summary>The id of MatSMTP in the platform (MatDesign AppRegistry) and the default client id in MatIdentity.</summary>
    public const string AppId = "matsmtp";

    /// <summary>The accent of MatSMTP in MatDesign's AppRegistry (sun yellow; the text on it is dark).</summary>
    public const string Accent = "#f5b800";

    /// <summary>The fields of the platform form and the property of <see cref="RelaySettingsChange"/> each one is.</summary>
    private static readonly IReadOnlyDictionary<string, string> Fields = new Dictionary<string, string>
    {
        ["hostname"] = nameof(RelaySettingsChange.Hostname),
        ["defaultSenderDomain"] = nameof(RelaySettingsChange.DefaultSenderDomain),
        ["trustedNetworks"] = nameof(RelaySettingsChange.TrustedNetworks),
        ["paused"] = nameof(RelaySettingsChange.Paused),
        ["relayHost"] = nameof(RelaySettingsChange.RelayHost),
        ["relayPort"] = nameof(RelaySettingsChange.RelayPort),
        ["relaySecurity"] = nameof(RelaySettingsChange.RelaySecurity),
        ["relayUsername"] = nameof(RelaySettingsChange.RelayUsername),
        ["relayPassword"] = nameof(RelaySettingsChange.RelayPassword),
        ["relayFrom"] = nameof(RelaySettingsChange.RelayFrom),
    };

    /// <summary>The MatApiDefinition of MatSMTP (<c>GET /api</c>); <c>auth</c> and <c>endpoints</c> are filled in by MatPlatform.</summary>
    public static void Describe(MatPlatformBuilder o)
    {
        var T = o.T;

        o.App(AppId, "MatSMTP", T("SMTP-Relay mit Regeln, Warteschlange und Webhooks", "SMTP relay with rules, a queue and webhooks"), AppVersion.Current, "/logo.svg", Accent);

        o.Page("overview", T("Übersicht", "Overview"), "dashboard",
            MatSection.Tiles("queued", "deferred", "sent24h", "failed24h", "received24h", "lastDelivery"),
            new MatSection("tiles", ["accounts", "accountsHealth", "webhooksFailing"], T("Konten und Webhooks", "Accounts and webhooks")),
            MatSection.Actions("open", "messages", "queue", "accountsPage", "flushDeferred", "retryFailed", "testAccount"));
        o.Page("settings", T("Einstellungen", "Settings"), "settings",
            MatSection.Form(T("Empfang", "Receiving"), "hostname", "defaultSenderDomain", "trustedNetworks"),
            MatSection.Form(T("Versand über das Standardkonto", "Sending through the default account"),
                "relayHost", "relayPort", "relaySecurity", "relayUsername", "relayPassword", "relayFrom", "paused"));

        o.Tile(new MatStateItem("queued", T("Wartend", "Queued")) { Icon = "mail", WarnAbove = 50 });
        o.Tile(new MatStateItem("deferred", T("Zurückgestellt", "Deferred")) { Icon = "clock", WarnAbove = 0 });
        o.Tile("sent24h", T("Versendet (24 h)", "Sent (24 h)"), icon: "check-circle");
        o.Tile(new MatStateItem("failed24h", T("Fehlgeschlagen (24 h)", "Failed (24 h)")) { Icon = "x-circle", ErrorAbove = 0 });
        o.Tile("received24h", T("Empfangen (24 h)", "Received (24 h)"), icon: "download");
        o.Tile("lastDelivery", T("Letzte Zustellung", "Last delivery"), "time", icon: "clock");
        o.Tile("accounts", T("SMTP-Konten", "SMTP accounts"), icon: "server");
        o.Tile(new MatStateItem("accountsHealth", T("Zustand der Konten", "Health of the accounts"), "status") { Icon = "activity" });
        o.Tile(new MatStateItem("webhooksFailing", T("Webhooks mit Fehlern (24 h)", "Failing webhooks (24 h)")) { Icon = "plug", WarnAbove = 0 });

        o.Field(new MatField("hostname", T("Servername", "Server name"), "text")
        {
            Required = true,
            Max = 253,
            Pattern = @"^[A-Za-z0-9.\-]+$",
            Hint = T("Steht im Gruß des Servers, in der Received-Kopfzeile und im selbst signierten Zertifikat.", "Shown in the greeting, the Received header and the self-signed certificate."),
        });
        o.Field(new MatField("defaultSenderDomain", T("Standarddomain für Absender", "Default sender domain"), "text")
        {
            Max = 253,
            Placeholder = "example.com",
            Hint = T("Ein Absender ohne echte Domain (root, app@localhost) bekommt diese. Leer: aus.", "A sender without a real domain (root, app@localhost) gets this one. Empty: off."),
        });
        o.Field(new MatField("trustedNetworks", T("Ohne Anmeldung aus", "Without sign-in from"), "text")
        {
            Max = 2000,
            Hint = T("Netze, die ohne Zugang versenden dürfen (CIDR, mit Komma getrennt). Standard: nur 127.0.0.0/8, ::1. Ein privates Netz nur bewusst: hinter dem Docker-Proxy kommen Clients aus dem Internet mit 172.17.0.1 an.",
                "Networks that may relay without a relay user (CIDR, separated by commas). Default: only 127.0.0.0/8, ::1. A private range only consciously: behind Docker's proxy clients from the internet arrive as 172.17.0.1."),
        });
        o.Field(new MatField("relayHost", T("SMTP-Server", "SMTP server"), "text")
        {
            Max = 253,
            Placeholder = "smtp.example.com",
            Hint = T("Der Server des Anbieters, über den MatSMTP versendet. Leer lassen, solange es noch kein Konto gibt.", "The server of the provider MatSMTP sends through. Leave it empty while there is no account."),
        });
        o.Field(new MatField("relayPort", "Port", "number") { Min = 1, Max = 65535, Placeholder = "587" });
        o.Field(new MatField("relaySecurity", T("Verschlüsselung", "Encryption"), "select")
        {
            Options =
            [
                new MatOption(nameof(SmtpSecurity.Auto), T("Automatisch (STARTTLS, auf 465 TLS)", "Automatic (STARTTLS, TLS on 465)")),
                new MatOption(nameof(SmtpSecurity.StartTls), "STARTTLS"),
                new MatOption(nameof(SmtpSecurity.SslOnConnect), T("TLS von Anfang an", "TLS from the start")),
                new MatOption(nameof(SmtpSecurity.None), T("Keine", "None")),
            ],
        });
        o.Field(new MatField("relayUsername", T("Benutzername", "User name"), "text") { Max = 255 });
        o.Field(new MatField("relayPassword", T("Passwort", "Password"), "secret"));
        o.Field(new MatField("relayFrom", T("Absender erzwingen", "Force the sender"), "email")
        {
            Max = 255,
            Hint = T("Viele Anbieter erlauben nur ihre eigene Adresse: jede Nachricht geht dann mit ihr hinaus, der alte Absender wird Reply-To.", "Many providers allow only their own address: every message leaves with it, the old sender becomes Reply-To."),
        });
        o.Field(new MatField("paused", T("Versand anhalten", "Pause sending"), "bool")
        {
            Hint = T("Mails werden weiter angenommen und warten in der Warteschlange.", "Mail is still received and waits in the queue."),
        });

        o.LinkAction("open", T("MatSMTP öffnen", "Open MatSMTP"), "external", "/");
        o.LinkAction("messages", T("Nachrichten", "Messages"), "list", "/Messages");
        o.LinkAction("queue", T("Warteschlange", "Queue"), "clock", "/Messages/Queue");
        o.LinkAction("accountsPage", T("SMTP-Konten", "SMTP accounts"), "server", "/Accounts");
        o.PostAction("flushDeferred", T("Zurückgestellte jetzt senden", "Send deferred now"), "play");
        o.PostAction("retryFailed", T("Fehlgeschlagene erneut senden", "Retry failed"), "refresh",
            T("Alle in den letzten 24 Stunden fehlgeschlagenen Nachrichten erneut in die Warteschlange stellen?", "Put every message that failed in the last 24 hours back into the queue?"));
        o.PostAction("testAccount", T("Standardkonto prüfen", "Check the default account"), "plug");

        o.Role("admin", "Administrator");
        o.Role("user", T("Benutzer", "User"));
    }

    public async Task<IReadOnlyList<MatHealthCheck>> CheckHealthAsync(MatPlatformCall call, CancellationToken ct)
        => [.. (await status.HealthAsync(call.T, ct)).Select(c => new MatHealthCheck(c.Name, c.Status, c.Message))];

    public async Task<IReadOnlyDictionary<string, MatStateValue>> GetStateAsync(MatPlatformCall call, CancellationToken ct)
    {
        var c = await status.CountsAsync(ct);
        var (level, text) = (c.HasDefaultAccount, c.AccountsFailing) switch
        {
            (false, _) => ("error", call.T("Kein Standardkonto", "No default account")),
            (true, > 0) => ("warn", call.T($"{c.AccountsFailing} von {c.Accounts} mit Fehler", $"{c.AccountsFailing} of {c.Accounts} failing")),
            _ => ("ok", call.T(c.Accounts == 1 ? "1 Konto bereit" : $"{c.Accounts} Konten bereit", c.Accounts == 1 ? "1 account ready" : $"{c.Accounts} accounts ready")),
        };

        return new Dictionary<string, MatStateValue>
        {
            ["queued"] = new(c.Queued, c.Queued > 50 ? "warn" : null),
            ["deferred"] = new(c.Deferred, c.Deferred > 0 ? "warn" : null),
            ["sent24h"] = new(c.Sent24h),
            ["failed24h"] = new(c.Failed24h, c.Failed24h > 0 ? "error" : null),
            ["received24h"] = new(c.Received24h),
            ["lastDelivery"] = new(Time(c.LastDeliveryUtc)),
            ["accounts"] = new(c.Accounts),
            ["accountsHealth"] = new(level, level, text),
            ["webhooksFailing"] = new(c.WebhooksFailing, c.WebhooksFailing > 0 ? "warn" : null),
        };
    }

    public async Task<IReadOnlyDictionary<string, object?>> GetConfigurationAsync(MatPlatformCall call, CancellationToken ct)
    {
        var s = await settings.GetAsync(ct);
        return new Dictionary<string, object?>
        {
            ["hostname"] = s.Hostname,
            ["defaultSenderDomain"] = s.DefaultSenderDomain,
            ["trustedNetworks"] = s.TrustedNetworks,
            ["paused"] = s.Paused,
            ["relayHost"] = s.RelayHost,
            ["relayPort"] = s.RelayPort,
            ["relaySecurity"] = s.RelaySecurity.ToString(),
            ["relayUsername"] = s.RelayUsername,
            // Never the password itself: MatPlatform sends {"isSet": bool} for a secret whatever comes here.
            ["relayPassword"] = new MatSecret(s.RelayPasswordSet),
            ["relayFrom"] = s.RelayFrom,
        };
    }

    public async Task<IReadOnlyDictionary<string, string>> ConfigureAsync(IReadOnlyDictionary<string, JsonElement> values, MatPlatformCall call, CancellationToken ct)
    {
        // MatPlatform has checked the types, the lengths and the options against the definition; the rest is the service's.
        var change = new RelaySettingsChange();
        foreach (var (key, value) in values)
        {
            var text = value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : value.ValueKind == JsonValueKind.Null ? "" : null;
            switch (key)
            {
                case "hostname": change.Hostname = text; break;
                case "defaultSenderDomain": change.DefaultSenderDomain = text; break;
                case "trustedNetworks": change.TrustedNetworks = text; break;
                case "relayHost": change.RelayHost = text; break;
                case "relayUsername": change.RelayUsername = text; break;
                case "relayPassword": change.RelayPassword = text; break;
                case "relayFrom": change.RelayFrom = text; break;
                case "relaySecurity": change.RelaySecurity = Enum.TryParse<SmtpSecurity>(text, out var security) ? security : SmtpSecurity.Auto; break;
                case "relayPort": change.RelayPort = value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var port) ? port : null; break;
                case "paused": change.Paused = value.ValueKind == JsonValueKind.True; break;
            }
        }

        var errors = await settings.ApplyAsync(change, call.T, ct);
        if (errors.Count > 0)
        {
            return errors.ToDictionary(e => Fields.First(f => f.Value == e.Key).Key, e => e.Value);
        }

        using (current.RunAs(null, call.Caller))
        {
            await log.LogAsync("Einstellungen über die Plattform geändert", "Settings", null, string.Join(", ", values.Keys));
        }

        return new Dictionary<string, string>();
    }

    public async Task<MatActionResult?> RunActionAsync(string id, MatPlatformCall call, CancellationToken ct)
    {
        MatActionResult result;
        switch (id)
        {
            case "flushDeferred":
                var flushed = await delivery.FlushDeferredAsync(ct);
                signal.Wake();
                result = new MatActionResult(true, flushed == 0
                    ? call.T("Nichts ist zurückgestellt.", "Nothing is deferred.")
                    : call.T($"{flushed} Nachricht(en) werden jetzt gesendet.", $"{flushed} message(s) are being sent now."));
                break;

            case "retryFailed":
                var retried = await delivery.RetryFailedAsync(clock.GetUtcNow().UtcDateTime.AddDays(-1), ct);
                signal.Wake();
                result = new MatActionResult(true, retried == 0
                    ? call.T("In den letzten 24 Stunden ist nichts fehlgeschlagen, das noch einen Inhalt hat.", "Nothing failed in the last 24 hours that still has its content.")
                    : call.T($"{retried} Nachricht(en) stehen wieder in der Warteschlange.", $"{retried} message(s) are back in the queue."));
                break;

            case "testAccount":
                result = await TestDefaultAccountAsync(call, ct);
                break;

            default:
                return null;
        }

        using (current.RunAs(null, call.Caller))
        {
            await log.LogAsync("Aktion über die Plattform: " + id, "Platform", null, result.Message);
        }

        return result;
    }

    /// <summary>Connects to the default account and signs in, without sending: a short timeout, so the answer comes while the person waits.</summary>
    private async Task<MatActionResult> TestDefaultAccountAsync(MatPlatformCall call, CancellationToken ct)
    {
        var account = await accounts.DefaultAsync(ct);
        if (account is null)
        {
            return new MatActionResult(false, call.T("Es gibt noch kein Standardkonto.", "There is no default account yet."));
        }

        account.TimeoutSeconds = Math.Min(account.TimeoutSeconds, 15);
        var outcome = await delivery.CheckAsync(account, ct);
        return new MatActionResult(outcome.Ok, $"{account.Name}: {outcome.Text}");
    }

    private static DateTimeOffset? Time(DateTime? utc) => utc is { } value ? new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc)) : null;
}
