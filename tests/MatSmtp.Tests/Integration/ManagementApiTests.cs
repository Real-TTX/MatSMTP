using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using MailKit.Net.Smtp;
using MailKit.Security;
using MatSmtp.Configuration;
using MatSmtp.Core.Security;
using MatSmtp.Data;
using MatSmtp.Data.Entities;
using MatSmtp.Services.Relay;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MimeKit;

namespace MatSmtp.Tests.Integration;

/// <summary>
/// The management API of the engine for Mattiverse (<c>/api/v1</c>), pinned: the exact shapes and status codes, the key, a password that leaves only once,
/// <c>managedBy</c>, and that a relay user made through it sends from the Docker network without TLS, limited to its senders.
/// </summary>
[Collection(IntegrationCollection.Name)]
public sealed class ManagementApiTests : IClassFixture<AppFixture>, IAsyncLifetime
{
    private readonly AppFixture _app;
    private readonly FakeUpstream _upstream = new();

    public ManagementApiTests(AppFixture app) => _app = app;

    public async Task InitializeAsync()
    {
        await _app.EnsureAdminAsync();
        await _app.WithScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<MsDbContext>();
            await db.Rule.IgnoreQueryFilters().ExecuteDeleteAsync();
            await db.SmtpAccount.IgnoreQueryFilters().ExecuteUpdateAsync(s => s.SetProperty(a => a.IsDefault, false));
            db.SmtpAccount.Add(new SmtpAccount { Name = "Upstream API", Host = "127.0.0.1", Port = _upstream.Port, Security = SmtpSecurity.None, IsDefault = true });
            await db.SaveChangesAsync();
            sp.GetRequiredService<ConfigStore>().Update(c =>
            {
                // Nobody sends without a relay user here: only the API's users get through.
                c.Smtp.TrustedNetworks = "";
                c.Smtp.RequireAuth = false;
                c.Smtp.PlainAuthNetworks = SmtpConfig.DefaultPrivateNetworks;
                c.Smtp.AllowPlainAuth = false;
                c.Smtp.DefaultSenderDomain = "";
                c.Delivery.Paused = false;
            });
        });
    }

    public async Task DisposeAsync()
    {
        await _app.WithScopeAsync(sp =>
        {
            sp.GetRequiredService<ConfigStore>().Update(c =>
            {
                c.Smtp.TrustedNetworks = "127.0.0.0/8, ::1/128";
                c.Smtp.DefaultSenderDomain = "";
            });
            return Task.CompletedTask;
        });
        await _upstream.DisposeAsync();
    }

    private static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

    private static async Task<JsonElement> BodyAsync(HttpResponseMessage response) => JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    private static string[] Keys(JsonElement element) => [.. element.EnumerateObject().Select(p => p.Name).Order()];

    [Fact]
    public async Task A_missing_or_wrong_key_is_401_with_an_error_and_the_cookie_counts_for_nothing()
    {
        foreach (var key in new string?[] { null, "wrong-key" })
        {
            using var client = _app.ApiClient(key);
            foreach (var (method, url) in new[] { (HttpMethod.Get, "/api/v1/status"), (HttpMethod.Get, "/api/v1/relay-users"), (HttpMethod.Get, "/api/v1/settings"), (HttpMethod.Delete, "/api/v1/relay-users/x1?managedBy=mattiverse") })
            {
                var response = await client.SendAsync(new HttpRequestMessage(method, url));
                Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
                Assert.Equal(["error"], Keys(await BodyAsync(response)));
            }
        }

        // A signed-in administrator without the key is no API caller.
        using var browser = _app.NewClient();
        await AppFixture.LoginAsync(browser, "admin@example.test", "Admin-Passwort-2026");
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.GetAsync("/api/v1/status")).StatusCode);
    }

    [Fact]
    public async Task The_status_names_the_port_applications_in_the_docker_network_send_to()
    {
        using var client = _app.ApiClient();
        var response = await client.GetAsync("/api/v1/status");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await BodyAsync(response);
        Assert.Equal(["apiVersion", "app", "ok", "smtpEnabled", "smtpPort", "smtpTls", "version"], Keys(body));
        Assert.Equal("matsmtp", body.GetProperty("app").GetString());
        Assert.Equal(1, body.GetProperty("apiVersion").GetInt32());
        Assert.False(string.IsNullOrEmpty(body.GetProperty("version").GetString()));
        // The container port of plain SMTP (2025 in the image; a free port here), without TLS: AUTH works there from the private networks.
        Assert.Equal(_app.SmtpPort, body.GetProperty("smtpPort").GetInt32());
        Assert.Equal("none", body.GetProperty("smtpTls").GetString());

        // Without AUTH over plain connections from anywhere the applications have to use STARTTLS.
        await _app.WithScopeAsync(sp =>
        {
            sp.GetRequiredService<ConfigStore>().Update(c => c.Smtp.PlainAuthNetworks = "");
            return Task.CompletedTask;
        });
        try
        {
            Assert.Equal("starttls", (await BodyAsync(await client.GetAsync("/api/v1/status"))).GetProperty("smtpTls").GetString());
        }
        finally
        {
            await _app.WithScopeAsync(sp =>
            {
                sp.GetRequiredService<ConfigStore>().Update(c => c.Smtp.PlainAuthNetworks = SmtpConfig.DefaultPrivateNetworks);
                return Task.CompletedTask;
            });
        }
    }

    [Fact]
    public async Task A_relay_user_is_made_changed_listed_and_deleted_and_its_password_leaves_only_once()
    {
        using var client = _app.ApiClient();
        const string body = """{"displayName":"MatSplit","allowedSenders":["matsplit@example.com"],"managedBy":"mattiverse","renewSecret":false}""";

        var created = await client.PutAsync("/api/v1/relay-users/matsplit", Json(body));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var first = await BodyAsync(created);
        Assert.Equal(["created", "id", "password", "username"], Keys(first));
        Assert.Equal("matsplit", first.GetProperty("id").GetString());
        Assert.Equal("matsplit", first.GetProperty("username").GetString());
        Assert.True(first.GetProperty("created").GetBoolean());
        var password = first.GetProperty("password").GetString()!;
        Assert.True(password.Length >= 32);

        // Stored as a hash only.
        var stored = await _app.WithScopeAsync(sp => sp.GetRequiredService<MsDbContext>().RelayUser.AsNoTracking().SingleAsync(u => u.ApiId == "matsplit"));
        Assert.DoesNotContain(password, stored.PasswordHash);
        Assert.True(PasswordHasher.Verify(password, stored.PasswordHash));
        Assert.Equal("mattiverse", stored.ManagedBy);

        // The same call again changes nothing and returns no password.
        var again = await client.PutAsync("/api/v1/relay-users/matsplit", Json(body));
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        var second = await BodyAsync(again);
        Assert.Equal(["created", "id", "username"], Keys(second));
        Assert.False(second.GetProperty("created").GetBoolean());
        Assert.DoesNotContain(password, await again.Content.ReadAsStringAsync());
        Assert.True(PasswordHasher.Verify(password, (await _app.WithScopeAsync(sp => sp.GetRequiredService<MsDbContext>().RelayUser.AsNoTracking().SingleAsync(u => u.ApiId == "matsplit"))).PasswordHash));

        // A new one only when asked for, and the old one is void.
        var renewed = await BodyAsync(await client.PutAsync("/api/v1/relay-users/matsplit", Json(body.Replace("\"renewSecret\":false", "\"renewSecret\":true"))));
        var newPassword = renewed.GetProperty("password").GetString()!;
        Assert.NotEqual(password, newPassword);
        var hash = (await _app.WithScopeAsync(sp => sp.GetRequiredService<MsDbContext>().RelayUser.AsNoTracking().SingleAsync(u => u.ApiId == "matsplit"))).PasswordHash;
        Assert.False(PasswordHasher.Verify(password, hash));
        Assert.True(PasswordHasher.Verify(newPassword, hash));

        // The list: a plain array, never a secret.
        var listResponse = await client.GetAsync("/api/v1/relay-users");
        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
        var listText = await listResponse.Content.ReadAsStringAsync();
        Assert.DoesNotContain(newPassword, listText);
        Assert.DoesNotContain("v1.", listText);
        var list = JsonDocument.Parse(listText).RootElement;
        Assert.Equal(JsonValueKind.Array, list.ValueKind);
        var item = list.EnumerateArray().Single(u => u.GetProperty("id").GetString() == "matsplit");
        Assert.Equal(["allowedSenders", "displayName", "id", "lastUsedUtc", "managedBy", "username"], Keys(item));
        Assert.Equal("MatSplit", item.GetProperty("displayName").GetString());
        Assert.Equal(["matsplit@example.com"], item.GetProperty("allowedSenders").EnumerateArray().Select(e => e.GetString()!).ToArray());
        Assert.Equal("mattiverse", item.GetProperty("managedBy").GetString());
        Assert.Equal(JsonValueKind.Null, item.GetProperty("lastUsedUtc").ValueKind);

        // Another manager may neither change nor delete it.
        var foreign = await client.PutAsync("/api/v1/relay-users/matsplit", Json(body.Replace("mattiverse", "someone-else")));
        Assert.Equal(HttpStatusCode.Conflict, foreign.StatusCode);
        Assert.Equal(["error"], Keys(await BodyAsync(foreign)));
        Assert.Equal(HttpStatusCode.Conflict, (await client.DeleteAsync("/api/v1/relay-users/matsplit?managedBy=someone-else")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.DeleteAsync("/api/v1/relay-users/matsplit")).StatusCode);

        // Deleted, it is gone; a second delete is 404; the id can be used again.
        var deleted = await client.DeleteAsync("/api/v1/relay-users/matsplit?managedBy=mattiverse");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Empty(await deleted.Content.ReadAsByteArrayAsync());
        var gone = await client.DeleteAsync("/api/v1/relay-users/matsplit?managedBy=mattiverse");
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
        Assert.Equal(["error"], Keys(await BodyAsync(gone)));
        Assert.DoesNotContain("\"matsplit\"", await client.GetStringAsync("/api/v1/relay-users"));
        Assert.Equal(HttpStatusCode.Created, (await client.PutAsync("/api/v1/relay-users/matsplit", Json(body))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync("/api/v1/relay-users/matsplit?managedBy=mattiverse")).StatusCode);
    }

    [Fact]
    public async Task A_wrong_input_is_400_and_a_relay_user_of_the_pages_is_not_taken_over()
    {
        using var client = _app.ApiClient();
        foreach (var (id, body) in new[]
        {
            ("Bad_Id", """{"allowedSenders":["*"]}"""),
            ("x", """{"allowedSenders":["*"]}"""),
            ("no-senders", """{"allowedSenders":[]}"""),
            ("two-in-one", """{"allowedSenders":["a@example.com, b@example.com"]}"""),
            ("bad-regex", """{"allowedSenders":["/[/"]}"""),
            ("not-json", "{"),
        })
        {
            var response = await client.PutAsync("/api/v1/relay-users/" + id, Json(body));
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal(["error"], Keys(await BodyAsync(response)));
        }

        await _app.WithScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<MsDbContext>();
            db.RelayUser.Add(new RelayUser { UserName = "printer", PasswordHash = PasswordHasher.Hash("Printer-Passwort-2026") });
            await db.SaveChangesAsync();
        });
        var taken = await client.PutAsync("/api/v1/relay-users/printer", Json("""{"allowedSenders":["*"],"managedBy":"mattiverse"}"""));
        Assert.Equal(HttpStatusCode.Conflict, taken.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync("/api/v1/relay-users/printer?managedBy=mattiverse")).StatusCode);
    }

    [Fact]
    public async Task The_settings_of_a_headless_installation_are_read_and_written()
    {
        using var client = _app.ApiClient();
        var read = await client.GetAsync("/api/v1/settings");
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        var before = await BodyAsync(read);
        Assert.Equal(["defaultSenderDomain", "setupCompleted"], Keys(before));
        Assert.True(before.GetProperty("setupCompleted").GetBoolean()); // there is an administrator

        var written = await client.PutAsync("/api/v1/settings", Json("""{"setupCompleted":true,"defaultSenderDomain":"example.com"}"""));
        Assert.Equal(HttpStatusCode.OK, written.StatusCode);
        var after = await BodyAsync(written);
        Assert.True(after.GetProperty("setupCompleted").GetBoolean());
        Assert.Equal("example.com", after.GetProperty("defaultSenderDomain").GetString());
        Assert.Equal("example.com", _app.Services.GetRequiredService<ConfigStore>().Current.Smtp.DefaultSenderDomain);

        // Either field may be left out.
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsync("/api/v1/settings", Json("""{"setupCompleted":true}"""))).StatusCode);
        Assert.Equal("example.com", (await BodyAsync(await client.GetAsync("/api/v1/settings"))).GetProperty("defaultSenderDomain").GetString());
        Assert.Equal("", (await BodyAsync(await client.PutAsync("/api/v1/settings", Json("""{"defaultSenderDomain":""}"""))))
            .GetProperty("defaultSenderDomain").GetString());

        var wrong = await client.PutAsync("/api/v1/settings", Json("""{"defaultSenderDomain":"not a domain"}"""));
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        Assert.Equal(["error"], Keys(await BodyAsync(wrong)));
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsync("/api/v1/settings", Json("[1"))).StatusCode);
    }

    [Fact]
    public async Task A_relay_user_of_the_api_sends_from_the_docker_network_without_tls_only_as_its_senders()
    {
        using var client = _app.ApiClient();
        // What Mattiverse does when it knows no domain: the app sends as <slug>@localhost; MatSMTP gives it the default domain on the way out.
        var made = await BodyAsync(await client.PutAsync("/api/v1/relay-users/matplay",
            Json("""{"displayName":"MatPlay","allowedSenders":["matplay@localhost"],"managedBy":"mattiverse"}""")));
        var password = made.GetProperty("password").GetString()!;
        await client.PutAsync("/api/v1/settings", Json("""{"defaultSenderDomain":"example.com"}"""));

        try
        {
            // 127.0.0.1 is in the private networks: AUTH is offered and works without TLS.
            using (var smtp = new SmtpClient { Timeout = 10000 })
            {
                await smtp.ConnectAsync("127.0.0.1", _app.SmtpPort, SecureSocketOptions.None);
                Assert.False(smtp.IsSecure);
                Assert.Contains("PLAIN", smtp.AuthenticationMechanisms);
                await smtp.AuthenticateAsync("matplay", password);
                await smtp.SendAsync(Mail("MatPlay <matplay@localhost>", "kunde@example.test", "Von MatPlay ohne TLS"));

                // Another sender is refused, in the envelope and in the header.
                var other = await Assert.ThrowsAsync<SmtpCommandException>(() => smtp.SendAsync(Mail("ceo@example.com", "kunde@example.test", "Fremder Absender API")));
                Assert.Equal(550, (int)other.StatusCode);
                var header = Mail("ceo@example.com", "kunde@example.test", "Fremder Header API");
                var spoofed = await Assert.ThrowsAsync<SmtpCommandException>(() => smtp.SendAsync(header, MailboxAddress.Parse("matplay@localhost"), [MailboxAddress.Parse("kunde@example.test")]));
                Assert.Equal(550, (int)spoofed.StatusCode);
                await smtp.DisconnectAsync(true);
            }

            var upstream = await UpstreamAsync("Von MatPlay ohne TLS");
            Assert.Equal("matplay@example.com", upstream.From);
            Assert.Equal("matplay@example.com", upstream.Message.From.Mailboxes.Single().Address);
            Assert.Equal("matplay@localhost", upstream.Message.Headers["X-MatSMTP-Original-From"]);
            var message = await _app.EventuallyAsync(
                sp => sp.GetRequiredService<MsDbContext>().Message.AsNoTracking().FirstOrDefaultAsync(m => m.Subject == "Von MatPlay ohne TLS"),
                m => m?.Status == MessageStatus.Sent);
            Assert.Equal("matplay", message!.AuthUser);
            Assert.False(message.IsTls);
            Assert.Equal("matplay@localhost", message.OriginalFrom);

            // The use shows in the list.
            var listed = JsonDocument.Parse(await client.GetStringAsync("/api/v1/relay-users")).RootElement.EnumerateArray().Single(u => u.GetProperty("id").GetString() == "matplay");
            Assert.Equal(JsonValueKind.String, listed.GetProperty("lastUsedUtc").ValueKind);
        }
        finally
        {
            await client.DeleteAsync("/api/v1/relay-users/matplay?managedBy=mattiverse");
            await client.PutAsync("/api/v1/settings", Json("""{"defaultSenderDomain":""}"""));
        }
    }

    [Fact]
    public async Task Outside_the_private_networks_auth_needs_tls_and_a_client_that_tries_anyway_hears_538()
    {
        using var client = _app.ApiClient();
        var password = (await BodyAsync(await client.PutAsync("/api/v1/relay-users/outsider", Json("""{"allowedSenders":["*"],"managedBy":"mattiverse"}""")))).GetProperty("password").GetString()!;
        await _app.WithScopeAsync(sp =>
        {
            // As if 127.0.0.1 were a client on the internet.
            sp.GetRequiredService<ConfigStore>().Update(c => c.Smtp.PlainAuthNetworks = "10.0.0.0/8");
            return Task.CompletedTask;
        });

        try
        {
            using (var smtp = new SmtpClient { Timeout = 10000, ServerCertificateValidationCallback = (_, _, _, _) => true })
            {
                await smtp.ConnectAsync("127.0.0.1", _app.SmtpPort, SecureSocketOptions.None);
                Assert.Empty(smtp.AuthenticationMechanisms);
                await smtp.DisconnectAsync(true);
            }

            // A client that sends AUTH without being offered it.
            using (var tcp = new System.Net.Sockets.TcpClient())
            {
                await tcp.ConnectAsync("127.0.0.1", _app.SmtpPort);
                using var stream = tcp.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII);
                using var writer = new StreamWriter(stream, Encoding.ASCII) { NewLine = "\r\n", AutoFlush = true };
                Assert.StartsWith("220", await reader.ReadLineAsync());
                await writer.WriteLineAsync("EHLO test");
                string? line;
                while ((line = await reader.ReadLineAsync()) is not null && line.StartsWith("250-", StringComparison.Ordinal))
                {
                    Assert.DoesNotContain("AUTH", line);
                }

                Assert.DoesNotContain("AUTH", line);
                await writer.WriteLineAsync("AUTH PLAIN " + Convert.ToBase64String(Encoding.UTF8.GetBytes("\0outsider\0" + password)));
                Assert.StartsWith("538", await reader.ReadLineAsync());
                await writer.WriteLineAsync("QUIT");
            }

            // After STARTTLS it is offered, and works.
            using (var smtp = new SmtpClient { Timeout = 10000, ServerCertificateValidationCallback = (_, _, _, _) => true })
            {
                await smtp.ConnectAsync("127.0.0.1", _app.SmtpPort, SecureSocketOptions.StartTls);
                Assert.Contains("PLAIN", smtp.AuthenticationMechanisms);
                await smtp.AuthenticateAsync("outsider", password);
                await smtp.DisconnectAsync(true);
            }
        }
        finally
        {
            await _app.WithScopeAsync(sp =>
            {
                sp.GetRequiredService<ConfigStore>().Update(c => c.Smtp.PlainAuthNetworks = SmtpConfig.DefaultPrivateNetworks);
                return Task.CompletedTask;
            });
            await client.DeleteAsync("/api/v1/relay-users/outsider?managedBy=mattiverse");
        }
    }

    [Fact]
    public async Task On_the_submission_port_a_client_from_a_private_address_gets_no_auth_before_starttls_and_cannot_relay_without_it()
    {
        using var client = _app.ApiClient();
        var password = (await BodyAsync(await client.PutAsync("/api/v1/relay-users/submitter", Json("""{"allowedSenders":["*"],"managedBy":"mattiverse"}""")))).GetProperty("password").GetString()!;

        try
        {
            // 127.0.0.1 is in the private networks of plain AUTH, the trusted networks are empty: like an app in the Docker network on 2587.
            using (var smtp = new SmtpClient { Timeout = 10000 })
            {
                await smtp.ConnectAsync("127.0.0.1", _app.SubmissionPort, SecureSocketOptions.None);
                Assert.Empty(smtp.AuthenticationMechanisms);
                var refused = await Assert.ThrowsAsync<MailKit.ServiceNotAuthenticatedException>(() => smtp.SendAsync(Mail("app@example.com", "kunde@example.test", "Submission ohne Anmeldung")));
                Assert.Contains("Bitte zuerst anmelden", refused.Message);
                await smtp.DisconnectAsync(true);
            }

            await AssertAuthRefusedInClearAsync(_app.SubmissionPort, "submitter", password);

            // After STARTTLS it is offered and the message goes through.
            using (var smtp = new SmtpClient { Timeout = 10000, ServerCertificateValidationCallback = (_, _, _, _) => true })
            {
                await smtp.ConnectAsync("127.0.0.1", _app.SubmissionPort, SecureSocketOptions.StartTls);
                Assert.Contains("PLAIN", smtp.AuthenticationMechanisms);
                await smtp.AuthenticateAsync("submitter", password);
                await smtp.SendAsync(Mail("app@example.com", "kunde@example.test", "Submission mit TLS"));
                await smtp.DisconnectAsync(true);
            }

            await UpstreamAsync("Submission mit TLS");

            // The same client on the internal port may sign in in clear.
            using (var smtp = new SmtpClient { Timeout = 10000 })
            {
                await smtp.ConnectAsync("127.0.0.1", _app.SmtpPort, SecureSocketOptions.None);
                Assert.Contains("PLAIN", smtp.AuthenticationMechanisms);
                await smtp.AuthenticateAsync("submitter", password);
                await smtp.DisconnectAsync(true);
            }

            // "From everywhere" does not reach the submission port.
            await _app.WithScopeAsync(sp =>
            {
                sp.GetRequiredService<ConfigStore>().Update(c => c.Smtp.AllowPlainAuth = true);
                return Task.CompletedTask;
            });
            await AssertAuthRefusedInClearAsync(_app.SubmissionPort, "submitter", password);
        }
        finally
        {
            await _app.WithScopeAsync(sp =>
            {
                sp.GetRequiredService<ConfigStore>().Update(c => c.Smtp.AllowPlainAuth = false);
                return Task.CompletedTask;
            });
            await client.DeleteAsync("/api/v1/relay-users/submitter?managedBy=mattiverse");
        }
    }

    /// <summary>A plain connection to <paramref name="port"/>: EHLO offers no AUTH, and an AUTH sent anyway is answered 538.</summary>
    private static async Task AssertAuthRefusedInClearAsync(int port, string user, string password)
    {
        using var tcp = new System.Net.Sockets.TcpClient();
        await tcp.ConnectAsync("127.0.0.1", port);
        using var stream = tcp.GetStream();
        using var reader = new StreamReader(stream, Encoding.ASCII);
        using var writer = new StreamWriter(stream, Encoding.ASCII) { NewLine = "\r\n", AutoFlush = true };
        Assert.StartsWith("220", await reader.ReadLineAsync());
        await writer.WriteLineAsync("EHLO test");
        string? line;
        while ((line = await reader.ReadLineAsync()) is not null && line.StartsWith("250-", StringComparison.Ordinal))
        {
            Assert.DoesNotContain("AUTH", line);
        }

        Assert.DoesNotContain("AUTH", line);
        await writer.WriteLineAsync("AUTH PLAIN " + Convert.ToBase64String(Encoding.UTF8.GetBytes("\0" + user + "\0" + password)));
        Assert.StartsWith("538", await reader.ReadLineAsync());
        await writer.WriteLineAsync("QUIT");
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

/// <summary>Without a key the management API is off (503), the platform endpoints are closed (401), and no relay user can be made.</summary>
[Collection(IntegrationCollection.Name)]
public sealed class ManagementApiWithoutKeyTests(NoKeyFixture app) : IClassFixture<NoKeyFixture>
{
    [Fact]
    public async Task Without_a_key_every_call_is_503_with_an_error()
    {
        using var client = app.ApiClient("anything");
        foreach (var url in new[] { "/api/v1/status", "/api/v1/relay-users", "/api/v1/settings" })
        {
            var response = await client.GetAsync(url);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
            Assert.Equal(JsonValueKind.String, body.GetProperty("error").ValueKind);
        }

        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.PutAsync("/api/v1/relay-users/x1", JsonContent.Create(new { allowedSenders = new[] { "*" } }))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/state")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/health")).StatusCode);
    }
}
