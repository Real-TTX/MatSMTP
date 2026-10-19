using System.Net;
using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Html;

namespace MatSmtp.UiKit;

/// <summary>The version of this build: "1.0.42-20261007", "nightly-42-20261007" or "local-20261007" (see Directory.Build.props).</summary>
public static class AppVersion
{
    public static string Current { get; } = Read();

    private static string Read()
    {
        var informational = typeof(AppVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        return string.IsNullOrWhiteSpace(informational) ? "local" : informational.Split('+')[0];
    }
}

/// <summary>Small helpers for the markup of the toolkit: icons, the logo, status pills, numbers, cells.</summary>
public static partial class Ui
{
    [GeneratedRegex("^[a-z0-9-]+$")]
    private static partial Regex NameRegex();

    /// <summary>An icon from the page's inline sprite. The name must exist in <c>matui-icons.svg</c> or in the app's own sprite.</summary>
    public static IHtmlContent Icon(string name, string? extraClass = null)
    {
        if (!NameRegex().IsMatch(name))
        {
            throw new ArgumentException($"'{name}' is not an icon name.", nameof(name));
        }

        var cls = string.IsNullOrEmpty(extraClass) ? "mat-icon" : $"mat-icon {extraClass}";
        return new HtmlString($"<svg class=\"{cls}\" aria-hidden=\"true\" focusable=\"false\"><use href=\"#mat-i-{name}\"></use></svg>");
    }

    /// <summary>The logo tile of MatSMTP: the envelope with the arrow on the yellow of the app.</summary>
    public static IHtmlContent Logo(string size = "md")
        => new HtmlString(
            $"<span class=\"mat-logo mat-logo--{size}\" role=\"img\" aria-label=\"MatSMTP\">" +
            "<svg class=\"mat-icon\" aria-hidden=\"true\" focusable=\"false\"><use href=\"#mat-i-ms-logo\"></use></svg></span>");

    /// <summary>The first letters of a name, for an avatar.</summary>
    public static string Initials(string name) => Services.Fmt.Initials(name);

    /// <summary>A status as a pill with a word and, optionally, an icon; <paramref name="tone"/> is ok, warn, error, info, busy, accent, plain or empty.</summary>
    public static IHtmlContent Pill(string text, string tone = "", string? icon = null)
        => new HtmlString($"<span class=\"mat-pill{(string.IsNullOrEmpty(tone) ? "" : " mat-pill--" + tone)}\">{(icon is null ? "" : Icon(icon).ToHtml())}{WebUtility.HtmlEncode(text)}</span>");

    /// <summary>
    /// A number on a card (the facts at the top of a page). <paramref name="note"/> says what it means; with <paramref name="bad"/> the note is a warning.
    /// A card with an address is a link. The icon names the fact for the eye; it stands quietly in the label, not as a tile.
    /// </summary>
    public static IHtmlContent Kpi(string label, string value, string? note, string icon, string tone = "accent", string? href = null, bool bad = false)
    {
        var tag = href is null ? "div" : "a";
        var link = href is null ? "" : $" href=\"{WebUtility.HtmlEncode(href)}\"";
        var noteHtml = string.IsNullOrWhiteSpace(note) ? "" : $"<span class=\"mat-stat__delta{(bad ? " mat-stat__delta--down" : "")}\">{WebUtility.HtmlEncode(note)}</span>";
        return new HtmlString(
            $"<{tag} class=\"mat-card ms-stat{(href is null ? "" : " ms-stat--link")}\"{link}><div class=\"mat-card__body mat-stat\">" +
            $"<span class=\"mat-stat__label\">{Icon(icon).ToHtml()}{WebUtility.HtmlEncode(label)}</span><span class=\"mat-stat__value\">{WebUtility.HtmlEncode(value)}</span>{noteHtml}</div></{tag}>");
    }

    /// <summary>Encoded text for a table cell; empty for null.</summary>
    public static IHtmlContent Text(string? text) => new HtmlString(WebUtility.HtmlEncode(text ?? string.Empty));

