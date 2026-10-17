using System.Net;
using MatSmtp.Core.Relay;

namespace MatSmtp.Tests.Core;

public sealed class AddressPatternTests
{
    [Theory]
    [InlineData("*", "a@b.de", true)]
    [InlineData("*", "", true)]
    [InlineData("firma.de", "max@firma.de", true)]
    [InlineData("firma.de", "max@sub.firma.de", false)]
    [InlineData("@firma.de", "max@FIRMA.de", true)]
    [InlineData("*.firma.de", "max@shop.firma.de", true)]
    [InlineData("*.firma.de", "max@firma.de", false)]
    [InlineData("app-*@firma.de", "app-17@firma.de", true)]
    [InlineData("app-?@firma.de", "app-17@firma.de", false)]
    [InlineData("rechnung@firma.de", "Rechnung@Firma.de", true)]
    [InlineData("rechnung@firma.de", "rechnung2@firma.de", false)]
    [InlineData("/^noreply\\d+@/", "noreply12@x.de", true)]
    [InlineData("/^noreply\\d+@/", "noreply@x.de", false)]
    [InlineData("firma.de", "", false)]
    public void An_entry_matches_as_documented(string entry, string address, bool expected) => Assert.Equal(expected, AddressPattern.Matches(entry, address));

    [Fact]
    public void A_list_matches_when_one_entry_does()
    {
        Assert.True(AddressPattern.MatchesAny("a.de, b.de\nc.de;d.de", "x@c.de"));
        Assert.False(AddressPattern.MatchesAny("a.de, b.de", "x@e.de"));
        Assert.False(AddressPattern.MatchesAny("", "x@e.de"));
        Assert.True(AddressPattern.MatchesAny("b.de", ["x@a.de", "y@b.de"]));
    }

    [Fact]
    public void A_subject_matches_by_contained_text_or_regex()
    {
        Assert.True(AddressPattern.TextMatchesAny("rechnung, mahnung", "Ihre MAHNUNG vom Mai"));
        Assert.True(AddressPattern.TextMatchesAny("/^\\[Ticket #\\d+\\]/", "[Ticket #42] Drucker"));
        Assert.False(AddressPattern.TextMatchesAny("rechnung", null));
    }

    [Fact]
    public void A_broken_regex_or_a_space_is_named()
    {
        Assert.NotNull(AddressPattern.Problem("/(unclosed/"));
        Assert.NotNull(AddressPattern.Problem("a.de b.de"));
        Assert.Null(AddressPattern.Problem("a.de, /^x/"));
    }
}

public sealed class IpNetworkTests
{
    [Theory]
    [InlineData("192.168.0.0/16", "192.168.44.3", true)]
    [InlineData("192.168.0.0/16", "192.169.0.1", false)]
    [InlineData("10.1.2.3", "10.1.2.3", true)]
    [InlineData("10.1.2.3", "10.1.2.4", false)]
    [InlineData("172.16.0.0/12", "172.31.255.255", true)]
    [InlineData("172.16.0.0/12", "172.32.0.1", false)]
    [InlineData("10.0.0.0/8", "::ffff:10.9.8.7", true)]
    [InlineData("fc00::/7", "fd12::1", true)]
    [InlineData("fc00::/7", "2001:db8::1", false)]
    [InlineData("0.0.0.0/0", "8.8.8.8", true)]
    [InlineData("10.0.0.0/8", "fd00::1", false)]
    public void A_network_contains_its_addresses(string network, string address, bool expected)
    {
        Assert.True(IpNetwork.TryParse(network, out var n));
        Assert.Equal(expected, n.Contains(IPAddress.Parse(address)));
    }

