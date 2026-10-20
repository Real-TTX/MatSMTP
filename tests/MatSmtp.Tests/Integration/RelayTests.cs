using System.Collections.Concurrent;
using MailKit.Net.Smtp;
using MailKit.Security;
using MatSmtp.Configuration;
using MatSmtp.Core.Relay;
using MatSmtp.Core.Security;
using MatSmtp.Data;
using MatSmtp.Data.Entities;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MimeKit;

namespace MatSmtp.Tests.Integration;

/// <summary>
/// The relay end to end: a real SMTP client talks to the receiver of the application, the rules decide, the spooler sends through an account to a fake upstream
/// server (or a webhook takes it). Nothing is mocked between the socket and the upstream server.
/// </summary>
[Collection(IntegrationCollection.Name)]
public sealed class RelayTests : IClassFixture<AppFixture>, IAsyncLifetime
{
    private readonly AppFixture _app;
    private readonly FakeUpstream _upstream = new();

    public RelayTests(AppFixture app) => _app = app;

    public async Task InitializeAsync()
    {
        await _app.EnsureAdminAsync();
        await _app.WithScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<MsDbContext>();
            await db.Rule.IgnoreQueryFilters().ExecuteDeleteAsync();
            await db.SmtpAccount.IgnoreQueryFilters().ExecuteUpdateAsync(s => s.SetProperty(a => a.IsDefault, false));
            db.SmtpAccount.Add(new SmtpAccount { Name = "Upstream", Host = "127.0.0.1", Port = _upstream.Port, Security = SmtpSecurity.None, IsDefault = true });
            await db.SaveChangesAsync();
            sp.GetRequiredService<ConfigStore>().Update(c =>
            {
                c.Smtp.TrustedNetworks = "127.0.0.0/8, ::1/128";
                c.Delivery.Paused = false;
                c.Delivery.RetryMinutes = "1";
                c.Delivery.MaxAttempts = 3;
            });
        });
        await WaitForListenerAsync();
    }

    public async Task DisposeAsync() => await _upstream.DisposeAsync();

    private async Task WaitForListenerAsync()
    {
        var listener = _app.Services.GetRequiredService<Services.Relay.SmtpListener>();
        for (var i = 0; i < 100 && !listener.State.Running; i++)
        {
            await Task.Delay(50);
        }

        Assert.True(listener.State.Running, listener.State.Error);
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

    private async Task SendAsync(MimeMessage message, SecureSocketOptions security = SecureSocketOptions.None, (string User, string Password)? auth = null, int? port = null)
    {
        using var client = new SmtpClient { ServerCertificateValidationCallback = (_, _, _, _) => true, Timeout = 10000 };
        await client.ConnectAsync("127.0.0.1", port ?? _app.SmtpPort, security);
        if (auth is { } a)
        {
            await client.AuthenticateAsync(a.User, a.Password);
        }

        await client.SendAsync(message);
        await client.DisconnectAsync(true);
    }

    private Task<Message> MessageAsync(string subject, Func<Message, bool> done)
        => _app.EventuallyAsync(sp => sp.GetRequiredService<MsDbContext>().Message.AsNoTracking().Include(m => m.Events).Include(m => m.WebhookDeliveries).FirstOrDefaultAsync(m => m.Subject == subject)!,
            m => m is not null && done(m))!;

    private async Task<UpstreamMail> UpstreamAsync(string subject)
    {
        for (var i = 0; i < 200; i++)
        {
            var hit = _upstream.Received.FirstOrDefault(r => r.Message.Subject == subject);
            if (hit is not null)
            {
                return hit;
            }

            await Task.Delay(50);
        }

        throw new Xunit.Sdk.XunitException($"The upstream server never got \"{subject}\".");
    }

    private Task AddRuleAsync(Rule rule) => _app.WithScopeAsync(async sp =>
    {
        var db = sp.GetRequiredService<MsDbContext>();
        rule.Position = (await db.Rule.MaxAsync(r => (int?)r.Position) ?? 0) + 10;
        db.Rule.Add(rule);
        await db.SaveChangesAsync();
    });

    [Fact]
    public async Task A_message_from_a_trusted_network_goes_out_through_the_default_account()
    {
        await SendAsync(Mail("app@app.local", "kunde@example.test", "Einfach durch"));

        var message = await MessageAsync("Einfach durch", m => m.Status == MessageStatus.Sent);
        Assert.Equal(MessageStatus.Sent, message.Status);
        Assert.Equal("Upstream", message.AccountName);
        Assert.Equal("127.0.0.1", message.ClientIp);
        Assert.Contains(message.Events, e => e.Level == EventLevel.Success);

        var got = await UpstreamAsync("Einfach durch");
        Assert.Equal("app@app.local", got.From);
        Assert.Equal(["kunde@example.test"], got.To);
        Assert.Contains("MatSMTP", got.Message.Headers[HeaderId.Received]);
    }

    [Fact]
    public async Task A_rule_rewrites_the_sender_and_keeps_the_old_one_for_answers()
    {
        await AddRuleAsync(new Rule { Name = "Umschreiben", MatchSenders = "app.local", Action = RuleAction.Continue, RewriteFrom = "noreply@firma.test", RewriteFromName = "Firma", ReplyToOriginal = true });

        await SendAsync(Mail("shop@app.local", "kunde@example.test", "Umgeschrieben"));

        var got = await UpstreamAsync("Umgeschrieben");
        Assert.Equal("noreply@firma.test", got.From);
        var from = got.Message.From.Mailboxes.Single();
        Assert.Equal("noreply@firma.test", from.Address);
        Assert.Equal("Firma", from.Name);
        Assert.Equal("shop@app.local", got.Message.ReplyTo.Mailboxes.Single().Address);
        Assert.Equal("shop@app.local", got.Message.Headers["X-MatSMTP-Original-From"]);

        var message = await MessageAsync("Umgeschrieben", m => m.Status == MessageStatus.Sent);
        Assert.Equal("shop@app.local", message.OriginalFrom);
        Assert.Equal("Umschreiben", message.Rules);
    }

    [Fact]
    public async Task A_message_is_signed_with_the_dkim_key_of_its_sender_domain_after_the_rewrite()
    {
        var (pem, publicKey) = MatSmtp.Core.Relay.Dkim.Generate(1024);
        await _app.WithScopeAsync(sp => sp.GetRequiredService<Services.Relay.DkimService>().SaveAsync(null, k =>
        {
            k.Domain = "signiert.test";
            k.Selector = "s1";
            k.IncludeSubdomains = true;
            k.IsEnabled = true;
        }, (pem, publicKey, 1024)));
        await AddRuleAsync(new Rule { Name = "Auf signiert", MatchSenders = "dkim-app.local", Action = RuleAction.Continue, RewriteFrom = "noreply@news.signiert.test" });

        var mail = Mail("app@dkim-app.local", "kunde@example.test", "Mit Signatur");
        mail.Body = new TextPart("plain") { Text = "Grüße mit Umlauten, damit der Inhalt 8 Bit hat." };
        await SendAsync(mail);
        await SendAsync(Mail("app@ohne-schluessel.local", "kunde@example.test", "Ohne Signatur"));

        var got = await UpstreamAsync("Mit Signatur");
        var signature = got.Message.Headers.Single(h => h.Id == HeaderId.DkimSignature);
        Assert.Contains("d=signiert.test", signature.Value);
        Assert.Contains("s=s1", signature.Value);
        var verifier = new MimeKit.Cryptography.DkimVerifier(new FixedKeyLocator(MatSmtp.Core.Relay.Dkim.RecordValue(publicKey)));
        Assert.True(await verifier.VerifyAsync(got.Message, signature), "The signature does not verify with the public key of the record.");

        var message = await MessageAsync("Mit Signatur", m => m.Status == MessageStatus.Sent);
        Assert.Contains(message.Events, e => e.Text.Contains("DKIM-signiert für signiert.test", StringComparison.Ordinal));
        Assert.DoesNotContain((await UpstreamAsync("Ohne Signatur")).Message.Headers, h => h.Id == HeaderId.DkimSignature);
    }

    /// <summary>The public key as the DNS would have it, without asking a DNS server.</summary>
    private sealed class FixedKeyLocator(string record) : MimeKit.Cryptography.DkimPublicKeyLocatorBase
    {
        public override Org.BouncyCastle.Crypto.AsymmetricKeyParameter LocatePublicKey(string methods, string domain, string selector, CancellationToken cancellationToken = default)
            => GetPublicKey(record);

        public override Task<Org.BouncyCastle.Crypto.AsymmetricKeyParameter> LocatePublicKeyAsync(string methods, string domain, string selector, CancellationToken cancellationToken = default)
            => Task.FromResult(GetPublicKey(record));
    }

    [Fact]
    public async Task A_rule_sends_by_recipient_through_another_account()
    {
        await using var second = new FakeUpstream("relay", "geheim-2026");
        var accountId = await _app.WithScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<MsDbContext>();
            var account = new SmtpAccount
            {
                Name = "Intern", Host = "127.0.0.1", Port = second.Port, Security = SmtpSecurity.None, UserName = "relay",
                PasswordProtected = sp.GetRequiredService<SecretProtector>().Protect("geheim-2026"),
            };
            db.SmtpAccount.Add(account);
            await db.SaveChangesAsync();
            return account.Id;
        });
        await AddRuleAsync(new Rule { Name = "Intern", MatchRecipients = "*@intern.test", Action = RuleAction.Relay, AccountId = accountId, AddBcc = "archiv@firma.test" });

        await SendAsync(Mail("app@app.local", "kollege@intern.test", "Nach innen"));

        UpstreamMail? got = null;
        for (var i = 0; i < 200 && got is null; i++)
        {
            got = second.Received.FirstOrDefault(r => r.Message.Subject == "Nach innen");
            await Task.Delay(50);
        }

        Assert.NotNull(got);
        Assert.Equal("relay", got.User);
        Assert.Equal(["kollege@intern.test", "archiv@firma.test"], got.To);
        Assert.DoesNotContain(_upstream.Received, r => r.Message.Subject == "Nach innen");
        var message = await MessageAsync("Nach innen", m => m.Status == MessageStatus.Sent);
        Assert.Equal("Intern", message.AccountName);
    }

    [Fact]
    public async Task A_rule_rejects_while_the_application_is_connected()
    {
        await AddRuleAsync(new Rule { Name = "Kein Spam", MatchSubject = "Gewinnspiel", Action = RuleAction.Reject, RejectMessage = "Bitte nicht." });

        var error = await Assert.ThrowsAsync<SmtpCommandException>(() => SendAsync(Mail("app@app.local", "kunde@example.test", "Großes Gewinnspiel")));

        Assert.Equal(550, (int)error.StatusCode);
        Assert.Contains("Bitte nicht.", error.Message);
        var message = await MessageAsync("Großes Gewinnspiel", m => m.Status == MessageStatus.Rejected);
        Assert.Null(message.StorageKey);
    }

    [Fact]
    public async Task Outside_the_trusted_networks_a_relay_user_has_to_sign_in()
    {
        await _app.WithScopeAsync(async sp =>
        {
            sp.GetRequiredService<ConfigStore>().Update(c => c.Smtp.TrustedNetworks = "");
            var db = sp.GetRequiredService<MsDbContext>();
            db.RelayUser.Add(new RelayUser { UserName = "shop", PasswordHash = PasswordHasher.Hash("Shop-Passwort-2026"), AllowedSenders = "shop.test" });
            await db.SaveChangesAsync();
        });
        try
        {
            var refused = await Assert.ThrowsAsync<MailKit.ServiceNotAuthenticatedException>(() => SendAsync(Mail("app@shop.test", "kunde@example.test", "Ohne Anmeldung")));
            Assert.Contains("Bitte zuerst anmelden", refused.Message);

            await Assert.ThrowsAsync<AuthenticationException>(() => SendAsync(Mail("app@shop.test", "kunde@example.test", "Falsches Passwort"), SecureSocketOptions.StartTls, ("shop", "falsch")));

            await SendAsync(Mail("app@shop.test", "kunde@example.test", "Mit Anmeldung"), SecureSocketOptions.StartTls, ("shop", "Shop-Passwort-2026"));
            var message = await MessageAsync("Mit Anmeldung", m => m.Status == MessageStatus.Sent);
            Assert.Equal("shop", message.AuthUser);
            Assert.True(message.IsTls);

            // the relay user may only send as shop.test
            var other = await Assert.ThrowsAsync<SmtpCommandException>(() => SendAsync(Mail("ceo@firma.test", "kunde@example.test", "Fremder Absender"), SecureSocketOptions.StartTls, ("shop", "Shop-Passwort-2026")));
            Assert.Equal(550, (int)other.StatusCode);

            // the TLS port speaks TLS from the first byte
            await SendAsync(Mail("app@shop.test", "kunde@example.test", "Über TLS"), SecureSocketOptions.SslOnConnect, ("shop", "Shop-Passwort-2026"), _app.TlsPort);
            await MessageAsync("Über TLS", m => m.Status == MessageStatus.Sent);
        }
        finally
        {
            await _app.WithScopeAsync(sp =>
            {
                sp.GetRequiredService<ConfigStore>().Update(c => c.Smtp.TrustedNetworks = "127.0.0.0/8, ::1/128");
                return Task.CompletedTask;
            });
        }
    }

    [Fact]
    public async Task A_temporary_error_is_tried_again_and_a_permanent_one_ends_it()
    {
        _upstream.Fail = (451, "4.3.0 Bitte später");
        await SendAsync(Mail("app@app.local", "kunde@example.test", "Später"));
        var deferred = await MessageAsync("Später", m => m.Status == MessageStatus.Deferred);
        Assert.Equal(MessageStatus.Deferred, deferred.Status);
        Assert.Equal(1, deferred.Attempts);
        Assert.NotNull(deferred.NextAttemptDate);
        Assert.Contains("451", deferred.LastError);

        _upstream.Fail = (550, "5.1.1 Gibt es nicht");
        await SendAsync(Mail("app@app.local", "niemand@example.test", "Endgültig"));
        var failed = await MessageAsync("Endgültig", m => m.Status == MessageStatus.Failed);
        Assert.Equal(MessageStatus.Failed, failed.Status);
        Assert.Equal(1, failed.Attempts);

        // sent again by hand once the server takes it
        _upstream.Fail = null;
        await _app.WithScopeAsync(async sp =>
        {
            await sp.GetRequiredService<Services.Relay.DeliveryService>().RetryAsync([deferred.Id, failed.Id], CancellationToken.None);
            sp.GetRequiredService<Services.Relay.SpoolSignal>().Wake();
        });
        await MessageAsync("Später", m => m.Status == MessageStatus.Sent);
        await MessageAsync("Endgültig", m => m.Status == MessageStatus.Sent);
    }

    [Fact]
    public async Task A_message_for_webhooks_only_is_handed_over_as_signed_json()
    {
        var calls = new ConcurrentQueue<(string Body, string? Signature)>();
        var port = AppFixture.FreePort();
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
        await using var receiver = builder.Build();
        receiver.MapPost("/hook", async (HttpRequest request) =>
        {
            using var reader = new StreamReader(request.Body);
            calls.Enqueue((await reader.ReadToEndAsync(), request.Headers[WebhookSignature.Header].ToString()));
            return Results.Ok();
        });
        await receiver.StartAsync();

        var hookId = await _app.WithScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<MsDbContext>();
            var hook = new Webhook { Name = "Tickets", Url = $"http://127.0.0.1:{port}/hook", Payload = WebhookPayload.Content, SecretProtected = sp.GetRequiredService<SecretProtector>().Protect("hook-geheimnis-2026") };
            db.Webhook.Add(hook);
            await db.SaveChangesAsync();
            return hook.Id;
        });
        await AddRuleAsync(new Rule { Name = "Tickets", MatchRecipients = "support@firma.test", Action = RuleAction.WebhookOnly, WebhookId = hookId });

        await SendAsync(Mail("kunde@example.test", "support@firma.test", "Drucker kaputt"));

        var message = await MessageAsync("Drucker kaputt", m => m.Status == MessageStatus.Delivered);
        Assert.Equal(MessageStatus.Delivered, message.Status);
        Assert.Single(message.WebhookDeliveries, d => d.Status == WebhookDeliveryStatus.Done);
        Assert.DoesNotContain(_upstream.Received, r => r.Message.Subject == "Drucker kaputt");

        var (body, signature) = Assert.Single(calls);
        Assert.Contains("\"subject\":\"Drucker kaputt\"", body);
        Assert.Contains("Hallo aus dem Test.", body);
        Assert.True(WebhookSignature.Verify("hook-geheimnis-2026", signature!, body));
        await receiver.StopAsync();
    }
}
