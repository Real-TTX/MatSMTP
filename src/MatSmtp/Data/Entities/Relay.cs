using MatSmtp.Core.Relay;

namespace MatSmtp.Data.Entities;

public enum SmtpSecurity
{
    /// <summary>STARTTLS when the server offers it (port 587), TLS from the start on port 465.</summary>
    Auto,
    StartTls,
    /// <summary>TLS from the first byte (port 465).</summary>
    SslOnConnect,
    /// <summary>No encryption (only inside a trusted network).</summary>
    None,
}

/// <summary>An SMTP account of a provider through which the relay sends (Microsoft 365, a mail host, the server of the company).</summary>
public class SmtpAccount : AuditedEntity, ISoftDeletable
{
    public string Name { get; set; } = "";

    public string? Description { get; set; }

    public string Host { get; set; } = "";

    public int Port { get; set; } = 587;

    public SmtpSecurity Security { get; set; } = SmtpSecurity.Auto;

    public string? UserName { get; set; }

    /// <summary>Protected with the key ring of the application.</summary>
    public string? PasswordProtected { get; set; }

    /// <summary>When set, every message through this account goes out with this sender (many providers allow only their own address); the old one goes into Reply-To.</summary>
    public string? ForceFromAddress { get; set; }

    /// <summary>How many messages a minute this account may send (0 = no limit).</summary>
    public int MaxPerMinute { get; set; }

    /// <summary>How many messages a day (0 = no limit).</summary>
    public int MaxPerDay { get; set; }

    public int TimeoutSeconds { get; set; } = 60;

    /// <summary>Messages without a rule that decides go through the default account.</summary>
    public bool IsDefault { get; set; }

    public bool IsEnabled { get; set; } = true;

    public DateTime? LastUsedDate { get; set; }

    public DateTime? LastErrorDate { get; set; }

    public string? LastError { get; set; }

    public UpdateState UpdateState { get; set; } = UpdateState.Created;
}

/// <summary>An account with which an application signs in at the relay (SMTP AUTH). The password is hashed like the one of a user.</summary>
public class RelayUser : AuditedEntity, ISoftDeletable
{
    public string UserName { get; set; } = "";

    public string? Description { get; set; }

    public string PasswordHash { get; set; } = "";

    /// <summary>The senders this user may use (patterns, see <see cref="AddressPattern"/>); empty: any.</summary>
    public string? AllowedSenders { get; set; }

    public bool IsEnabled { get; set; } = true;

    public DateTime? LastUsedDate { get; set; }

    /// <summary>The id of the relay user in the management API (<c>PUT /api/v1/relay-users/{id}</c>, <c>[a-z0-9-]{2,64}</c>); null: made on the page.</summary>
    public string? ApiId { get; set; }

    /// <summary>Who manages it through the API (<c>mattiverse</c>): only the same caller may change or delete it there; the pages show a badge.</summary>
    public string? ManagedBy { get; set; }

    public UpdateState UpdateState { get; set; } = UpdateState.Created;
}

/// <summary>How much of the message a webhook gets.</summary>
public enum WebhookPayload
{
    /// <summary>Envelope, headers, subject, status: no content.</summary>
    Metadata,
    /// <summary>The metadata plus the text and HTML body and the names of the attachments.</summary>
    Content,
    /// <summary>The metadata plus the whole message as it was received (base64 of the .eml file), attachments included.</summary>
    Raw,
}

/// <summary>An address that is called (HTTP POST, JSON) when a rule triggers it: to hand a mail to another system.</summary>
public class Webhook : AuditedEntity, ISoftDeletable
{
    public string Name { get; set; } = "";

    public string? Description { get; set; }

    public string Url { get; set; } = "";

    public WebhookPayload Payload { get; set; } = WebhookPayload.Metadata;

    /// <summary>Signs every call (HMAC-SHA256, header X-MatSMTP-Signature). Protected with the key ring.</summary>
    public string? SecretProtected { get; set; }

    /// <summary>Extra headers, one per line ("Authorization: Bearer ...").</summary>
    public string? Headers { get; set; }

    public int TimeoutSeconds { get; set; } = 15;

    public int MaxAttempts { get; set; } = 5;

    public bool IsEnabled { get; set; } = true;

    public DateTime? LastCallDate { get; set; }

    public int? LastStatusCode { get; set; }

    public string? LastError { get; set; }

    public UpdateState UpdateState { get; set; } = UpdateState.Created;
}

/// <summary>A rule of the policy based routing (see <see cref="RuleEngine"/>): conditions, then what happens.</summary>
public class Rule : AuditedEntity, ISoftDeletable
{
    public string Name { get; set; } = "";

    public string? Description { get; set; }

    /// <summary>The order: smaller first.</summary>
    public int Position { get; set; }

    public bool IsEnabled { get; set; } = true;

    public string? MatchSenders { get; set; }

    public string? MatchRecipients { get; set; }

    public string? MatchNetworks { get; set; }

    public AuthCondition MatchAuth { get; set; } = AuthCondition.Any;

    public long? MatchRelayUserId { get; set; }

    public RelayUser? MatchRelayUser { get; set; }

    public string? MatchSubject { get; set; }

    public RuleAction Action { get; set; } = RuleAction.Relay;

    public long? AccountId { get; set; }

    public SmtpAccount? Account { get; set; }

    public long? WebhookId { get; set; }

    public Webhook? Webhook { get; set; }