    [Fact]
    public void A_list_is_parsed_and_a_wrong_entry_is_named()
    {
        Assert.True(IpNetworks.Contains("127.0.0.0/8, 10.0.0.0/8\n192.168.1.0/24", IPAddress.Parse("192.168.1.9")));
        Assert.Equal("„10.0.0.0/33“ ist keine IP-Adresse und kein Netz (Beispiel: 192.168.1.0/24).", IpNetworks.Problem("10.0.0.0/33"));
        Assert.NotNull(IpNetworks.Problem("intranet"));
        Assert.Null(IpNetworks.Problem("10.0.0.0/8, ::1"));
        Assert.False(IpNetworks.Contains("", IPAddress.Loopback));
    }
}

public sealed class SenderRewriteTests
{
    [Theory]
    [InlineData("noreply@firma.de", "app@intern.local", "noreply@firma.de")]
    [InlineData("{local}@firma.de", "shop@intern.local", "shop@firma.de")]
    [InlineData("firma.de", "shop@intern.local", "shop@firma.de")]
    [InlineData("relay+{local}@{domain}", "shop@intern.local", "relay+shop@intern.local")]
    [InlineData("{address}", "a@b.de", "a@b.de")]
    public void A_template_makes_the_new_sender(string template, string original, string expected) => Assert.Equal(expected, SenderRewrite.Apply(template, original));
}

public sealed class RuleEngineTests
{
    private static MessageFacts Facts(string from = "app@shop.de", string to = "kunde@example.com", string ip = "10.0.0.5", string? user = null, string? subject = "Bestellung")
        => new() { EnvelopeFrom = from, HeaderFrom = from, Recipients = [to], ClientIp = IPAddress.Parse(ip), AuthUser = user, Subject = subject };

    [Fact]
    public void Without_rules_the_message_goes_through_the_default_account()
    {
        var d = RuleEngine.Evaluate([], Facts());
        Assert.Equal(RuleAction.Relay, d.Action);
        Assert.Null(d.AccountId);
        Assert.Null(d.DecidedBy);
        Assert.False(d.SenderChanged);
    }

    [Fact]
    public void The_first_rule_with_a_decision_wins_and_ends_the_evaluation()
    {
        RuleSpec[] rules =
        [
            new() { Id = 1, Name = "Andere", Senders = "other.de", Action = RuleAction.Reject },
            new() { Id = 2, Name = "Shop", Senders = "shop.de", Action = RuleAction.Relay, AccountId = 7 },
            new() { Id = 3, Name = "Alle", Senders = "*", Action = RuleAction.Relay, AccountId = 9 },
        ];
        var d = RuleEngine.Evaluate(rules, Facts());
        Assert.Equal(7, d.AccountId);
        Assert.Equal("Shop", d.DecidedBy);
        Assert.Equal(["Shop"], d.Matched);
    }

    [Fact]
    public void Rules_that_only_change_add_up_before_the_decision()
    {
        RuleSpec[] rules =
        [
            new() { Id = 1, Name = "Umschreiben", Senders = "shop.de", Action = RuleAction.Continue, RewriteFrom = "noreply@firma.de", ReplyToOriginal = true },
            new() { Id = 2, Name = "Archiv", Action = RuleAction.Continue, AddBcc = "archiv@firma.de", WebhookId = 4 },
            // matched against the rewritten sender
            new() { Id = 3, Name = "Firma", Senders = "firma.de", Action = RuleAction.Relay, AccountId = 2 },
        ];
        var d = RuleEngine.Evaluate(rules, Facts());
        Assert.Equal(["Umschreiben", "Archiv", "Firma"], d.Matched);
        Assert.Equal("noreply@firma.de", d.EnvelopeFrom);
        Assert.Equal("noreply@firma.de", d.HeaderFrom);
        Assert.Equal("app@shop.de", d.ReplyTo);
        Assert.Equal(["archiv@firma.de"], d.Bcc);
        Assert.Equal([4L], d.WebhookIds);
        Assert.Equal(2, d.AccountId);
    }

