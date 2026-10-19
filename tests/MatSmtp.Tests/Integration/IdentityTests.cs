using System.Net;
using System.Security.Claims;
using System.Text.RegularExpressions;
using MatPlatform;
using MatSmtp.Core.Security;
using MatSmtp.Data;
using MatSmtp.Data.Entities;
using MatSmtp.Services.Auth;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing.Handlers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Net.Http.Headers;

namespace MatSmtp.Tests.Integration;

/// <summary>A headless installation: <c>MATSMTP_ADMIN_PASSWORD</c> makes the administrator <c>admin</c>, the setup page is skipped.</summary>
[Collection(IntegrationCollection.Name)]
public sealed class SeededAdminTests(SeededFixture app) : IClassFixture<SeededFixture>
{
    [Fact]
    public async Task The_administrator_admin_signs_in_with_the_password_of_the_environment_and_there_is_no_setup()
    {
        using var client = app.NewClient();
        var home = await client.GetAsync("/");
        Assert.Equal(HttpStatusCode.Redirect, home.StatusCode);
        Assert.Equal("/Account/Login", AppFixture.LocationPath(home));

        await AppFixture.LoginAsync(client, "admin", SeededFixture.Password);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/Admin/Users")).StatusCode);

        var admin = await app.WithScopeAsync(sp => sp.GetRequiredService<MsDbContext>().User.AsNoTracking().SingleAsync(u => u.Role == UserRole.Admin));
        Assert.Equal("admin", admin.Email);

        using var api = app.ApiClient();
        Assert.Contains("\"setupCompleted\":true", await api.GetStringAsync("/api/v1/settings"));
    }

    [Fact]
    public async Task A_short_password_or_an_existing_administrator_seeds_nothing()
    {
        await app.WithScopeAsync(async sp =>
        {
            var users = sp.GetRequiredService<UserService>();
            Assert.Null(await users.SeedAdminAsync("short", "other"));
            Assert.Null(await users.SeedAdminAsync("admin", "other"));
            Assert.Null(await users.SeedAdminAsync("Long-Enough-2026", "other"));
            Assert.Equal(1, await sp.GetRequiredService<MsDbContext>().User.CountAsync(u => u.Role == UserRole.Admin));
        });
    }
}

/// <summary>
/// The sign-in through MatIdentity (a MatIdentity that does not need to run): the automatic redirect, the local way in, a new person, the role claim, and
/// the pending link: an account whose address matches is linked only after its own password (and its second factor).
/// </summary>
[Collection(IntegrationCollection.Name)]
public sealed class IdentityTests(IdentityFixture app) : IClassFixture<IdentityFixture>
{
    private const string LinkTarget = "/Account/Login?local=1&link=1";

    [Fact]
    public async Task A_visitor_goes_straight_to_matidentity_and_the_local_form_stays_at_local_1()
    {
        await app.EnsureAdminAsync();
        using var client = app.NewClient();

        var login = await client.GetAsync("/Account/Login");
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        Assert.StartsWith(IdentityFixture.IdentityAuthority + "/connect/authorize", login.Headers.Location!.ToString());

        var local = await AppFixture.TextAsync(client, "/Account/Login?local=1");
        Assert.Contains("id=\"login-form\"", local);
        Assert.Contains("Mit MatIdentity anmelden", local);

        Assert.Contains("Du bist abgemeldet", await AppFixture.TextAsync(client, "/Account/Login?signedOut=1"));
        Assert.Contains("id=\"login-sso-error\"", await AppFixture.TextAsync(client, "/Account/Login?ssoError=1"));

        // The platform key of MAT_API_KEY, and the identity in the definition.
        using var api = app.ApiClient(IdentityFixture.PlatformApiKey);
        Assert.Equal(HttpStatusCode.OK, (await api.GetAsync("/api/state")).StatusCode);
        Assert.Contains(IdentityFixture.IdentityAuthority, await api.GetStringAsync("/api"));
    }

