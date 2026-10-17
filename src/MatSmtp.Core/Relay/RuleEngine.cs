using System.Net;

namespace MatSmtp.Core.Relay;

/// <summary>What a rule does with a message once its conditions match.</summary>
public enum RuleAction
{
    /// <summary>Only its changes (rewrite, webhook, copy): the next rules are asked too.</summary>
    Continue,
    /// <summary>Send it through an SMTP account (the default account when the rule names none). Ends the evaluation.</summary>
    Relay,
    /// <summary>Hand it only to the webhook of the rule, never send it by mail. Ends the evaluation.</summary>
    WebhookOnly,
    /// <summary>Refuse it while the application is still connected (an SMTP error 550). Ends the evaluation.</summary>
    Reject,
    /// <summary>Accept it and throw it away. Ends the evaluation.</summary>
    Drop,
}

/// <summary>Which sessions a rule is for: everyone, only those that signed in (with a relay user), or only those that did not.</summary>
public enum AuthCondition
{
    Any,
    Authenticated,
    Anonymous,
}

/// <summary>A rule as the engine sees it. Empty conditions are not checked; all others have to match (AND), an entry of a list is enough (OR).</summary>
public sealed record RuleSpec
{
    public long Id { get; init; }

    public string Name { get; init; } = "";

    /// <summary>Patterns for the sender (the envelope sender or the From header, see <see cref="AddressPattern"/>).</summary>
    public string? Senders { get; init; }

    /// <summary>Patterns for the recipients: one recipient that matches is enough.</summary>
    public string? Recipients { get; init; }

    /// <summary>Networks of the client (CIDR).</summary>
    public string? Networks { get; init; }

    public AuthCondition Auth { get; init; } = AuthCondition.Any;

    /// <summary>The name of a relay user: only sessions that signed in with it.</summary>
    public string? RelayUser { get; init; }

    /// <summary>Texts the subject contains (or <c>/regex/</c>).</summary>
    public string? Subject { get; init; }

    public RuleAction Action { get; init; } = RuleAction.Relay;

    public long? AccountId { get; init; }

    public long? WebhookId { get; init; }

    /// <summary>The new sender, with <c>{local}</c>, <c>{domain}</c> and <c>{address}</c> of the old one (see <see cref="SenderRewrite"/>).</summary>
    public string? RewriteFrom { get; init; }

    /// <summary>The new display name of the sender; empty keeps it.</summary>
    public string? RewriteFromName { get; init; }

    /// <summary>The rewrite also changes the From header (otherwise only the envelope sender).</summary>
    public bool RewriteHeader { get; init; } = true;

    /// <summary>The old sender goes into Reply-To (when the message has none), so answers still reach it.</summary>
    public bool ReplyToOriginal { get; init; }

    /// <summary>Addresses that get a blind copy.</summary>
    public string? AddBcc { get; init; }

    public string? RejectMessage { get; init; }
}

/// <summary>What is known about a message when the rules are asked.</summary>
public sealed record MessageFacts
{
    public string EnvelopeFrom { get; init; } = "";

    public string? HeaderFrom { get; init; }

    public string? HeaderFromName { get; init; }

    public IReadOnlyList<string> Recipients { get; init; } = [];

    public IPAddress? ClientIp { get; init; }

    /// <summary>The relay user of the session; null when it did not sign in.</summary>
    public string? AuthUser { get; init; }

    public string? Subject { get; init; }
}

/// <summary>The result of the rules: what happens with the message, and how it looks afterwards.</summary>
public sealed record RuleDecision
{
    public RuleAction Action { get; init; } = RuleAction.Relay;

    /// <summary>The account of the deciding rule; null means the default account.</summary>
    public long? AccountId { get; init; }

    /// <summary>The rule that decided; null when none did and the default applies.</summary>
    public string? DecidedBy { get; init; }

    /// <summary>Every rule that matched, in order.</summary>
    public IReadOnlyList<string> Matched { get; init; } = [];

    public IReadOnlyList<long> MatchedIds { get; init; } = [];

    public IReadOnlyList<long> WebhookIds { get; init; } = [];

    public string EnvelopeFrom { get; init; } = "";

    /// <summary>The new From header; null leaves the header as it is.</summary>
    public string? HeaderFrom { get; init; }

    public string? HeaderFromName { get; init; }

    /// <summary>The address for Reply-To; null leaves it.</summary>
    public string? ReplyTo { get; init; }

    public IReadOnlyList<string> Bcc { get; init; } = [];

    public string? RejectMessage { get; init; }

    public bool SenderChanged { get; init; }
}

