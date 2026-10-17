using System.Net;
using MatSmtp.Core.Security;
using MatSmtp.Data;
using MatSmtp.Data.Entities;
using MatSmtp.Services.Auth;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MatSmtp.Tests.Integration;

[Collection(IntegrationCollection.Name)]
public sealed class SignInTests : IClassFixture<AppFixture>
{
    private readonly AppFixture _app;

    public SignInTests(AppFixture app) => _app = app;

    [Fact]
    public async Task A_wrong_password_is_refused_and_the_right_one_signs_in()
    {
        await _app.EnsureAdminAsync();
        await _app.CreateUserAsync("login@example.test", "Richtig-Richtig-1", UserRole.User);
        using var client = _app.NewClient();

        var wrong = await AppFixture.PostFormAsync(client, "/Account/Login", new Dictionary<string, string> { ["Input.Email"] = "login@example.test", ["Input.Password"] = "falsch" });
        Assert.Equal(HttpStatusCode.OK, wrong.StatusCode);
        Assert.DoesNotContain("Richtig-Richtig-1", await AppFixture.TextAsync(wrong));
        Assert.Equal(HttpStatusCode.Redirect, (await client.GetAsync("/Messages")).StatusCode);

        await AppFixture.LoginAsync(client, "login@example.test", "Richtig-Richtig-1");
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/Messages")).StatusCode);

        // a member of the staff has no administration
        var admin = await client.GetAsync("/Admin/Users");
        Assert.NotEqual(HttpStatusCode.OK, admin.StatusCode);
    }

    [Fact]
    public async Task Too_many_wrong_passwords_lock_the_account_for_a_while()
    {
        await _app.EnsureAdminAsync();
        await _app.CreateUserAsync("lock@example.test", "Richtig-Richtig-2", UserRole.User);
        using var client = _app.NewClient();

        for (var i = 0; i < 6; i++)
        {
            await AppFixture.PostFormAsync(client, "/Account/Login", new Dictionary<string, string> { ["Input.Email"] = "lock@example.test", ["Input.Password"] = "falsch" + i });
        }

        // even the right password is refused during the lock-out
        var locked = await AppFixture.PostFormAsync(client, "/Account/Login", new Dictionary<string, string> { ["Input.Email"] = "lock@example.test", ["Input.Password"] = "Richtig-Richtig-2" });
        Assert.Equal(HttpStatusCode.OK, locked.StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await client.GetAsync("/Messages")).StatusCode);
    }

    [Fact]
    public async Task A_missing_page_says_so_in_words()
    {
        await _app.EnsureAdminAsync();
        await _app.CreateUserAsync("missing@example.test", "Richtig-Richtig-3", UserRole.User);
        using var client = _app.NewClient();
        await AppFixture.LoginAsync(client, "missing@example.test", "Richtig-Richtig-3");

        var response = await client.GetAsync("/Messages/Details?id=987654");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("Das gibt es nicht", await AppFixture.TextAsync(response));
    }

    [Fact]
    public async Task With_the_second_factor_a_password_alone_is_not_enough_and_a_recovery_code_works_once()
    {
        await _app.EnsureAdminAsync();
        var user = await _app.CreateUserAsync("totp@example.test", "Totp-Passwort-2026", UserRole.User);
        IReadOnlyList<string> recovery = [];
        string secret = "";
        await _app.WithScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<MsDbContext>();
            var twoFactor = sp.GetRequiredService<TwoFactorService>();
            var tracked = await db.User.Include(u => u.Totp).FirstAsync(u => u.Id == user.Id);
            secret = (await twoFactor.BeginEnrollmentAsync(tracked)).Secret;
            recovery = (await twoFactor.ConfirmEnrollmentAsync(tracked, Totp.Code(secret, Totp.StepOf(DateTimeOffset.UtcNow))))!;
        });
        Assert.NotEmpty(recovery);

        // the password opens the second page, not the application
        using var client = _app.NewClient();
        var first = await AppFixture.PostFormAsync(client, "/Account/Login", new Dictionary<string, string> { ["Input.Email"] = "totp@example.test", ["Input.Password"] = "Totp-Passwort-2026" });
        Assert.Equal(HttpStatusCode.Redirect, first.StatusCode);
        Assert.Equal("/Account/TwoFactor", AppFixture.LocationPath(first));
        Assert.Equal(HttpStatusCode.Redirect, (await client.GetAsync("/Messages")).StatusCode);

        // a wrong code is refused, a recovery code signs in
        var wrong = await AppFixture.PostFormAsync(client, "/Account/TwoFactor", new Dictionary<string, string> { ["Code"] = "000000" });
        Assert.Equal(HttpStatusCode.OK, wrong.StatusCode);
        var ok = await AppFixture.PostFormAsync(client, "/Account/TwoFactor", new Dictionary<string, string> { ["Code"] = recovery[0] });
        Assert.Equal(HttpStatusCode.Redirect, ok.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/Messages")).StatusCode);

        // the same recovery code does not work a second time
        using var again = _app.NewClient();
        await AppFixture.PostFormAsync(again, "/Account/Login", new Dictionary<string, string> { ["Input.Email"] = "totp@example.test", ["Input.Password"] = "Totp-Passwort-2026" });
        var reused = await AppFixture.PostFormAsync(again, "/Account/TwoFactor", new Dictionary<string, string> { ["Code"] = recovery[0] });
        Assert.Equal(HttpStatusCode.OK, reused.StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await again.GetAsync("/Messages")).StatusCode);
    }

    
}
