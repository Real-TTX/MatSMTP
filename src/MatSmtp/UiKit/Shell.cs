using System.Net;
using MatSmtp.Data;
using MatSmtp.Data.Entities;
using MatSmtp.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace MatSmtp.UiKit;

/// <summary>An entry of the user's context menu. A <see cref="Post"/> item changes something (sign out): a button of a small form, not a link.</summary>
public sealed record UserMenuItem(string Title, string Href, string Icon, bool Danger = false, bool SeparatorBefore = false, bool Post = false);

/// <summary>Everything the layout needs to draw the shell around a page: the menus, the user, the bell, the version.</summary>
public sealed class ShellModel
{
    public IReadOnlyList<NavSet> Menus { get; init; } = [];

    public bool HasSwitcher => Menus.Count > 1;

    public NavSet CurrentMenu(string? active) => Menus.FirstOrDefault(m => m.Contains(active)) ?? Menus[0];

    public string UserName { get; init; } = "";

    public string UserRole { get; init; } = "";

    public string UserLine { get; init; } = "";

    public IReadOnlyList<UserMenuItem> UserMenu { get; init; } = [];

    public string Version { get; init; } = "";

    public string Tagline { get; init; } = "SMTP-Relay";

    /// <summary>"light" or "dark" when the user chose one; null follows the system.</summary>
    public string? Theme { get; init; }

    public string HomeHref { get; init; } = "/";

    /// <summary>The optional attributes of &lt;html&gt;, written only when they have a value (an empty data-theme would count as a choice).</summary>
    public string RootAttributes => Theme is "light" or "dark" ? $"data-theme=\"{WebUtility.HtmlEncode(Theme)}\"" : string.Empty;
}

/// <summary>Counts shown as badges in the menu. Cached for 30 seconds: a badge may be a little late, a page must not wait for queries.</summary>
public sealed class NavCounts(IMemoryCache cache, MsDbContext db)
{
    public sealed record Counts(int Queue, int Deferred, int Failed);

    public async Task<Counts> GetAsync()
        => (await cache.GetOrCreateAsync("nav-counts", async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(30);
            return new Counts(
                await db.Message.CountAsync(m => m.Status == MessageStatus.Queued || m.Status == MessageStatus.Sending),
                await db.Message.CountAsync(m => m.Status == MessageStatus.Deferred),
                await db.Message.CountAsync(m => m.Status == MessageStatus.Failed));
        }))!;

    public void Invalidate() => cache.Remove("nav-counts");
}

/// <summary>Builds the shell for the person who is signed in: the configuration of the relay and the settings menu only for administrators.</summary>
public sealed class ShellFactory(ICurrentUser user, NavCounts counts, IHttpContextAccessor accessor)
{
    public async Task<ShellModel> BuildAsync()
    {
        var principal = accessor.HttpContext?.User;
        var theme = principal?.FindFirst(AppClaims.Theme)?.Value;

        var menus = new List<NavSet> { await RelayMenuAsync() };
        if (user.IsAdmin)
        {
            menus.Add(AdminMenu());
        }

        return new ShellModel
        {
            Menus = menus,
            UserName = user.Name ?? "Gast",
            UserRole = user.IsAdmin ? "Administrator" : "Benutzer",
            UserLine = principal?.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value ?? "",
            UserMenu = UserMenu(),
            Version = $"MatSMTP {AppVersion.Current}",
            Theme = theme,
        };
    }

    /// <summary>The shell of a page without a menu (sign-in, setup, a shared link).</summary>
    public static ShellModel Bare(string? theme = null) => new()
    {
        Menus = [new NavSet("none", "MatSMTP", "home", [])],
        Version = $"MatSMTP {AppVersion.Current}",
        Theme = theme,
    };

    private async Task<NavSet> RelayMenuAsync()
    {
        var c = await counts.GetAsync();
        var messageBadge = c.Failed > 0 ? NavBadge.Count(c.Failed, "error", "fehlgeschlagene Nachrichten")
            : c.Deferred > 0 ? NavBadge.Count(c.Deferred, "warn", "verzögerte Nachrichten") : null;
        var queueBadge = c.Queue > 0 ? NavBadge.Count(c.Queue, "info", "Nachrichten in der Warteschlange") : null;
        var groups = new List<NavGroup>
        {
            new(null,
            [
                new("Übersicht", "/", "dashboard"),
                new("Nachrichten", "/Messages", "inbox", messageBadge),
                new("Warteschlange", "/Messages/Queue", "clock", queueBadge),
            ]),
        };
        if (user.IsAdmin)
        {
            groups.Add(new NavGroup("Relay",
            [
                new("Regeln", "/Rules", "route"),
                new("SMTP-Konten", "/Accounts", "server"),
                new("Zugänge", "/RelayUsers", "key"),
                new("Webhooks", "/Webhooks", "plug"),
            ]));
            groups.Add(new NavGroup("Werkzeuge",
            [
                new("Regeln testen", "/RuleTest", "flask"),
                new("Testnachricht", "/Tools/Send", "send"),
            ]));
        }
        else
        {
            groups.Add(new NavGroup("Werkzeuge", [new("Regeln testen", "/RuleTest", "flask")]));
        }

        return new NavSet("relay", "Relay", "ms-logo", groups);
    }

    private static NavSet AdminMenu() => new("admin", "Einstellungen", "settings",
    [
        new("Relay",
        [
            new("Empfang", "/Admin/Smtp", "inbox"),
            new("Zustellung", "/Admin/Delivery", "send"),
        ]),
        new("Organisation",
        [
            new("Benutzer", "/Admin/Users", "user"),
            new("Freigaben", "/Admin/Shares", "share"),
        ]),
        new("System",
        [
            new("Backup", null, "archive", Children:
            [
                new("Übersicht", "/Admin/Backup", null),
                new("Pläne", "/Admin/Backup/Plans", null),
                new("Ziele", "/Admin/Backup/Targets", null),
                new("Wiederherstellung", "/Admin/Backup/Restore", null),
            ]),
            new("Sicherheit", "/Admin/Security", "shield"),
        ]),
        new("Protokolle",
        [
            new("Aktivität", "/Admin/Activity", "activity"),
            new("System", "/Admin/System", "monitor"),
        ]),
    ]);

    private static List<UserMenuItem> UserMenu() =>
    [
        new("Mein Konto", "/Account/Profile", "user"),
        new("Sicherheit", "/Account/Security", "shield"),
        new("Abmelden", "/Account/Logout", "logout", Danger: true, SeparatorBefore: true, Post: true),
    ];
}
