using System.Net;

namespace MatSmtp.Tests.Integration;

/// <summary>
/// MAT_FRAME_ANCESTORS (MatDesign docs/PLATFORM.md "Shown inside another app"): Mattiverse may show MatSMTP in its window when the variable names it;
/// without it only MatSMTP itself may frame its pages.
/// </summary>
[Collection(IntegrationCollection.Name)]
public sealed class FrameAncestorsTests(AppFixture plain, FramedFixture framed) : IClassFixture<AppFixture>, IClassFixture<FramedFixture>
{
    [Fact]
    public async Task Without_the_variable_only_matsmtp_may_frame_its_pages()
    {
        using var client = plain.NewClient();
        var response = await client.GetAsync("/Account/Login");

        Assert.Equal("SAMEORIGIN", response.Headers.GetValues("X-Frame-Options").Single());
        Assert.EndsWith("; frame-ancestors 'self'", response.Headers.GetValues("Content-Security-Policy").Single());
    }

    [Theory]
    [InlineData("/Account/Login")]
    [InlineData("/api/health")]
    [InlineData("/")]
    public async Task With_it_Mattiverse_may_frame_every_answer(string path)
    {
        using var client = framed.NewClient();
        var response = await client.GetAsync(path);

        Assert.False(response.Headers.Contains("X-Frame-Options"));
        var csp = response.Headers.GetValues("Content-Security-Policy").Single();
        Assert.StartsWith("default-src 'self'; script-src 'self';", csp);
        Assert.EndsWith("; form-action 'self'; frame-ancestors 'self' http://localhost:8030", csp);
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
    }
}
