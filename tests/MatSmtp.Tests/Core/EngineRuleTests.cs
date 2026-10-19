using System.Net;
using MatSmtp.Api;
using MatSmtp.Configuration;
using MatSmtp.Core.Relay;
using MatSmtp.Services.Relay;

namespace MatSmtp.Tests.Core;

/// <summary>The small rules of the engine: the default domain of senders, who may sign in without TLS, the port the status names, the settings file.</summary>
public sealed class EngineRuleTests
{
    [Theory]
    [InlineData("root", "example.com", "root@example.com")]
    [InlineData("app@localhost", "example.com", "app@example.com")]
    [InlineData("cron@nas", "example.com", "cron@example.com")]
    [InlineData("backup@nas.local", "example.com", "backup@example.com")]
    [InlineData("matsplit@mattiverse.lan", "@example.com", "matsplit@example.com")]
    [InlineData("app@[10.0.0.5]", "example.com", "app@example.com")]
    [InlineData("shop@shop.example.org", "example.com", "shop@shop.example.org")]
    [InlineData("", "example.com", "")]
    [InlineData("app@localhost", "", "app@localhost")]
    public void A_sender_without_a_real_domain_gets_the_default_domain(string sender, string domain, string expected)
        => Assert.Equal(expected, SenderRewrite.CompleteDomain(sender, domain));

    [Fact]
    public void Auth_without_tls_is_allowed_only_on_the_internal_port_and_there_only_from_the_private_networks()
    {
        static bool Plain(SmtpConfig smtp, int port, string? ip) => Services.Relay.SmtpListener.PlainAuthAllowed(smtp, port, ip is null ? null : IPAddress.Parse(ip));
        var smtp = new SmtpConfig();
        Assert.True(Plain(smtp, 2025, "172.18.0.5"));
        Assert.True(Plain(smtp, 2025, "192.168.1.20"));
        Assert.True(Plain(smtp, 2025, "127.0.0.1"));
        Assert.False(Plain(smtp, 2025, "203.0.113.9"));
        Assert.False(Plain(smtp, 2025, null));

        // Submission and the TLS port: never in clear, also from the Docker network.
        foreach (var port in new[] { 2587, 2465 })
        {
            Assert.False(Plain(smtp, port, "172.18.0.5"));
            Assert.False(Plain(smtp, port, "127.0.0.1"));
        }

        smtp.PlainAuthNetworks = "";
        Assert.False(Plain(smtp, 2025, "172.18.0.5"));

        // "From everywhere" is about the internal port only.
        smtp.AllowPlainAuth = true;
        Assert.True(Plain(smtp, 2025, "203.0.113.9"));
        Assert.False(Plain(smtp, 2587, "203.0.113.9"));
        Assert.False(Plain(smtp, 2465, "203.0.113.9"));

        // The internal port switched off: no AUTH in clear anywhere.
        smtp.Port = 0;
        Assert.False(Plain(smtp, 0, "172.18.0.5"));
    }

    [Fact]
    public void By_default_only_the_loopback_relays_without_signing_in_not_the_docker_gateway()
    {
        var smtp = new SmtpConfig();
        Assert.True(Services.Relay.SmtpListener.RelayWithoutAuth(smtp, IPAddress.Loopback));
        Assert.True(Services.Relay.SmtpListener.RelayWithoutAuth(smtp, IPAddress.IPv6Loopback));
        // Behind Docker's userland proxy a client from the internet arrives as the gateway.
        Assert.False(Services.Relay.SmtpListener.RelayWithoutAuth(smtp, IPAddress.Parse("172.17.0.1")));
        Assert.False(Services.Relay.SmtpListener.RelayWithoutAuth(smtp, IPAddress.Parse("192.168.1.20")));
        Assert.False(Services.Relay.SmtpListener.RelayWithoutAuth(smtp, IPAddress.Parse("10.253.130.5")));
        Assert.False(Services.Relay.SmtpListener.RelayWithoutAuth(smtp, null));

        // Widened consciously it holds, unless every session has to sign in.
        smtp.TrustedNetworks = "192.168.0.0/16";
        Assert.True(Services.Relay.SmtpListener.RelayWithoutAuth(smtp, IPAddress.Parse("192.168.1.20")));
        smtp.RequireAuth = true;
        Assert.False(Services.Relay.SmtpListener.RelayWithoutAuth(smtp, IPAddress.Parse("192.168.1.20")));
    }

    [Fact]
    public void The_status_names_the_container_port_of_smtp_and_whether_tls_is_needed()
    {
        Assert.Equal((2025, "none"), ManagementApi.SendPort(new SmtpConfig()));
        Assert.Equal((2025, "starttls"), ManagementApi.SendPort(new SmtpConfig { PlainAuthNetworks = "" }));
        Assert.Equal((2587, "starttls"), ManagementApi.SendPort(new SmtpConfig { Port = 0 }));
        Assert.Equal((2465, "tls"), ManagementApi.SendPort(new SmtpConfig { Port = 0, SubmissionPort = 0 }));
    }

    [Theory]
    [InlineData("example.com", true)]
    [InlineData("mail.example.co.uk", true)]
    [InlineData("", true)]
    [InlineData("localhost", false)]
    [InlineData("not a domain", false)]
    [InlineData("example.com/x", false)]
    public void The_default_domain_is_a_domain_with_a_dot_or_nothing(string domain, bool valid)
        => Assert.Equal(valid, RelaySettingsService.IsValidDomain(domain));

    [Fact]
    public void A_public_address_set_on_a_page_survives_a_restart()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ms-config-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            new ConfigStore(dir).Update(c =>
            {
                c.Server.PublicUrl = "https://smtp.example.com";
                c.Server.ForceSecureCookie = true;
            });

            var again = new ConfigStore(dir).Current.Server;
            Assert.Equal("https://smtp.example.com", again.PublicUrl);
            Assert.True(again.ForceSecureCookie);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
