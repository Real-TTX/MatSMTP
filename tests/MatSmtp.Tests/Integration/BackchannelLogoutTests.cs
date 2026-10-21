using System.Net;
using System.Security.Claims;
using MatPlatform;
using MatSmtp.Data;
using MatSmtp.Data.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace MatSmtp.Tests.Integration;

/// <summary>
/// The back-channel logout of MatIdentity (MatPlatform 1.4.0, OpenID Connect Back-Channel Logout 1.0): MatIdentity posts a signed logout token to
/// <c>/signout-backchannel</c>; MatSMTP revokes the sessions it names, and their next request lands on the sign-in page.
/// </summary>
[Collection(IntegrationCollection.Name)]
public sealed class BackchannelLogoutTests(IdentityFixture app) : IClassFixture<IdentityFixture>
{
    private const string Path = "/signout-backchannel";

    [Fact]
    public async Task A_valid_token_ends_the_session_it_names_and_its_next_request_is_signed_out()
    {
        await app.EnsureAdminAsync();
        var first = await SignedInAsync("bc-sid", "sid-1");
        var second = await SignedInAsync("bc-sid", "sid-2");

        var response = await PostAsync(Token("bc-sid", "sid-1"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
        AssertSignedOut(await first.Client.GetAsync("/Messages"));
        // Another session of the same person (another sid) stays.
        Assert.Equal(HttpStatusCode.OK, (await second.Client.GetAsync("/Messages")).StatusCode);

        var user = await FindBySubjectAsync("bc-sid");
        var log = await app.WithScopeAsync(sp => sp.GetRequiredService<MsDbContext>().ActivityLog.AsNoTracking()
            .Where(l => l.Action == "Abmeldung über MatIdentity" && l.EntityId == user.Id).ToListAsync());
        Assert.Contains(log, l => l.Summary!.Contains("sid-1", StringComparison.Ordinal) && l.UserId == user.Id);
    }

    [Fact]
    public async Task A_token_with_the_subject_alone_ends_every_session_of_the_person()
    {
        await app.EnsureAdminAsync();
        var first = await SignedInAsync("bc-all", "sid-a");
        var second = await SignedInAsync("bc-all", "sid-b");
        var other = await SignedInAsync("bc-other", "sid-c");

        Assert.Equal(HttpStatusCode.OK, (await PostAsync(Token("bc-all", sessionId: null))).StatusCode);

        AssertSignedOut(await first.Client.GetAsync("/Messages"));
        AssertSignedOut(await second.Client.GetAsync("/Messages"));
        Assert.Equal(HttpStatusCode.OK, (await other.Client.GetAsync("/Messages")).StatusCode);
    }

    [Fact]
    public async Task An_unknown_subject_or_session_is_no_error_and_ends_nothing()
    {
        await app.EnsureAdminAsync();
        var browser = await SignedInAsync("bc-known", "sid-known");

        Assert.Equal(HttpStatusCode.OK, (await PostAsync(Token("bc-nobody", "sid-known"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(Token("bc-known", "sid-nobody"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(Token(subject: null, "sid-nobody"))).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await browser.Client.GetAsync("/Messages")).StatusCode);
    }

    [Fact]
    public async Task A_token_with_another_key_is_400_and_ends_nothing()
    {
        await app.EnsureAdminAsync();
        var browser = await SignedInAsync("bc-forged", "sid-forged");
        var forger = new SigningCredentials(new RsaSecurityKey(System.Security.Cryptography.RSA.Create(2048)) { KeyId = "test-key" }, SecurityAlgorithms.RsaSha256);

        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(Token("bc-forged", "sid-forged", forger))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await browser.Client.GetAsync("/Messages")).StatusCode);
    }

    [Fact]
    public async Task The_sid_of_a_pending_link_goes_with_the_session_of_the_linked_account()
    {
        await app.EnsureAdminAsync();
        var account = await app.CreateUserAsync("bc-link@example.test", "Link-Passwort-2026", UserRole.User);
        var browser = new IdentityTests.Browser(app);

        Assert.Equal("/Account/Login?local=1&link=1", await browser.SignInThroughIdentityAsync(Person("bc-link", "bc-link@example.test", "sid-link")));
        Assert.Equal(HttpStatusCode.Redirect, (await browser.SignInLocallyAsync("bc-link@example.test", "Link-Passwort-2026")).StatusCode);

        var session = await app.WithScopeAsync(sp => sp.GetRequiredService<MsDbContext>().UserSession.AsNoTracking()
            .OrderByDescending(s => s.Id).FirstAsync(s => s.UserId == account.Id));
        Assert.Equal("sid-link", session.IdentitySessionId);

        Assert.Equal(HttpStatusCode.OK, (await PostAsync(Token("bc-link", "sid-link"))).StatusCode);
        AssertSignedOut(await browser.Client.GetAsync("/Messages"));
    }

    [Fact]
    public async Task The_path_is_mapped_for_post_only()
    {
        await app.EnsureAdminAsync();

        Assert.True(IsMapped(app.Services));
    }

    /// <summary>Whether <c>POST /signout-backchannel</c> is an endpoint of the app.</summary>
    internal static bool IsMapped(IServiceProvider services)
        => services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Any(e => e.RoutePattern.RawText == Path && e.Metadata.GetMetadata<HttpMethodMetadata>() is { } methods && methods.HttpMethods.SequenceEqual(["POST"]));

    private async Task<IdentityTests.Browser> SignedInAsync(string subject, string sessionId)
    {
        var browser = new IdentityTests.Browser(app);
        Assert.Equal("/", await browser.SignInThroughIdentityAsync(Person(subject, subject + "@example.test", sessionId)));
        Assert.Equal(HttpStatusCode.OK, (await browser.Client.GetAsync("/Messages")).StatusCode);
        return browser;
    }

    private static void AssertSignedOut(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/Account/Login", AppFixture.LocationPath(response));
    }

    private static MatIdentityPerson Person(string subject, string email, string sessionId)
        => new(subject, email, subject, null, "user", subject + ".id.token", new ClaimsPrincipal()) { SessionId = sessionId };

    private Task<User> FindBySubjectAsync(string subject)
        => app.WithScopeAsync(sp => sp.GetRequiredService<MsDbContext>().User.AsNoTracking().SingleAsync(u => u.ExternalSubject == subject));

    private async Task<HttpResponseMessage> PostAsync(string token)
    {
        using var client = app.NewClient();
        return await client.PostAsync(Path, new FormUrlEncodedContent(new Dictionary<string, string> { ["logout_token"] = token }));
    }

    /// <summary>A logout token as MatIdentity writes it (<c>typ</c> <c>logout+jwt</c>, signed with the fixture's key).</summary>
    internal static string Token(string? subject, string? sessionId, SigningCredentials? credentials = null)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var claims = new Dictionary<string, object>
        {
            ["iss"] = IdentityFixture.IdentityAuthority,
            ["aud"] = "matsmtp",
            ["iat"] = now,
            ["nbf"] = now,
            ["exp"] = now + 120,
            ["jti"] = Guid.NewGuid().ToString("N"),
            ["events"] = new Dictionary<string, object> { [MatIdentityLogoutToken.EventType] = new Dictionary<string, object>() },
        };
        if (subject is not null)
        {
            claims["sub"] = subject;
        }

        if (sessionId is not null)
        {
            claims["sid"] = sessionId;
        }

        return new JsonWebTokenHandler { SetDefaultTimesOnTokenCreation = false }.CreateToken(new SecurityTokenDescriptor
        {
            Claims = claims,
            TokenType = MatIdentityLogoutToken.TokenType,
            SigningCredentials = credentials ?? AppFixture.IdentityKey,
        });
    }
}

/// <summary>Without <c>MATIDENTITY_AUTHORITY</c> there is no back-channel logout.</summary>
[Collection(IntegrationCollection.Name)]
public sealed class BackchannelLogoutWithoutIdentityTests(AppFixture app) : IClassFixture<AppFixture>
{
    [Fact]
    public async Task Without_matidentity_the_path_is_404()
    {
        await app.EnsureAdminAsync();
        using var client = app.NewClient();
        var body = new Dictionary<string, string> { ["logout_token"] = BackchannelLogoutTests.Token("bc-none", "sid-none") };

        // Not mapped at all; anonymous it is a path like any other behind the sign-in (the token is never read).
        Assert.False(BackchannelLogoutTests.IsMapped(app.Services));
        var anonymous = await client.PostAsync("/signout-backchannel", new FormUrlEncodedContent(body));
        Assert.Equal(HttpStatusCode.Redirect, anonymous.StatusCode);
        Assert.Equal("/Account/Login", AppFixture.LocationPath(anonymous));

        // Signed in it does not exist: 404 (a POST to a path that does not exist ends in the error page, which wants an anti-forgery token: 400).
        await app.CreateUserAsync("bc-none@example.test", "None-Passwort-2026", UserRole.User);
        await AppFixture.LoginAsync(client, "bc-none@example.test", "None-Passwort-2026");
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/signout-backchannel")).StatusCode);
    }
}
