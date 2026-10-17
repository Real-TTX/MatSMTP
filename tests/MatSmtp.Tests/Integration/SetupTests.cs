using System.Net;

namespace MatSmtp.Tests.Integration;

[Collection(IntegrationCollection.Name)]
public sealed class SetupTests : IClassFixture<AppFixture>
{
    private readonly AppFixture _app;

    public SetupTests(AppFixture app) => _app = app;

    [Fact]
    public async Task The_first_start_leads_to_setup_then_to_a_signed_in_administrator_and_back_out()
    {
        using var client = _app.NewClient();

        // no administrator yet: the only page is the one that makes the first one
        var first = await client.GetAsync("/Messages");
        Assert.Equal(HttpStatusCode.Redirect, first.StatusCode);
        Assert.Equal("/Account/Setup", AppFixture.LocationPath(first));

        var setup = await AppFixture.PostFormAsync(client, "/Account/Setup", new Dictionary<string, string>
        {
            ["Input.DisplayName"] = "Miriam Test", ["Input.Email"] = "Miriam@Example.Test", ["Input.Password"] = "Projekte-2026-Test", ["Input.PasswordRepeat"] = "Projekte-2026-Test",
        });
        Assert.Equal(HttpStatusCode.Redirect, setup.StatusCode);

        // the setup signs the new administrator in
        var todos = await client.GetAsync("/Messages");
        Assert.Equal(HttpStatusCode.OK, todos.StatusCode);
        Assert.Contains("Alles, was MatSMTP empfangen hat", await AppFixture.TextAsync(todos));

        // and the setup page is gone for good
        var again = await client.GetAsync("/Account/Setup");
        Assert.NotEqual(HttpStatusCode.OK, again.StatusCode);

        // sign out: the session is ended, the page asks for the sign-in
        var out1 = await AppFixture.PostFormAsync(client, "/Messages", new Dictionary<string, string>(), "/Account/Logout");
        Assert.Equal(HttpStatusCode.Redirect, out1.StatusCode);
        var afterLogout = await client.GetAsync("/Messages");
        Assert.Equal(HttpStatusCode.Redirect, afterLogout.StatusCode);
        Assert.Equal("/Account/Login", AppFixture.LocationPath(afterLogout));
    }
}