    [Fact]
    public void All_conditions_of_a_rule_have_to_match()
    {
        var rule = new RuleSpec { Name = "Shop aus dem Büro", Senders = "shop.de", Networks = "10.0.0.0/24", Recipients = "*@example.com", Subject = "bestellung", Action = RuleAction.Drop };
        Assert.True(RuleEngine.Matches(rule, Facts()));
        Assert.False(RuleEngine.Matches(rule, Facts(ip: "10.0.1.5")));
        Assert.False(RuleEngine.Matches(rule, Facts(to: "x@other.com")));
        Assert.False(RuleEngine.Matches(rule, Facts(subject: "Newsletter")));
    }

    [Fact]
    public void A_rule_can_ask_for_a_signed_in_session_or_one_relay_user()
    {
        var signedIn = new RuleSpec { Name = "Angemeldet", Auth = AuthCondition.Authenticated };
        var anonymous = new RuleSpec { Name = "Anonym", Auth = AuthCondition.Anonymous };
        var shop = new RuleSpec { Name = "Shop", RelayUser = "shop" };
        Assert.False(RuleEngine.Matches(signedIn, Facts()));
        Assert.True(RuleEngine.Matches(signedIn, Facts(user: "crm")));
        Assert.True(RuleEngine.Matches(anonymous, Facts()));
        Assert.False(RuleEngine.Matches(anonymous, Facts(user: "crm")));
        Assert.True(RuleEngine.Matches(shop, Facts(user: "SHOP")));
        Assert.False(RuleEngine.Matches(shop, Facts(user: "crm")));
    }

    [Fact]
    public void A_rejection_carries_its_text_and_no_changes()
    {
        RuleSpec[] rules =
        [
            new() { Name = "Webhook", Action = RuleAction.Continue, WebhookId = 1, AddBcc = "a@b.de" },
            new() { Name = "Nein", Subject = "bestellung", Action = RuleAction.Reject },
        ];
        var d = RuleEngine.Evaluate(rules, Facts());
        Assert.Equal(RuleAction.Reject, d.Action);
        Assert.Equal("Die Nachricht wurde durch eine Regel abgewiesen.", d.RejectMessage);
        Assert.Empty(d.WebhookIds);
        Assert.Empty(d.Bcc);
    }

    [Fact]
    public void A_rewrite_of_the_envelope_only_leaves_the_header()
    {
        var d = RuleEngine.Evaluate([new RuleSpec { Name = "Bounce", Action = RuleAction.Continue, RewriteFrom = "bounce@firma.de", RewriteHeader = false }], Facts());
        Assert.Equal("bounce@firma.de", d.EnvelopeFrom);
        Assert.Null(d.HeaderFrom);
        Assert.True(d.SenderChanged);
    }
}

public sealed class RetryScheduleTests
{
    [Fact]
    public void The_last_wait_repeats()
    {
        Assert.Equal(TimeSpan.FromMinutes(1), RetrySchedule.Delay("1, 5, 15", 1));
        Assert.Equal(TimeSpan.FromMinutes(15), RetrySchedule.Delay("1, 5, 15", 3));
        Assert.Equal(TimeSpan.FromMinutes(15), RetrySchedule.Delay("1, 5, 15", 9));
        Assert.Equal(TimeSpan.FromMinutes(1), RetrySchedule.Delay("", 1));
        Assert.NotNull(RetrySchedule.Problem("1, x"));
        Assert.Null(RetrySchedule.Problem("1, 5, 60"));
    }
}

public sealed class WebhookSignatureTests
{
    [Fact]
    public void A_signature_verifies_only_with_its_secret_and_body()
    {
        var header = WebhookSignature.HeaderValue("geheim", 1760000000, "{\"id\":1}");
        Assert.StartsWith("t=1760000000,v1=", header);
        Assert.True(WebhookSignature.Verify("geheim", header, "{\"id\":1}"));
        Assert.False(WebhookSignature.Verify("anders", header, "{\"id\":1}"));
        Assert.False(WebhookSignature.Verify("geheim", header, "{\"id\":2}"));
        Assert.False(WebhookSignature.Verify("geheim", "kaputt", "{\"id\":1}"));
    }
}
