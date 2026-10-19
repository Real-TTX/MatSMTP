using MatSmtp.Core.Relay;
using MatSmtp.Data.Entities;
using Microsoft.AspNetCore.Html;

namespace MatSmtp.UiKit;

/// <summary>The German words for the enums of the data model, and the tone of a status pill (the colour is never alone: the pill carries the word).</summary>
public static class Labels
{
    public static string Of(UserRole r) => r == UserRole.Admin ? "Administrator" : "Benutzer";

    public static string Of(MessageStatus s) => s switch
    {
        MessageStatus.Queued => "In der Warteschlange",
        MessageStatus.Sending => "Wird gesendet",
        MessageStatus.Deferred => "Verzögert",
        MessageStatus.Sent => "Versendet",
        MessageStatus.Failed => "Fehlgeschlagen",
        MessageStatus.Rejected => "Abgewiesen",
        MessageStatus.Dropped => "Verworfen",
        _ => "Zugestellt (Webhook)",
    };

    public static IHtmlContent Pill(MessageStatus s) => s switch
    {
        MessageStatus.Queued => Ui.Pill("Warteschlange", "info", "clock"),
        MessageStatus.Sending => Ui.Pill("Wird gesendet", "busy", "send"),
        MessageStatus.Deferred => Ui.Pill("Verzögert", "warn", "refresh"),
        MessageStatus.Sent => Ui.Pill("Versendet", "ok", "check"),
        MessageStatus.Failed => Ui.Pill("Fehlgeschlagen", "error", "x-circle"),
        MessageStatus.Rejected => Ui.Pill("Abgewiesen", "error", "ban"),
        MessageStatus.Dropped => Ui.Pill("Verworfen", "plain", "trash"),
        _ => Ui.Pill("Zugestellt", "ok", "plug"),
    };

    public static string Of(RuleAction a) => a switch
    {
        RuleAction.Continue => "Nur ändern, weiter prüfen",
        RuleAction.Relay => "Versenden",
        RuleAction.WebhookOnly => "Nur an Webhook",
        RuleAction.Reject => "Abweisen",
        _ => "Verwerfen",
    };

    public static IHtmlContent Pill(RuleAction a) => a switch
    {
        RuleAction.Continue => Ui.Pill("Weiter", "plain", "arrow-down"),
        RuleAction.Relay => Ui.Pill("Versenden", "ok", "send"),
        RuleAction.WebhookOnly => Ui.Pill("Webhook", "info", "plug"),
        RuleAction.Reject => Ui.Pill("Abweisen", "error", "ban"),
        _ => Ui.Pill("Verwerfen", "warn", "trash"),
    };

    public static string Of(AuthCondition a) => a switch
    {
        AuthCondition.Authenticated => "Nur angemeldete (mit Zugang)",
        AuthCondition.Anonymous => "Nur ohne Anmeldung",
        _ => "Alle",
    };

    public static string Of(SmtpSecurity s) => s switch
    {
        SmtpSecurity.StartTls => "STARTTLS",
        SmtpSecurity.SslOnConnect => "TLS ab Verbindungsbeginn (SSL)",
        SmtpSecurity.None => "Keine Verschlüsselung",
        _ => "Automatisch",
    };

    public static string Of(WebhookPayload p) => p switch
    {
        WebhookPayload.Content => "Metadaten und Inhalt (Text, HTML, Anhangsnamen)",
        WebhookPayload.Raw => "Metadaten und vollständige Nachricht (.eml, Base64)",
        _ => "Nur Metadaten",
    };

    public static string Of(WebhookDeliveryStatus s) => s switch
    {
        WebhookDeliveryStatus.Done => "Erfolgreich",
        WebhookDeliveryStatus.Failed => "Fehlgeschlagen",
        _ => "Ausstehend",
    };

    public static IHtmlContent Pill(WebhookDeliveryStatus s) => s switch
    {
        WebhookDeliveryStatus.Done => Ui.Pill("Erfolgreich", "ok", "check"),
        WebhookDeliveryStatus.Failed => Ui.Pill("Fehlgeschlagen", "error", "x-circle"),
        _ => Ui.Pill("Ausstehend", "info", "clock"),
    };

    public static string Tone(EventLevel l) => l switch
    {
        EventLevel.Success => "ok",
        EventLevel.Warning => "warn",
        EventLevel.Error => "error",
        _ => "info",
    };

    public static string Icon(EventLevel l) => l switch
    {
        EventLevel.Success => "check-circle",
        EventLevel.Warning => "alert",
        EventLevel.Error => "x-circle",
        _ => "info",
    };

    public static string Of(BackupScope s) => s == BackupScope.SystemOnly ? "System (Datenbank, Einstellungen, Zertifikate)" : "System und Nachrichteninhalte";

    public static string Of(BackupTargetKind k) => k == BackupTargetKind.Smb ? "NAS (SMB-Freigabe)" : "Ordner auf dem Server";

    public static string Of(ScheduleKind k) => k switch
    {
        ScheduleKind.Hourly => "Stündlich",
        ScheduleKind.Daily => "Täglich",
        ScheduleKind.Weekly => "Wöchentlich",
        _ => "Monatlich",
    };

    public static string Of(DayOfWeek d) => Services.Fmt.Culture.DateTimeFormat.GetDayName(d);

    /// <summary>The badge of a record that another program manages through the API: "von Mattiverse" for <c>mattiverse</c>.</summary>
    public static string ManagedBy(string managedBy) => "von " + Manager(managedBy);

    /// <summary>The name of the program that manages a record: <c>mattiverse</c> becomes "Mattiverse".</summary>
    public static string Manager(string managedBy) => managedBy.Length == 0 ? managedBy : char.ToUpperInvariant(managedBy[0]) + managedBy[1..];
}
