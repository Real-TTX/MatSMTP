using System.Net;
using System.Text;
using System.Text.Json;
using MatPlatform;
using MatSmtp.Configuration;
using MatSmtp.Data;
using MatSmtp.Data.Entities;
using MatSmtp.Platform;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MatSmtp.Tests.Integration;

/// <summary>The platform contract (MatDesign docs/PLATFORM.md): a valid definition, the anonymous and the protected endpoints, a secret never sent back.</summary>
[Collection(IntegrationCollection.Name)]
public sealed class PlatformTests(AppFixture app) : IClassFixture<AppFixture>
{
    private static readonly string Sprite = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "MatSmtp", "wwwroot", "ui", "matui-icons.svg");

    private static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

    private static async Task<JsonElement> BodyAsync(HttpResponseMessage response) => JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public void The_definition_is_valid_and_its_icons_are_in_the_sprite_of_matui(string language)
    {
        var definition = MatPlatformBuilder.Build(SmtpPlatform.Describe, language);
        Assert.Empty(MatPlatformValidator.Validate(definition, MatPlatformValidator.ReadSprite(Sprite)));
        Assert.Equal("matsmtp", definition.App.Id);
        Assert.Equal("#f5b800", definition.App.Accent);
        Assert.True(definition.Configure.Count <= 10, "at most ten settings");
        Assert.Equal(["admin", "user"], definition.Roles!.Select(r => r.Key).ToArray());

        // All engines on one copy of the package.
        Assert.Equal("1.4.0", MatPlatformInfo.Version);
    }

    [Fact]
    public async Task The_definition_and_the_health_answer_without_a_key()
    {
        await app.EnsureAdminAsync();
        using var anonymous = app.ApiClient(null);

        var definition = await anonymous.GetAsync("/api");
        Assert.Equal(HttpStatusCode.OK, definition.StatusCode);
        var body = await BodyAsync(definition);
        Assert.Equal(1, body.GetProperty("contract").GetInt32());
        Assert.Equal("matsmtp", body.GetProperty("app").GetProperty("id").GetString());
        Assert.True(body.GetProperty("auth").GetProperty("apiKey").GetBoolean());

        var health = await anonymous.GetAsync("/api/health");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        var checks = (await BodyAsync(health)).GetProperty("checks").EnumerateArray().Select(c => c.GetProperty("name").GetString()).ToList();
        Assert.Equal(["database", "smtp", "accounts"], checks);

        foreach (var url in new[] { "/api/state", "/api/state/queued", "/api/configure" })
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(url)).StatusCode);
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsync("/api/actions/flushDeferred", null)).StatusCode);
    }

    [Fact]
    public async Task The_engine_key_reads_every_tile_and_runs_the_actions()
    {
        await app.EnsureAdminAsync();
        using var client = app.ApiClient();

        var state = await BodyAsync(await client.GetAsync("/api/state"));
        var values = state.GetProperty("values");
        foreach (var key in new[] { "queued", "deferred", "sent24h", "failed24h", "received24h", "lastDelivery", "accounts", "accountsHealth", "webhooksFailing" })
        {
            Assert.True(values.TryGetProperty(key, out _), key);
        }

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/state/queued")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/state/nothing")).StatusCode);

        var flushed = await client.PostAsync("/api/actions/flushDeferred", null);
        Assert.Equal(HttpStatusCode.OK, flushed.StatusCode);
        Assert.True((await BodyAsync(flushed)).GetProperty("ok").GetBoolean());
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/api/actions/retryFailed", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync("/api/actions/open", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync("/api/actions/nothing", null)).StatusCode);
    }

    [Fact]
    public async Task The_settings_make_the_default_account_and_never_send_its_password_back()
    {
        await app.EnsureAdminAsync();
        await app.WithScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<MsDbContext>();
            await db.SmtpAccount.IgnoreQueryFilters().ExecuteUpdateAsync(s => s.SetProperty(a => a.IsDefault, false));
        });
        using var client = app.ApiClient();

        var saved = await client.PutAsync("/api/configure", Json("""
            {"values": {"relayHost": "smtp.provider.test", "relayPort": 587, "relaySecurity": "StartTls", "relayUsername": "relay@example.com",
                        "relayPassword": "Provider-Secret-1", "relayFrom": "relay@example.com", "defaultSenderDomain": "example.com"}}
            """));
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var text = await saved.Content.ReadAsStringAsync();
        Assert.DoesNotContain("Provider-Secret-1", text);
        var values = JsonDocument.Parse(text).RootElement.GetProperty("values");
        Assert.True(values.GetProperty("relayPassword").GetProperty("isSet").GetBoolean());
        Assert.Equal("smtp.provider.test", values.GetProperty("relayHost").GetString());
        Assert.Equal("StartTls", values.GetProperty("relaySecurity").GetString());

        var account = await app.WithScopeAsync(sp => sp.GetRequiredService<MsDbContext>().SmtpAccount.AsNoTracking().SingleAsync(a => a.IsDefault));
        Assert.Equal("smtp.provider.test", account.Host);
        Assert.Equal(SmtpSecurity.StartTls, account.Security);
        Assert.DoesNotContain("Provider-Secret-1", account.PasswordProtected);
        Assert.Equal("example.com", app.Services.GetRequiredService<ConfigStore>().Current.Smtp.DefaultSenderDomain);
        Assert.DoesNotContain("Provider-Secret-1", await client.GetStringAsync("/api/configure"));

        // A wrong value saves nothing.
        var wrong = await client.PutAsync("/api/configure", Json("""{"values": {"trustedNetworks": "not a network", "hostname": "relay.example.com"}}"""));
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        Assert.True((await BodyAsync(wrong)).GetProperty("errors").TryGetProperty("trustedNetworks", out _));
        Assert.NotEqual("relay.example.com", app.Services.GetRequiredService<ConfigStore>().Current.Smtp.Hostname);

        await app.WithScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<MsDbContext>();
            await db.SmtpAccount.Where(a => a.Id == account.Id).ExecuteUpdateAsync(s => s.SetProperty(a => a.IsDefault, false));
            sp.GetRequiredService<ConfigStore>().Update(c => c.Smtp.DefaultSenderDomain = "");
        });
    }
}
