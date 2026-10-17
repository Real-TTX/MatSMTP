using System.Globalization;
using System.Text.RegularExpressions;

namespace MatSmtp.UiKit;

/// <summary>A badge at the right end of a menu item: a count, or a dot for "something needs a look" (empty text).</summary>
public sealed record NavBadge(string Text, string Tone = "accent", string? Label = null)
{
    private static readonly string[] Severity = ["error", "warn", "info", "accent", "ok", "plain"];

    public bool IsDot => Text.Length == 0;

    public static NavBadge Count(int number, string tone = "accent", string? label = null)
        => new(number > 99 ? "99+" : number.ToString(CultureInfo.InvariantCulture), tone, label);

    public static NavBadge Dot(string tone = "accent", string? label = null) => new(string.Empty, tone, label);

    public string Classes(bool sum = false)
        => "mat-nav__badge" + (Tone == "accent" ? string.Empty : $" mat-nav__badge--{Tone}") + (IsDot ? " mat-nav__badge--dot" : string.Empty) + (sum ? " mat-nav__badge--sum" : string.Empty);

    /// <summary>The badge of a closed parent: the sum of its children's counts, or a dot of the most urgent tone.</summary>
    public static NavBadge? Sum(IEnumerable<NavBadge> badges)
    {
        var list = badges.ToList();
        if (list.Count == 0)
        {
            return null;
        }

        var tone = Severity.First(t => list.Any(b => b.Tone == t));
        var counts = list.Where(b => !b.IsDot && int.TryParse(b.Text.TrimEnd('+'), NumberStyles.None, CultureInfo.InvariantCulture, out _)).ToList();
        var label = string.Join(", ", list.Select(b => b.Label).Where(l => l is not null));
        return counts.Count == list.Count
            ? Count(counts.Sum(b => int.Parse(b.Text.TrimEnd('+'), CultureInfo.InvariantCulture)), tone, label)
            : Dot(tone, label);
    }
}

/// <summary>
/// An item of the menu. With <see cref="Children"/> it is the parent of a submenu: a button that opens its children, one level deep. <see cref="Primary"/> is the
/// main action of the app (SHELL-19): the first item, filled with the accent.
/// </summary>
public sealed record NavItem(string Title, string? Href, string? Icon, NavBadge? Badge = null, IReadOnlyList<NavItem>? Children = null, bool Primary = false)
{
    public bool IsParent => Children is { Count: > 0 };

    public string Key => Regex.Replace(Title.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');

    public bool Contains(string? href) => href is not null && (Href == href || (Children?.Any(c => c.Href == href) ?? false));

    public NavBadge? SumOfChildren => IsParent && Badge is null ? NavBadge.Sum(Children!.Where(c => c.Badge is not null).Select(c => c.Badge!)) : null;
}

public sealed record NavGroup(string? Title, IReadOnlyList<NavItem> Items);

/// <summary>One menu of the app. The administration is a menu of its own; the switcher under the logo shows one at a time.</summary>
public sealed record NavSet(string Id, string Title, string Icon, IReadOnlyList<NavGroup> Groups)
{
    public IEnumerable<NavItem> AllItems => Groups.SelectMany(g => g.Items).SelectMany(i => i.IsParent ? i.Children!.Prepend(i) : [i]);

    public IEnumerable<NavItem> Pages => AllItems.Where(i => !i.IsParent && i.Href is not null);

    public bool Contains(string? href) => href is not null && Pages.Any(i => i.Href == href);

    public bool NeedsAttention => AllItems.Any(i => i.Badge is { Tone: "warn" or "error" });
}

public static class Navigation
{
    /// <summary>The address of the item that belongs to <paramref name="path"/>: the longest address that is the path or a parent of it.</summary>
    public static string? ActiveHref(IEnumerable<NavSet> menus, string path)
    {
        var normalized = path.Length > 1 ? path.TrimEnd('/') : path;
        return menus.SelectMany(m => m.Pages)
            .Select(i => i.Href!)
            .Where(href => href == "/" ? normalized == "/" : normalized == href || normalized.StartsWith(href + "/", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(href => href.Length)
            .FirstOrDefault();
    }
}
