using System.Net;
using MatSmtp.Core.Relay;
using MatSmtp.Data;
using MatSmtp.Data.Entities;
using MatSmtp.Services;
using MatSmtp.Services.Relay;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MatSmtp.Tests.Integration;

/// <summary>Every page of the application opens with data and as the people who may see it; the roles keep to their side; a share link shows its message and nothing else.</summary>
[Collection(IntegrationCollection.Name)]
public sealed class PagesTests : IClassFixture<AppFixture>
{
    private readonly AppFixture _app;

    public PagesTests(AppFixture app) => _app = app;

    /// <summary>One of everything: an account, a relay user, a webhook, a rule, a message in the queue and a share link.</summary>
    private Task<(long Account, long RelayUser, long Webhook, long Rule, long Message, ShareLink Link)> DataAsync() => _app.WithScopeAsync(async sp =>
    {
        sp.GetRequiredService<Configuration.ConfigStore>().Update(c => c.Delivery.Paused = true);
        var db = sp.GetRequiredService<MsDbContext>();
        var account = new SmtpAccount { Name = "Provider", Host = "smtp.example.test", Port = 587, IsDefault = true };
        var user = new RelayUser { UserName = "shop-" + Guid.NewGuid().ToString("N")[..4], PasswordHash = MatSmtp.Core.Security.PasswordHasher.Hash("Shop-Passwort-2026") };
        var hook = new Webhook { Name = "Tickets", Url = "https://tickets.example.test/hook" };
        db.AddRange(account, user, hook);
        await db.SaveChangesAsync();
        var rule = new Rule { Name = "Shop", MatchSenders = "shop.test", MatchRelayUserId = user.Id, MatchAuth = AuthCondition.Authenticated, Action = RuleAction.Relay, AccountId = account.Id, WebhookId = hook.Id, RewriteFrom = "noreply@firma.test", Position = 10 };
        db.Rule.Add(rule);
        await db.SaveChangesAsync();

        var raw = System.Text.Encoding.UTF8.GetBytes("From: app@app.local\r\nTo: kunde@example.test\r\nSubject: Seiten-Test\r\n\r\nHallo\r\n");
        var received = await sp.GetRequiredService<RelayService>().ReceiveAsync(new SessionInfo(IPAddress.Loopback, "test", null, 25, false), "app@app.local", ["kunde@example.test"], raw);
        var link = await sp.GetRequiredService<ShareService>().CreateAsync(received.MessageId!.Value, 7, "für den Kunden");
        return (account.Id, user.Id, hook.Id, rule.Id, received.MessageId.Value, link);
    });

