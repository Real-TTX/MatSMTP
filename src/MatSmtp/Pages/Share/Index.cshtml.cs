using MatSmtp.Data.Entities;
using MatSmtp.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MatSmtp.Pages.Share;

/// <summary>The page behind a share link: the delivery of one message, for somebody without an account (the role "Anonym"). Never the content.</summary>
[AllowAnonymous]
public sealed class IndexModel(ShareService shares, TimeProvider clock) : PageModelBase
{
    [BindProperty(SupportsGet = true)]
    public Guid Token { get; set; }

    public ShareState State { get; private set; }

    public ShareLink? Link { get; private set; }

    public Message? Item => Link?.Message;

    public string RemainingText => Link is null ? "" : ShareService.Remaining(Link, clock.GetUtcNow().UtcDateTime);

    public async Task<IActionResult> OnGetAsync()
    {
        var resolution = await shares.ResolveAsync(Token, HttpContext.RequestAborted);
        State = resolution.State;
        Link = resolution.Link;
        ViewData["Title"] = "Zustellung einer Nachricht";
        ViewData["Wide"] = true;
        Response.Headers.CacheControl = "private, no-store";
        if (State == ShareState.Active && Link is not null)
        {
            await shares.CountViewAsync(Link, HttpContext.RequestAborted);
            return Page();
        }

        Response.StatusCode = State == ShareState.Unknown ? 404 : 410;
        return Page();
    }
}