    [Fact]
    public async Task A_new_person_is_created_with_the_role_of_the_claim_and_comes_back_into_the_same_account()
    {
        await app.EnsureAdminAsync();
        var browser = new Browser(app);
        Assert.Equal("/", await browser.SignInThroughIdentityAsync(Person("sub-new", "new@example.test", "admin")));
        Assert.Equal(HttpStatusCode.OK, (await browser.Client.GetAsync("/Admin/Users")).StatusCode);

        var user = await FindBySubjectAsync("sub-new");
        Assert.Equal("new@example.test", user!.Email);
        Assert.Equal(UserRole.Admin, user.Role);
        Assert.Null(user.PasswordHash);

        var session = await app.WithScopeAsync(sp => sp.GetRequiredService<MsDbContext>().UserSession.AsNoTracking().OrderByDescending(s => s.Id).FirstAsync(s => s.UserId == user.Id));
        Assert.Equal("sub-new.id.token", session.IdToken);

        // The role follows the claim at the next sign-in; the account stays the same.
        Assert.Equal("/", await new Browser(app).SignInThroughIdentityAsync(Person("sub-new", "new@example.test", "user")));
        Assert.Equal(UserRole.User, (await FindBySubjectAsync("sub-new"))!.Role);
        Assert.Equal(1, await app.WithScopeAsync(sp => sp.GetRequiredService<MsDbContext>().User.CountAsync(u => u.ExternalSubject == "sub-new")));
    }

    [Fact]
    public async Task A_pre_claimed_address_is_linked_only_after_the_password_of_that_account()
    {
        await app.EnsureAdminAsync();
        var mallory = await app.CreateUserAsync("victim@example.test", "Mallory-Passwort-2026", UserRole.User);
        var count = await CountUsersAsync();
        var browser = new Browser(app);

        Assert.Equal(LinkTarget, await browser.SignInThroughIdentityAsync(Person("sub-victim", "victim@example.test", "admin")));
        Assert.Null((await FindAsync(mallory.Id)).ExternalSubject);
        Assert.Equal(count, await CountUsersAsync());
        Assert.Equal(HttpStatusCode.Redirect, (await browser.Client.GetAsync("/Messages")).StatusCode);

        var page = await AppFixture.TextAsync(browser.Client, LinkTarget);
        Assert.Contains("id=\"login-link\"", page);
        Assert.Contains("victim@example.test", page);

        // Another account signing in links nothing, and the pending link is gone.
        await app.CreateUserAsync("other@example.test", "Other-Passwort-2026", UserRole.User);
        Assert.Equal(HttpStatusCode.Redirect, (await browser.SignInLocallyAsync("other@example.test", "Other-Passwort-2026")).StatusCode);
        Assert.Null((await FindAsync(mallory.Id)).ExternalSubject);
        Assert.Null(await FindBySubjectAsync("sub-victim"));
        Assert.False(browser.HasPendingLink);
    }