    public string? RewriteFrom { get; set; }

    public string? RewriteFromName { get; set; }

    public bool RewriteHeader { get; set; } = true;

    public bool ReplyToOriginal { get; set; }

    public string? AddBcc { get; set; }

    public string? RejectMessage { get; set; }

    public long HitCount { get; set; }

    public DateTime? LastHitDate { get; set; }

    public UpdateState UpdateState { get; set; } = UpdateState.Created;
}

public enum MessageStatus
{
    /// <summary>Waits for the spooler.</summary>
    Queued,
    /// <summary>Is being sent right now.</summary>
    Sending,
    /// <summary>A temporary error: the spooler tries again at <see cref="Message.NextAttemptDate"/>.</summary>
    Deferred,
    Sent,
    /// <summary>A permanent error or too many attempts.</summary>
    Failed,
    /// <summary>Refused by a rule while the application was connected.</summary>
    Rejected,
    /// <summary>Accepted and thrown away by a rule.</summary>
    Dropped,
    /// <summary>Handed to its webhooks only (no mail).</summary>
    Delivered,
}

/// <summary>A message the relay received: the envelope, what the rules made of it, and where it stands. The content is an .eml file in the spool folder.</summary>
public class Message : AuditedEntity
{
    public DateTime ReceivedDate { get; set; }

    public string? ClientIp { get; set; }

    public string? HeloName { get; set; }

    public int ServerPort { get; set; }

    public bool IsTls { get; set; }

    /// <summary>The relay user of the session; null: it did not sign in.</summary>
    public string? AuthUser { get; set; }

    /// <summary>The envelope sender as it goes out (after the rules).</summary>
    public string EnvelopeFrom { get; set; } = "";

    /// <summary>The sender as the application gave it, when a rule or the account changed it.</summary>
    public string? OriginalFrom { get; set; }

    public string? HeaderFrom { get; set; }

    /// <summary>The recipients, separated by ", ".</summary>
    public string Recipients { get; set; } = "";

    public int RecipientCount { get; set; }

    public string? Subject { get; set; }

    public string? MessageIdHeader { get; set; }

    public long SizeBytes { get; set; }

    /// <summary>The file in the spool folder ("2026/10/18/3f2a....eml"); null once the content was removed.</summary>
    public string? StorageKey { get; set; }

    public MessageStatus Status { get; set; } = MessageStatus.Queued;

    /// <summary>The rule action (Relay, WebhookOnly ...).</summary>
    public RuleAction Action { get; set; } = RuleAction.Relay;

    /// <summary>The account chosen by the rules; null: the default account at the time of sending.</summary>
    public long? AccountId { get; set; }

    public SmtpAccount? Account { get; set; }

    /// <summary>The name of the account that sent it (kept when the account is deleted).</summary>
    public string? AccountName { get; set; }

    /// <summary>The rules that matched, separated by ", ".</summary>
    public string? Rules { get; set; }

    /// <summary>The blind copies added by the rules, separated by ", ".</summary>
    public string? Bcc { get; set; }

    public int Attempts { get; set; }

    public DateTime? NextAttemptDate { get; set; }

    public DateTime? SentDate { get; set; }

    public string? LastError { get; set; }

    /// <summary>The answer of the server that took it ("250 2.0.0 OK queued as ...").</summary>
    public string? RemoteResponse { get; set; }

    public List<MessageEvent> Events { get; set; } = [];

    public List<WebhookDelivery> WebhookDeliveries { get; set; } = [];

    public bool IsFinal => Status is MessageStatus.Sent or MessageStatus.Failed or MessageStatus.Rejected or MessageStatus.Dropped or MessageStatus.Delivered;
}

public enum EventLevel
{
    Info,
    Success,
    Warning,
    Error,
}

/// <summary>A line of the history of a message: received, rule, attempt, answer of the server, webhook.</summary>
public class MessageEvent : AuditedEntity
{
    public long MessageId { get; set; }

    public Message? Message { get; set; }

    public DateTime OccurredDate { get; set; }

    public EventLevel Level { get; set; } = EventLevel.Info;

    public string Text { get; set; } = "";
}

public enum WebhookDeliveryStatus
{
    Pending,
    Done,
    Failed,
}

/// <summary>A call of a webhook for a message: kept until it went through or ran out of attempts, so a restart loses none.</summary>
public class WebhookDelivery : AuditedEntity
{
    public long MessageId { get; set; }

    public Message? Message { get; set; }

    public long? WebhookId { get; set; }

    public Webhook? Webhook { get; set; }

    public string WebhookName { get; set; } = "";

    public WebhookDeliveryStatus Status { get; set; } = WebhookDeliveryStatus.Pending;

    public int Attempts { get; set; }

    public DateTime? NextAttemptDate { get; set; }

    public DateTime? DoneDate { get; set; }

    public int? ResponseCode { get; set; }

    public string? LastError { get; set; }
}

/// <summary>A link that shows the delivery of one message to someone without an account (sender, recipients, subject, status, history: never the content).</summary>
public class ShareLink : AuditedEntity
{
    public Guid Token { get; set; } = Guid.NewGuid();

    public long MessageId { get; set; }

    public Message? Message { get; set; }

    public string? Note { get; set; }

    public DateTime ExpiresDate { get; set; }

    public DateTime? RevokedDate { get; set; }

    public int Views { get; set; }

    public DateTime? LastViewDate { get; set; }
}