/// <summary>
/// Asks the rules about a message, top to bottom, like a firewall: every rule whose conditions match applies its changes (rewriting the sender, a webhook, a blind
/// copy), and the first matching rule with a decision (send, webhook only, reject, drop) ends the evaluation. When no rule decides, the message is sent through the
/// default account.
/// </summary>
public static class RuleEngine
{
    public static RuleDecision Evaluate(IEnumerable<RuleSpec> rules, MessageFacts facts)
    {
        var matched = new List<string>();
        var matchedIds = new List<long>();
        var webhooks = new List<long>();
        var bcc = new List<string>();
        var envelope = facts.EnvelopeFrom;
        var header = facts.HeaderFrom;
        var headerName = facts.HeaderFromName;
        string? replyTo = null;
        var headerChanged = false;
        var senderChanged = false;

        foreach (var rule in rules)
        {
            if (!Matches(rule, facts with { EnvelopeFrom = envelope, HeaderFrom = header }))
            {
                continue;
            }

            matched.Add(rule.Name);
            matchedIds.Add(rule.Id);
            if (rule.WebhookId is { } hook && !webhooks.Contains(hook))
            {
                webhooks.Add(hook);
            }

            foreach (var address in AddressPattern.Split(rule.AddBcc))
            {
                if (!bcc.Contains(address, StringComparer.OrdinalIgnoreCase))
                {
                    bcc.Add(address);
                }
            }

            if (!string.IsNullOrWhiteSpace(rule.RewriteFrom) || !string.IsNullOrWhiteSpace(rule.RewriteFromName))
            {
                var before = string.IsNullOrEmpty(header) ? envelope : header;
                if (!string.IsNullOrWhiteSpace(rule.RewriteFrom))
                {
                    envelope = SenderRewrite.Apply(rule.RewriteFrom, envelope.Length > 0 ? envelope : before ?? "");
                }

                if (rule.RewriteHeader)
                {
                    if (!string.IsNullOrWhiteSpace(rule.RewriteFrom))
                    {
                        header = SenderRewrite.Apply(rule.RewriteFrom, before ?? "");
                    }

                    if (!string.IsNullOrWhiteSpace(rule.RewriteFromName))
                    {
                        headerName = rule.RewriteFromName.Trim();
                    }

                    headerChanged = true;
                    if (rule.ReplyToOriginal && replyTo is null && !string.IsNullOrEmpty(before) && !string.Equals(before, header, StringComparison.OrdinalIgnoreCase))
                    {
                        replyTo = before;
                    }
                }

                senderChanged = true;
            }

            if (rule.Action == RuleAction.Continue)
            {
                continue;
            }

            return new RuleDecision
            {
                Action = rule.Action,
                AccountId = rule.Action == RuleAction.Relay ? rule.AccountId : null,
                DecidedBy = rule.Name,
                Matched = matched,
                MatchedIds = matchedIds,
                WebhookIds = rule.Action is RuleAction.Reject or RuleAction.Drop ? [] : webhooks,
                EnvelopeFrom = envelope,
                HeaderFrom = headerChanged ? header : null,
                HeaderFromName = headerChanged ? headerName : null,
                ReplyTo = replyTo,
                Bcc = rule.Action == RuleAction.Relay ? bcc : [],
                RejectMessage = rule.Action == RuleAction.Reject ? (string.IsNullOrWhiteSpace(rule.RejectMessage) ? "Die Nachricht wurde durch eine Regel abgewiesen." : rule.RejectMessage.Trim()) : null,
                SenderChanged = senderChanged,
            };
        }

        return new RuleDecision
        {
            Action = RuleAction.Relay,
            Matched = matched,
            MatchedIds = matchedIds,
            WebhookIds = webhooks,
            EnvelopeFrom = envelope,
            HeaderFrom = headerChanged ? header : null,
            HeaderFromName = headerChanged ? headerName : null,
            ReplyTo = replyTo,
            Bcc = bcc,
            SenderChanged = senderChanged,
        };
    }

    /// <summary>Whether every condition of the rule that is set matches.</summary>
    public static bool Matches(RuleSpec rule, MessageFacts facts)
    {
        if (!string.IsNullOrWhiteSpace(rule.Senders)
            && !AddressPattern.MatchesAny(rule.Senders, facts.EnvelopeFrom)
            && !AddressPattern.MatchesAny(rule.Senders, facts.HeaderFrom))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(rule.Recipients) && !AddressPattern.MatchesAny(rule.Recipients, facts.Recipients))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(rule.Networks) && !IpNetworks.Contains(rule.Networks, facts.ClientIp))
        {
            return false;
        }

        var signedIn = !string.IsNullOrEmpty(facts.AuthUser);
        if ((rule.Auth == AuthCondition.Authenticated && !signedIn) || (rule.Auth == AuthCondition.Anonymous && signedIn))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(rule.RelayUser) && !string.Equals(rule.RelayUser, facts.AuthUser, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return string.IsNullOrWhiteSpace(rule.Subject) || AddressPattern.TextMatchesAny(rule.Subject, facts.Subject);
    }
}

/// <summary>
/// Rewrites a sender from a template: <c>noreply@firma.de</c> replaces it, <c>{local}@firma.de</c> keeps the part before the @, <c>app@{domain}</c> the domain,
/// <c>{address}</c> the whole old address. A template without @ is a domain: <c>firma.de</c> means <c>{local}@firma.de</c>.
/// </summary>
public static class SenderRewrite
{
    public static string Apply(string template, string original)
    {
        var t = template.Trim();
        if (t.Length == 0)
        {
            return original;
        }

        if (!t.Contains('@') && !t.Contains("{address}", StringComparison.OrdinalIgnoreCase))
        {
            t = "{local}@" + t.TrimStart('@');
        }

        var local = AddressPattern.LocalPartOf(original);
        var domain = AddressPattern.DomainOf(original);
        return t.Replace("{local}", local, StringComparison.OrdinalIgnoreCase)
            .Replace("{domain}", domain, StringComparison.OrdinalIgnoreCase)
            .Replace("{address}", original, StringComparison.OrdinalIgnoreCase);
    }
}