    [Fact]
    public async Task Every_page_opens_for_an_administrator()
    {
        await _app.EnsureAdminAsync();
        var (account, relayUser, webhook, rule, message, _) = await DataAsync();
        using var client = _app.NewClient();
        await AppFixture.LoginAsync(client, "admin@example.test", "Admin-Passwort-2026");

        string[] urls =
        [
            "/", "/Messages", "/Messages?status=Queued&sort=subject&dir=desc", "/Messages?zeit=24h&q=Seiten", "/Messages/Queue", $"/Messages/Details?id={message}",
            "/Rules", "/Rules?aktion=Relay", "/Rules/Edit", $"/Rules/Edit?id={rule}", $"/Rules/Edit?copy={rule}", "/RuleTest",
            "/Accounts", "/Accounts/Edit", $"/Accounts/Edit?id={account}", "/RelayUsers", "/RelayUsers/Edit", $"/RelayUsers/Edit?id={relayUser}",
            "/Webhooks", "/Webhooks/Edit", $"/Webhooks/Edit?id={webhook}", "/Tools/Send",
            "/Admin/Smtp", "/Admin/Delivery", "/Admin/Users", "/Admin/Users/Edit", "/Admin/Shares", "/Admin/Backup", "/Admin/Backup/Plans", "/Admin/Backup/Plans/Edit",
            "/Admin/Backup/Targets", "/Admin/Backup/Targets/Edit", "/Admin/Backup/Restore", "/Admin/Security", "/Admin/Activity", "/Admin/System",
            "/Account/Profile", "/Account/Security",
        ];

        var failures = new List<string>();
        foreach (var url in urls)
        {
            var response = await client.GetAsync(url);
            var text = response.StatusCode == HttpStatusCode.OK ? await response.Content.ReadAsStringAsync() : "";
            if (response.StatusCode != HttpStatusCode.OK || text.Contains("Exception", StringComparison.Ordinal) || !text.Contains("<h1", StringComparison.Ordinal))
            {
                failures.Add($"{(int)response.StatusCode} {url}");
            }
        }

        Assert.True(failures.Count == 0, "Pages that failed: " + string.Join(", ", failures));

        var eml = await client.GetAsync($"/messages/{message}/eml");
        Assert.Equal(HttpStatusCode.OK, eml.StatusCode);
        Assert.Equal("message/rfc822", eml.Content.Headers.ContentType?.MediaType);
        Assert.Contains("Subject: Seiten-Test", await eml.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task The_rule_tester_shows_what_the_rules_would_do()
    {
        await _app.EnsureAdminAsync();
        var (_, relayUser, _, _, _, _) = await DataAsync();
        var userName = await _app.WithScopeAsync(sp => sp.GetRequiredService<MsDbContext>().RelayUser.Where(u => u.Id == relayUser).Select(u => u.UserName).FirstAsync());
        using var client = _app.NewClient();
        await AppFixture.LoginAsync(client, "admin@example.test", "Admin-Passwort-2026");

        var response = await AppFixture.PostFormAsync(client, "/RuleTest", new Dictionary<string, string>
        {
            ["Input.From"] = "app@shop.test", ["Input.To"] = "kunde@example.test", ["Input.ClientIp"] = "10.0.0.9", ["Input.AuthUser"] = userName,
        });
        var text = await AppFixture.TextAsync(response);
        Assert.Contains("durch „Shop“", text);
        Assert.Contains("noreply@firma.test", text);
        Assert.Contains("Tickets", text);
    }

    [Fact]
    public async Task A_user_stays_out_of_the_configuration_and_the_anonymous_visitor_is_sent_to_the_sign_in()
    {
        await _app.EnsureAdminAsync();
        await _app.CreateUserAsync("normal@example.test", "Normal-Passwort-2026", UserRole.User);

        using var anonymous = _app.NewClient();
        foreach (var url in new[] { "/", "/Messages", "/Messages/Queue", "/Rules", "/Accounts", "/Admin/Users", "/messages/1/eml", "/api/state", "/RuleTest" })
        {
            var status = (await anonymous.GetAsync(url)).StatusCode;
            Assert.True(status is HttpStatusCode.Redirect or HttpStatusCode.Unauthorized, $"{url} answered {(int)status} to somebody who is not signed in");
        }

        using var user = _app.NewClient();
        await AppFixture.LoginAsync(user, "normal@example.test", "Normal-Passwort-2026");
        foreach (var url in new[] { "/", "/Messages", "/Messages/Queue", "/RuleTest" })
        {
            Assert.Equal(HttpStatusCode.OK, (await user.GetAsync(url)).StatusCode);
        }

        foreach (var url in new[] { "/Rules", "/Rules/Edit", "/Accounts", "/RelayUsers", "/Webhooks", "/Tools/Send", "/Admin/Smtp", "/Admin/Delivery", "/Admin/Users", "/Admin/Backup", "/Admin/Shares", "/Admin/System", "/Admin/Security" })
        {
            var status = (await user.GetAsync(url)).StatusCode;
            Assert.NotEqual(HttpStatusCode.OK, status);
        }

        // the menu of a user has no configuration
        var menu = await AppFixture.TextAsync(user, "/Messages");
        Assert.DoesNotContain("/Admin/Users", menu);
        Assert.DoesNotContain("href=\"/Rules\"", menu);
    }

    [Fact]
    public async Task A_share_link_shows_the_delivery_without_an_account_and_ends_when_revoked()
    {
        await _app.EnsureAdminAsync();
        var (_, _, _, _, _, link) = await DataAsync();
        using var anonymous = _app.NewClient();

        var page = await anonymous.GetAsync($"/s/{link.Token}");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var text = await AppFixture.TextAsync(page);
        Assert.Contains("Seiten-Test", text);
        Assert.Contains("kunde@example.test", text);
        Assert.DoesNotContain("127.0.0.1", text);
        Assert.DoesNotContain("Hallo", text);

        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync($"/s/{Guid.NewGuid()}")).StatusCode);

        await _app.WithScopeAsync(sp => sp.GetRequiredService<ShareService>().RevokeAsync(link.Id));
        Assert.Equal(HttpStatusCode.Gone, (await anonymous.GetAsync($"/s/{link.Token}")).StatusCode);
        Assert.Equal(1, await _app.WithScopeAsync(sp => sp.GetRequiredService<MsDbContext>().ShareLink.Where(l => l.Id == link.Id).Select(l => l.Views).FirstAsync()));
    }
}
