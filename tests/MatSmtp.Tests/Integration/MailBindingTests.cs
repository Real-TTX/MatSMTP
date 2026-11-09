using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MailKit.Net.Smtp;
using MailKit.Security;
using MatPlatform;
using MatSmtp.Configuration;
using MatSmtp.Core.Security;
using MatSmtp.Data;
using MatSmtp.Data.Entities;
using MatSmtp.Platform;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MimeKit;

namespace MatSmtp.Tests.Integration;

/// <summary>
/// MatSMTP as the provider of <c>mail.smtp</c> (MatDesign docs/PLATFORM.md "Bindings"): a binding is a relay user of the API, made by a broker with the key
/// (<c>PUT /api/bindings/mail.smtp/{consumer}</c>) or by an administrator in the Connections area; its password leaves once, only its manager removes it, and the
/// relay user signs in over SMTP with the six values. <c>/api/v1</c> keeps working beside it (ManagementApiTests).
/// </summary>
[Collection(IntegrationCollection.Name)]
public sealed class MailBindingTests : IClassFixture<AppFixture>, IAsyncLifetime
{
    private const string AdminEmail = "admin@example.test";
    private const string AdminPassword = "Admin-Passwort-2026";

    private static readonly string[] Variables = ["MATSMTP_FROM", "MATSMTP_HOST", "MATSMTP_PASSWORD", "MATSMTP_PORT", "MATSMTP_TLS", "MATSMTP_USERNAME"];

    private readonly AppFixture _app;
    private readonly FakeUpstream _upstream = new();

    public MailBindingTests(AppFixture app) => _app = app;

