using System.Security.Claims;
using MatSmtp.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace MatSmtp.Services.Auth;

/// <summary>Checks the session of every request against the database (through <see cref="SessionCache"/>) and keeps the claims current.</summary>
public sealed class SessionCookieEvents(SessionCache sessions) : CookieAuthenticationEvents
{
    public override async Task ValidatePrincipal(CookieValidatePrincipalContext context)
    {
        var raw = context.Principal?.FindFirstValue(AppClaims.Session);
        if (!Guid.TryParse(raw, out var token))
        {
            await Reject(context);
            return;
        }

        var db = context.HttpContext.RequestServices.GetRequiredService<MsDbContext>();
        var snapshot = await sessions.GetAsync(token, db, context.HttpContext.RequestAborted);
        if (snapshot is null)
        {
            await Reject(context);
            return;
        }

        // A role or a name changed since the cookie was issued: the claims follow the database.
        var principal = context.Principal!;
        if (principal.FindFirstValue(ClaimTypes.Role) != snapshot.Role.ToString() || principal.Identity?.Name != snapshot.DisplayName)
        {
            context.ReplacePrincipal(SignInService.BuildPrincipal(snapshot, token));
            context.ShouldRenew = true;
        }
    }

    public override Task RedirectToAccessDenied(RedirectContext<CookieAuthenticationOptions> context)
    {
        context.Response.Redirect("/Account/AccessDenied");
        return Task.CompletedTask;
    }

    public override Task RedirectToLogin(RedirectContext<CookieAuthenticationOptions> context)
    {
        if (context.Request.Path.StartsWithSegments("/api"))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        }

        context.Response.Redirect(context.RedirectUri);
        return Task.CompletedTask;
    }

    private static async Task Reject(CookieValidatePrincipalContext context)
    {
        context.RejectPrincipal();
        await context.HttpContext.SignOutAsync(SignInService.CookieScheme);
    }
}