    [Fact]
    public async Task The_local_sign_in_of_the_matched_account_links_it_also_after_its_second_factor()
    {
        await app.EnsureAdminAsync();
        var vera = await app.CreateUserAsync("vera@example.test", "Vera-Passwort-2026", UserRole.User);
        var browser = new Browser(app);
        Assert.Equal(LinkTarget, await browser.SignInThroughIdentityAsync(Person("sub-vera", "vera@example.test", "admin")));

        Assert.Equal(HttpStatusCode.Redirect, (await browser.SignInLocallyAsync("vera@example.test", "Vera-Passwort-2026")).StatusCode);
        var linked = await FindAsync(vera.Id);
        Assert.Equal("sub-vera", linked.ExternalSubject);
        Assert.Equal(UserRole.Admin, linked.Role);
        Assert.False(browser.HasPendingLink);
        Assert.Equal("/", await new Browser(app).SignInThroughIdentityAsync(Person("sub-vera", "vera@example.test", null)));

        // With an authenticator app the link waits for the code.
        var tom = await app.CreateUserAsync("tom@example.test", "Tom-Passwort-2026", UserRole.User);
        var secret = await app.WithScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<MsDbContext>();
            var user = await db.User.Include(u => u.Totp).SingleAsync(u => u.Id == tom.Id);
            var service = sp.GetRequiredService<TwoFactorService>();
            var enrollment = await service.BeginEnrollmentAsync(user);
            await service.ConfirmEnrollmentAsync(user, Totp.Code(enrollment.Secret, Totp.StepOf(DateTimeOffset.UtcNow)));
            return enrollment.Secret;
        });
        var second = new Browser(app);
        Assert.Equal(LinkTarget, await second.SignInThroughIdentityAsync(Person("sub-tom", "tom@example.test", null)));
        var step = await second.SignInLocallyAsync("tom@example.test", "Tom-Passwort-2026");
        Assert.Equal("/Account/TwoFactor", AppFixture.LocationPath(step));
        Assert.Null((await FindAsync(tom.Id)).ExternalSubject);
        Assert.True(second.HasPendingLink);

        var code = Totp.Code(secret, Totp.StepOf(DateTimeOffset.UtcNow) + 1);
        var done = await AppFixture.PostFormAsync(second.Client, "/Account/TwoFactor", new Dictionary<string, string> { ["Code"] = code });
        Assert.Equal(HttpStatusCode.Redirect, done.StatusCode);
        Assert.Equal("sub-tom", (await FindAsync(tom.Id)).ExternalSubject);
    }

    [Fact]
    public async Task Cancel_deletes_the_pending_link()
    {
        await app.EnsureAdminAsync();
        var clara = await app.CreateUserAsync("clara@example.test", "Clara-Passwort-2026", UserRole.User);
        var browser = new Browser(app);
        Assert.Equal(LinkTarget, await browser.SignInThroughIdentityAsync(Person("sub-clara", "clara@example.test", null)));

        var cancel = await AppFixture.PostFormAsync(browser.Client, LinkTarget, new Dictionary<string, string>(), "/Account/Login?handler=CancelLink");
        Assert.Equal(HttpStatusCode.Redirect, cancel.StatusCode);
        Assert.False(browser.HasPendingLink);
        Assert.Contains("id=\"login-link-expired\"", await AppFixture.TextAsync(browser.Client, LinkTarget));

        await browser.SignInLocallyAsync("clara@example.test", "Clara-Passwort-2026");
        Assert.Null((await FindAsync(clara.Id)).ExternalSubject);
    }

    private static MatIdentityPerson Person(string subject, string email, string? role)
        => new(subject, email, subject, null, role, subject + ".id.token", new ClaimsPrincipal());

    private Task<User> FindAsync(long id) => app.WithScopeAsync(sp => sp.GetRequiredService<MsDbContext>().User.AsNoTracking().SingleAsync(u => u.Id == id));

    private Task<User?> FindBySubjectAsync(string subject)
        => app.WithScopeAsync(sp => sp.GetRequiredService<MsDbContext>().User.AsNoTracking().FirstOrDefaultAsync(u => u.ExternalSubject == subject));

    private Task<int> CountUsersAsync() => app.WithScopeAsync(sp => sp.GetRequiredService<MsDbContext>().User.CountAsync());

    /// <summary>A browser with its own cookies; the callback of MatIdentity runs in the app's services and its cookies are handed over.</summary>
    private sealed class Browser
    {
        private static readonly Uri Base = new("http://localhost");
        private readonly IdentityFixture _site;
        private readonly System.Net.CookieContainer _cookies = new();

        public Browser(IdentityFixture site)
        {
            _site = site;
            Client = site.CreateDefaultClient(Base, new CookieContainerHandler(_cookies));
        }

        public HttpClient Client { get; }

        public bool HasPendingLink => _cookies.GetCookies(Base)[MatIdentityPendingLink.CookieName] is { Expired: false } cookie && cookie.Value.Length > 0;

        /// <summary>What <c>/signin-oidc</c> does after MatIdentity signed the person in; the cookies it sets go into this browser.</summary>
        public async Task<string> SignInThroughIdentityAsync(MatIdentityPerson person)
        {
            using var scope = _site.Services.CreateScope();
            var http = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
            http.Request.Scheme = "http";
            http.Request.Host = new HostString("localhost");
            var accessor = scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>();
            accessor.HttpContext = http;
            try
            {
                var target = await MatIdentitySignIn.CompleteSignInAsync(http, person);
                foreach (var cookie in SetCookieHeaderValue.ParseList(http.Response.Headers.SetCookie.Select(x => x ?? string.Empty).ToList()))
                {
                    _cookies.Add(Base, new System.Net.Cookie(cookie.Name.Value!, cookie.Value.Value, "/"));
                }

                return target;
            }
            finally
            {
                accessor.HttpContext = null;
            }
        }

        public async Task<HttpResponseMessage> SignInLocallyAsync(string login, string password)
            => await AppFixture.PostFormAsync(Client, "/Account/Login?local=1", new Dictionary<string, string> { ["Input.Email"] = login, ["Input.Password"] = password });
    }
}