    public async Task InitializeAsync()
    {
        await _app.EnsureAdminAsync();
        await _app.WithScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<MsDbContext>();
            await db.SmtpAccount.IgnoreQueryFilters().ExecuteUpdateAsync(s => s.SetProperty(a => a.IsDefault, false));
            db.SmtpAccount.Add(new SmtpAccount { Name = "Upstream Bindings", Host = "127.0.0.1", Port = _upstream.Port, Security = SmtpSecurity.None, IsDefault = true });
            await db.SaveChangesAsync();
            sp.GetRequiredService<ConfigStore>().Update(c =>
            {
                // Nobody sends without a relay user here: only the users of the bindings get through.
                c.Smtp.TrustedNetworks = "";
                c.Smtp.PlainAuthNetworks = SmtpConfig.DefaultPrivateNetworks;
                c.Smtp.AllowPlainAuth = false;
                c.Smtp.DefaultSenderDomain = "";
                c.Delivery.Paused = false;
            });
        });
    }

    public async Task DisposeAsync() => await _upstream.DisposeAsync();

    private static MatBindRequest Request(string consumer, string managedBy, bool renew = false, Dictionary<string, string>? parameters = null, string? providerInternalUrl = "http://mattiverse-matsmtp-matsmtp:2525")
        => new(MatCapabilities.MailSmtp, 1,
            new MatBindingParty(consumer, "MatDrive", "https://drive.example.com", "http://mattiverse-matdrive-web:4810"),
            new MatBindingParty("matsmtp", "MatSMTP", "https://smtp.example.com", providerInternalUrl),
            parameters ?? new Dictionary<string, string>(), managedBy, renew);

    /// <summary>Runs the handler of <c>mail.smtp</c> in a scope of its own, like one request.</summary>
    private Task<T> HandlerAsync<T>(Func<IMatBindingHandler, Task<T>> action)
        => _app.WithScopeAsync(sp => action(sp.GetServices<IMatBindingHandler>().Single(h => h.Capability == MatCapabilities.MailSmtp)));

    private Task<RelayUser?> UserAsync(string apiId)
        => _app.WithScopeAsync(sp => sp.GetRequiredService<MsDbContext>().RelayUser.AsNoTracking().FirstOrDefaultAsync(u => u.ApiId == apiId));

    private static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

    private static async Task<JsonElement> BodyAsync(HttpResponseMessage response) => JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    [Fact]
    public async Task A_binding_is_a_relay_user_whose_password_leaves_once_and_only_its_manager_removes_it()
    {
        var first = await HandlerAsync(h => h.BindAsync(Request("matdrive", "mattiverse"), CancellationToken.None));
        Assert.Equal(MatCapabilities.Of(MatCapabilities.MailSmtp).Values.Select(v => v.Key).Order(), first.Values.Keys.Order());
        Assert.Equal("mattiverse-matsmtp-matsmtp", first.Values["host"]);
        Assert.Equal(_app.SmtpPort.ToString(System.Globalization.CultureInfo.InvariantCulture), first.Values["port"]);
        Assert.Equal("none", first.Values["tls"]);
        Assert.Equal("matdrive", first.Values["username"]);
        Assert.Equal("matdrive@localhost", first.Values["from"]);
        var password = first.Values["password"]!;
        Assert.True(password.Length >= 32);

        // The relay user of the API: its id, the consumer's name, the manager, its one sender, the password as a hash only.
        var user = (await UserAsync("matdrive"))!;
        Assert.Equal("matdrive", user.UserName);
        Assert.Equal("MatDrive", user.Description);
        Assert.Equal("mattiverse", user.ManagedBy);
        Assert.Equal("matdrive@localhost", user.AllowedSenders);
        Assert.True(user.IsEnabled);
        Assert.DoesNotContain(password, user.PasswordHash);
        Assert.True(PasswordHasher.Verify(password, user.PasswordHash));

        // The same request again is the same relay user, and no password goes out.
        var again = await HandlerAsync(h => h.BindAsync(Request("matdrive", "mattiverse"), CancellationToken.None));
        Assert.Null(again.Values["password"]);
        Assert.Equal("matdrive", again.Values["username"]);
        var same = (await UserAsync("matdrive"))!;
        Assert.Equal(user.Id, same.Id);
        Assert.True(PasswordHasher.Verify(password, same.PasswordHash));

        // Renew: a new password, the old one is void at once.
        var renewed = await HandlerAsync(h => h.BindAsync(Request("matdrive", "mattiverse", renew: true), CancellationToken.None));
        var newPassword = renewed.Values["password"]!;
        Assert.NotEqual(password, newPassword);
        var hash = (await UserAsync("matdrive"))!.PasswordHash;
        Assert.False(PasswordHasher.Verify(password, hash));
        Assert.True(PasswordHasher.Verify(newPassword, hash));

        // The list: the consumer, its name, its manager, since when.
        var listed = Assert.Single(await HandlerAsync(h => h.ListAsync(CancellationToken.None)), b => b.Consumer == "matdrive");
        Assert.Equal(MatCapabilities.MailSmtp, listed.Capability);
        Assert.Equal("MatDrive", listed.Name);
        Assert.Equal("mattiverse", listed.ManagedBy);
        Assert.NotNull(listed.Since);
        Assert.Null(listed.LastUsed);

        // Only its manager removes it (null: anyone); a second time there is nothing to remove.
        Assert.False(await HandlerAsync(h => h.UnbindAsync("matdrive", "someone-else", CancellationToken.None)));
        Assert.NotNull(await UserAsync("matdrive"));
        Assert.True(await HandlerAsync(h => h.UnbindAsync("matdrive", "mattiverse", CancellationToken.None)));
        Assert.Null(await UserAsync("matdrive"));
        Assert.False(await HandlerAsync(h => h.UnbindAsync("matdrive", "mattiverse", CancellationToken.None)));
        Assert.DoesNotContain(await HandlerAsync(h => h.ListAsync(CancellationToken.None)), b => b.Consumer == "matdrive");

        await HandlerAsync(h => h.BindAsync(Request("matdrive", "manual"), CancellationToken.None));
        Assert.True(await HandlerAsync(h => h.UnbindAsync("matdrive", null, CancellationToken.None)));
    }

    [Fact]
    public async Task The_sender_is_the_parameter_at_the_default_domain_and_the_host_falls_back_to_the_server_name()
    {
        await _app.WithScopeAsync(sp =>
        {
            sp.GetRequiredService<ConfigStore>().Update(c => c.Smtp.DefaultSenderDomain = "example.com");
            return Task.CompletedTask;
        });

        try
        {
            var bound = await HandlerAsync(h => h.BindAsync(Request("matsplit", "mattiverse", parameters: new() { ["sender"] = "split" }, providerInternalUrl: null), CancellationToken.None));
            Assert.Equal("split@example.com", bound.Values["from"]);
            Assert.Equal("split@example.com", (await UserAsync("matsplit"))!.AllowedSenders);

            // Without the internal address of the provider: MatSMTP's own name (the one of its greeting and its certificate).
            Assert.Equal(_app.Services.GetRequiredService<ConfigStore>().Current.Smtp.Hostname, bound.Values["host"]);

            // A parameter that is no plain part before the @ (a pattern, a list, a whole address) is not taken: the consumer id is.
            foreach (var wrong in new[] { "*", "a,b", "x@evil.example", "/.*/" })
            {
                var other = await HandlerAsync(h => h.BindAsync(Request("matsplit", "mattiverse", parameters: new() { ["sender"] = wrong }), CancellationToken.None));
                Assert.Equal("matsplit@example.com", other.Values["from"]);
            }

            // A one-letter consumer gets a relay user of the API all the same.
            var shortId = await HandlerAsync(h => h.BindAsync(Request("x", "mattiverse"), CancellationToken.None));
            Assert.Equal("x-app", shortId.Values["username"]);
            Assert.True(await HandlerAsync(h => h.UnbindAsync("x", "mattiverse", CancellationToken.None)));
        }
        finally
        {
            await HandlerAsync(h => h.UnbindAsync("matsplit", null, CancellationToken.None));
            await _app.WithScopeAsync(sp =>
            {
                sp.GetRequiredService<ConfigStore>().Update(c => c.Smtp.DefaultSenderDomain = "");
                return Task.CompletedTask;
            });
        }
    }

    [Fact]
    public async Task A_broker_binds_with_the_key_and_the_relay_user_sends_with_the_six_variables()
    {
        const string body = """
            { "version": 1, "managedBy": "mattiverse",
              "consumer": { "name": "MatDrive", "url": "https://drive.example.com", "internalUrl": "http://mattiverse-matdrive-web:4810" },
              "provider": { "url": "https://smtp.example.com", "internalUrl": "http://mattiverse-matsmtp-matsmtp:2525" },
              "params": { "sender": "drive" }, "renew": false }
            """;
        using var anonymous = _app.ApiClient(null);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PutAsync("/api/bindings/mail.smtp/matdrive", Json(body))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/bindings")).StatusCode);

        using var client = _app.ApiClient();
        var created = await client.PutAsync("/api/bindings/mail.smtp/matdrive", Json(body));
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var variables = (await BodyAsync(created)).GetProperty("variables");
        Assert.Equal(Variables, variables.EnumerateObject().Select(p => p.Name).Order().ToArray());
        Assert.Equal("mattiverse-matsmtp-matsmtp", variables.GetProperty("MATSMTP_HOST").GetString());
        Assert.Equal(_app.SmtpPort.ToString(System.Globalization.CultureInfo.InvariantCulture), variables.GetProperty("MATSMTP_PORT").GetString());
        Assert.Equal("none", variables.GetProperty("MATSMTP_TLS").GetString());
        Assert.Equal("drive@localhost", variables.GetProperty("MATSMTP_FROM").GetString());
        var username = variables.GetProperty("MATSMTP_USERNAME").GetString()!;
        var password = variables.GetProperty("MATSMTP_PASSWORD").GetString()!;
        Assert.Equal("matdrive", username);

        // The same request again: the password is not handed out again, it is set.
        var again = await client.PutAsync("/api/bindings/mail.smtp/matdrive", Json(body));
        var againText = await again.Content.ReadAsStringAsync();
        Assert.DoesNotContain(password, againText);
        Assert.True(JsonDocument.Parse(againText).RootElement.GetProperty("variables").GetProperty("MATSMTP_PASSWORD").GetProperty("isSet").GetBoolean());

        // A capability MatSMTP does not provide, a wrong id, a version it does not speak.
        Assert.Equal(HttpStatusCode.NotFound, (await client.PutAsync("/api/bindings/office.edit/matdrive", Json(body))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsync("/api/bindings/mail.smtp/Mat_Drive", Json(body))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsync("/api/bindings/mail.smtp/matdrive", Json(body.Replace("\"version\": 1", "\"version\": 2")))).StatusCode);

        try
        {
            // The relay user signs in inside the Docker network without TLS and sends as its sender, and as nobody else.
            using (var smtp = new SmtpClient { Timeout = 10000 })
            {
                await smtp.ConnectAsync("127.0.0.1", int.Parse(variables.GetProperty("MATSMTP_PORT").GetString()!, System.Globalization.CultureInfo.InvariantCulture), SecureSocketOptions.None);
                await smtp.AuthenticateAsync(username, password);
                await smtp.SendAsync(Mail("MatDrive <drive@localhost>", "kunde@example.test", "Von MatDrive über die Bindung"));
                var other = await Assert.ThrowsAsync<SmtpCommandException>(() => smtp.SendAsync(Mail("ceo@example.com", "kunde@example.test", "Fremder Absender Bindung")));
                Assert.Equal(550, (int)other.StatusCode);
                await smtp.DisconnectAsync(true);
            }

            var upstream = await UpstreamAsync("Von MatDrive über die Bindung");
            Assert.Equal("drive@localhost", upstream.From);

            // The broker's list and the engine API see the same relay user; it was used now.
            var binding = (await BodyAsync(await client.GetAsync("/api/bindings"))).GetProperty("bindings").EnumerateArray().Single(b => b.GetProperty("consumer").GetString() == "matdrive");
            Assert.Equal("mail.smtp", binding.GetProperty("capability").GetString());
            Assert.Equal("MatDrive", binding.GetProperty("name").GetString());
            Assert.Equal("mattiverse", binding.GetProperty("managedBy").GetString());
            Assert.Equal(JsonValueKind.String, binding.GetProperty("lastUsed").ValueKind);
            var v1 = JsonDocument.Parse(await client.GetStringAsync("/api/v1/relay-users")).RootElement.EnumerateArray().Single(u => u.GetProperty("id").GetString() == "matdrive");
            Assert.Equal(["drive@localhost"], v1.GetProperty("allowedSenders").EnumerateArray().Select(e => e.GetString()!).ToArray());
            Assert.Equal("mattiverse", v1.GetProperty("managedBy").GetString());
        }
        finally
        {
            Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync("/api/bindings/mail.smtp/matdrive?managedBy=someone-else")).StatusCode);
            Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync("/api/bindings/mail.smtp/matdrive?managedBy=mattiverse")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync("/api/bindings/mail.smtp/matdrive?managedBy=mattiverse")).StatusCode);
        }

        // Gone with the binding: the password no longer signs in.
        using var after = new SmtpClient { Timeout = 10000 };
        await after.ConnectAsync("127.0.0.1", _app.SmtpPort, SecureSocketOptions.None);
        await Assert.ThrowsAsync<AuthenticationException>(() => after.AuthenticateAsync(username, password));
    }

    [Fact]
    public async Task A_binding_takes_over_a_relay_user_of_the_pages_or_of_another_manager_while_api_v1_refuses_both()
    {
        var pageUserId = await _app.WithScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<MsDbContext>();
            var page = new RelayUser { UserName = "matplay", Description = "von Hand", PasswordHash = PasswordHasher.Hash("Page-Passwort-2026"), AllowedSenders = "*" };
            db.RelayUser.Add(page);
            await db.SaveChangesAsync();
            return page.Id;
        });
        using var client = _app.ApiClient();
        Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsync("/api/v1/relay-users/matplay", Json("""{"allowedSenders":["*"],"managedBy":"mattiverse"}"""))).StatusCode);

        // The binding is new for the consumer: the user of the pages becomes it, with a new password and the one sender.
        var bound = await HandlerAsync(h => h.BindAsync(Request("matplay", "mattiverse"), CancellationToken.None));
        var user = (await UserAsync("matplay"))!;
        Assert.Equal(pageUserId, user.Id);
        Assert.Equal("mattiverse", user.ManagedBy);
        Assert.Equal("matplay@localhost", user.AllowedSenders);
        Assert.False(PasswordHasher.Verify("Page-Passwort-2026", user.PasswordHash));
        Assert.True(PasswordHasher.Verify(bound.Values["password"]!, user.PasswordHash));

        // Another manager: /api/v1 refuses, a binding takes it over (the caller of a binding has the key of the app or is its administrator).
        Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsync("/api/v1/relay-users/matplay", Json("""{"allowedSenders":["*"],"managedBy":"someone-else"}"""))).StatusCode);
        var taken = await HandlerAsync(h => h.BindAsync(Request("matplay", "someone-else"), CancellationToken.None));
        Assert.Null(taken.Values["password"]);
        Assert.Equal("someone-else", (await UserAsync("matplay"))!.ManagedBy);
        Assert.True(await HandlerAsync(h => h.UnbindAsync("matplay", "someone-else", CancellationToken.None)));
    }

    [Fact]
    public async Task An_administrator_connects_an_app_by_hand_in_the_connections_area_and_sees_its_values_once()
    {
        using var browser = _app.NewClient();
        await AppFixture.LoginAsync(browser, AdminEmail, AdminPassword);

        var page = await AppFixture.TextAsync(browser, "/Admin/Connections");
        Assert.Contains("<h1", page);
        Assert.Contains("data-connections", page);
        Assert.Contains("Versendet Mails für", page);
        Assert.Contains("App von Hand verbinden", page);
        Assert.Contains("href=\"/Admin/Connections\"", page);

        var connected = await AppFixture.PostFormAsync(browser, "/Admin/Connections",
            new Dictionary<string, string> { ["do"] = "connect", ["capability"] = "mail.smtp", ["name"] = "MatDrive", ["returnUrl"] = "/Admin/Connections" },
            MatConnectionEndpoints.FormPath);
        Assert.Equal(HttpStatusCode.Redirect, connected.StatusCode);
        Assert.Equal("/Admin/Connections", AppFixture.LocationPath(connected));

        // The values once: the relay user and its password, the host of MatSMTP (its own name without MAT_INTERNAL_URL), port, TLS and the sender.
        var shown = await AppFixture.TextAsync(browser, "/Admin/Connections");
        Assert.Contains("Verbunden: MatDrive", shown);
        foreach (var variable in Variables)
        {
            Assert.Contains(variable, shown);
        }

        var password = Regex.Match(shown, "MATSMTP_PASSWORD</code></dt><dd><code id=\"[^\"]+\">([^<]+)</code>").Groups[1].Value;
        var user = (await UserAsync("matdrive"))!;
        Assert.True(PasswordHasher.Verify(password, user.PasswordHash));
        Assert.Equal(MatConnections.ManagedByHand, user.ManagedBy);
        Assert.Equal("MatDrive", user.Description);

        // Never again: the list shows the app made by hand, no password.
        var later = await AppFixture.TextAsync(browser, "/Admin/Connections");
        Assert.DoesNotContain(password, later);
        Assert.Contains("MatDrive", later);
        Assert.Contains("von Hand", later);
        Assert.Contains("Verbindung einer App", await AppFixture.TextAsync(browser, $"/RelayUsers/Edit?id={user.Id}"));

        // Renew shows a new password; remove takes the relay user away.
        await AppFixture.PostFormAsync(browser, "/Admin/Connections",
            new Dictionary<string, string> { ["do"] = "renew", ["capability"] = "mail.smtp", ["consumer"] = "matdrive", ["returnUrl"] = "/Admin/Connections" }, MatConnectionEndpoints.FormPath);
        var renewed = Regex.Match(await AppFixture.TextAsync(browser, "/Admin/Connections"), "MATSMTP_PASSWORD</code></dt><dd><code id=\"[^\"]+\">([^<]+)</code>").Groups[1].Value;
        Assert.NotEqual(password, renewed);
        Assert.True(PasswordHasher.Verify(renewed, (await UserAsync("matdrive"))!.PasswordHash));

        await AppFixture.PostFormAsync(browser, "/Admin/Connections",
            new Dictionary<string, string> { ["do"] = "remove", ["capability"] = "mail.smtp", ["consumer"] = "matdrive", ["returnUrl"] = "/Admin/Connections" }, MatConnectionEndpoints.FormPath);
        Assert.Null(await UserAsync("matdrive"));
        Assert.Contains("Verbindung entfernt", await AppFixture.TextAsync(browser, "/Admin/Connections"));
    }

    [Fact]
    public async Task Only_an_administrator_sees_and_uses_the_connections_area()
    {
        await _app.CreateUserAsync("verbindung@example.test", "Normal-Passwort-2026", UserRole.User);
        using var user = _app.NewClient();
        await AppFixture.LoginAsync(user, "verbindung@example.test", "Normal-Passwort-2026");
        Assert.NotEqual(HttpStatusCode.OK, (await user.GetAsync("/Admin/Connections")).StatusCode);
        Assert.DoesNotContain("/Admin/Connections", await AppFixture.TextAsync(user, "/Messages"));

        var fields = new Dictionary<string, string> { ["do"] = "connect", ["capability"] = "mail.smtp", ["name"] = "Eindringling" };
        Assert.Equal(HttpStatusCode.Forbidden, (await user.PostAsync(MatConnectionEndpoints.FormPath, new FormUrlEncodedContent(fields))).StatusCode);
        using var anonymous = _app.NewClient();
        Assert.Equal(HttpStatusCode.Forbidden, (await anonymous.PostAsync(MatConnectionEndpoints.FormPath, new FormUrlEncodedContent(fields))).StatusCode);
        Assert.Null(await UserAsync("eindringling"));
    }

    private static MimeMessage Mail(string from, string to, string subject)
    {
        var m = new MimeMessage();
        m.From.Add(MailboxAddress.Parse(from));
        m.To.Add(MailboxAddress.Parse(to));
        m.Subject = subject;
        m.Body = new TextPart("plain") { Text = "Hallo aus dem Test." };
        return m;
    }

    private async Task<UpstreamMail> UpstreamAsync(string subject)
    {
        for (var i = 0; i < 200; i++)
        {
            if (_upstream.Received.FirstOrDefault(r => r.Message.Subject == subject) is { } hit)
            {
                return hit;
            }

            await Task.Delay(50);
        }

        throw new Xunit.Sdk.XunitException($"The upstream server never got \"{subject}\".");
    }
}