    /// <summary>A quiet dash for "nothing here".</summary>
    public static IHtmlContent None() => new HtmlString("<span class=\"mat-muted\">–</span>");

    /// <summary>A link in a table cell.</summary>
    public static IHtmlContent Link(string text, string href)
        => new HtmlString($"<a href=\"{WebUtility.HtmlEncode(href)}\">{WebUtility.HtmlEncode(text)}</a>");

    /// <summary>A name with a second, quieter line below it.</summary>
    public static IHtmlContent NameWithSub(string text, string? href, string? sub)
    {
        var name = href is null ? WebUtility.HtmlEncode(text) : $"<a href=\"{WebUtility.HtmlEncode(href)}\">{WebUtility.HtmlEncode(text)}</a>";
        var small = string.IsNullOrWhiteSpace(sub) ? "" : $"<small class=\"mat-muted ms-sub\">{WebUtility.HtmlEncode(sub)}</small>";
        return new HtmlString($"<div class=\"mat-table__name ms-name\"><span>{name}</span>{small}</div>");
    }

    public static IHtmlContent Mono(string? text) => new HtmlString($"<span class=\"mat-mono\">{WebUtility.HtmlEncode(text ?? string.Empty)}</span>");


    /// <summary>Several pieces of markup after each other (a status pill and a second line, for example).</summary>
    public static IHtmlContent Concat(params IHtmlContent[] parts) => new HtmlString(string.Concat(parts.Select(p => p.ToHtml())));

    /// <summary>The round avatar with the initials of a person; the name is the tooltip and the label.</summary>
    public static IHtmlContent Avatar(string? name, string size = "")
    {
        var cls = string.IsNullOrEmpty(size) ? "mat-avatar" : $"mat-avatar mat-avatar--{size}";
        var shown = string.IsNullOrWhiteSpace(name) ? "Niemand" : name;
        return new HtmlString($"<span class=\"{cls}\" role=\"img\" aria-label=\"{WebUtility.HtmlEncode(shown)}\" data-tip=\"{WebUtility.HtmlEncode(shown)}\">{WebUtility.HtmlEncode(Services.Fmt.Initials(name))}</span>");
    }

    /// <summary>A person with the avatar, for a table cell.</summary>
    public static IHtmlContent Person(string? name)
        => string.IsNullOrWhiteSpace(name)
            ? None()
            : new HtmlString($"<span class=\"ms-person\">{Avatar(name, "sm").ToHtml()}<span>{WebUtility.HtmlEncode(name)}</span></span>");

    private static readonly Dictionary<string, string> Singulars = new(StringComparer.Ordinal)
    {
        ["Einträge"] = "Eintrag", ["Pläne"] = "Plan", ["Ziele"] = "Ziel", ["Links"] = "Link", ["Freigaben"] = "Freigabe", ["Nachrichten"] = "Nachricht",
        ["Regeln"] = "Regel", ["Konten"] = "Konto", ["Zugänge"] = "Zugang", ["Webhooks"] = "Webhook", ["Versuche"] = "Versuch", ["Benutzer"] = "Benutzer", ["Empfänger"] = "Empfänger",
    };

    /// <summary>The name of the things in a list for a count: "1 Nachricht", "3 Nachrichten". Names that are not known keep their plural.</summary>
    public static string CountNoun(long count, string plural) => $"{Services.Fmt.Integer(count)} {(count == 1 && Singulars.TryGetValue(plural, out var one) ? one : plural)}";

    /// <summary>A phrase with a count in German: "1 Plan aktiv", "3 Pläne aktiv".</summary>
    public static string Plural(int count, string one, string many) => $"{count} {(count == 1 ? one : many)}";

    /// <summary>The markup of a content as text (a cell that is made of other cells).</summary>
    public static string ToHtml(this IHtmlContent content)
    {
        using var writer = new StringWriter();
        content.WriteTo(writer, System.Text.Encodings.Web.HtmlEncoder.Default);
        return writer.ToString();
    }
}
