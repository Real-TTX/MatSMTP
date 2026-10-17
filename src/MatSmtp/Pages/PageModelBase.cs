using MatSmtp.UiKit;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MatSmtp.Pages;

/// <summary>What every page of the application needs: the title and the breadcrumb for the layout, and the messages that survive a redirect.</summary>
public abstract class PageModelBase : PageModel
{
    protected void Head(string title, string? sub = null, params (string Text, string? Url)[] crumbs)
    {
        ViewData["Title"] = title;
        ViewData["PageSub"] = sub;
        ViewData["Crumbs"] = Crumbs.Of(crumbs.Length == 0 ? [(title, null)] : crumbs);
    }

    /// <summary>A message after a save: the page redirects and the layout shows it as a toast.</summary>
    protected void Flash(string message) => TempData["Flash"] = message;

    protected void FlashError(string message) => TempData["Error"] = message;

    /// <summary>A local address only: a "return to" that comes from the outside must never send the user to another site.</summary>
    protected string SafeReturn(string? returnUrl, string fallback = "/")
        => !string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl) ? returnUrl : fallback;
}
