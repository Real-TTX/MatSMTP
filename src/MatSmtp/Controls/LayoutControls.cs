using System.Net;
using System.Text;
using MatSmtp.UiKit;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Razor.TagHelpers;

namespace MatSmtp.Controls;

/// <summary>
/// The buttons under a form, always in one row from positive to negative: Save, Back (and what the page adds), a space, Delete.
/// The order is enforced by the toolkit's CSS, not by the author.
/// </summary>
[HtmlTargetElement("ms-buttons")]
public sealed class ButtonsTagHelper : TagHelper
{
    public string SaveText { get; set; } = "Speichern";

    public string SaveIcon { get; set; } = "save";

    public bool NoSave { get; set; }

    public string? BackHref { get; set; }

    public string BackText { get; set; } = "Zurück";

    /// <summary>The address of the delete handler: the button posts the same form there, after a confirmation dialog.</summary>
    public string? DeleteUrl { get; set; }

    public string? DeleteConfirm { get; set; }

    public string DeleteText { get; set; } = "Löschen";

    public override async Task ProcessAsync(TagHelperContext context, TagHelperOutput output)
    {
        var extra = (await output.GetChildContentAsync()).GetContent();
        var sb = new StringBuilder("<div class=\"mat-btn-row\">");
        if (!NoSave)
        {
            sb.Append("<button type=\"submit\" class=\"mat-btn mat-btn--primary\">").Append(Ui.Icon(SaveIcon).ToHtml()).Append("<span>").Append(WebUtility.HtmlEncode(SaveText)).Append("</span></button>");
        }

        if (BackHref is not null)
        {
            sb.Append("<a class=\"mat-btn mat-btn--secondary\" href=\"").Append(WebUtility.HtmlEncode(BackHref)).Append("\">").Append(Ui.Icon("back").ToHtml()).Append("<span>").Append(WebUtility.HtmlEncode(BackText)).Append("</span></a>");
        }

        sb.Append(extra);
        if (DeleteUrl is not null)
        {
            sb.Append("<span class=\"mat-btn-row__spacer\"></span>");
            sb.Append("<button type=\"submit\" formnovalidate formaction=\"").Append(WebUtility.HtmlEncode(DeleteUrl)).Append("\" class=\"mat-btn mat-btn--danger\"")
              .Append(" data-confirm=\"").Append(WebUtility.HtmlEncode(DeleteConfirm ?? "Wirklich löschen? Das kann nicht rückgängig gemacht werden.")).Append("\" data-confirm-ok=\"").Append(WebUtility.HtmlEncode(DeleteText)).Append("\">")
              .Append(Ui.Icon("trash").ToHtml()).Append("<span>").Append(WebUtility.HtmlEncode(DeleteText)).Append("</span></button>");
        }

        sb.Append("</div>");
        output.TagName = null;
        output.Content.SetHtmlContent(sb.ToString());
    }
}

/// <summary>A card: an optional head with an icon and a title, and the content.</summary>
[HtmlTargetElement("ms-card")]
public sealed class CardTagHelper : TagHelper
{
    public string? Title { get; set; }

    public string? Icon { get; set; }

    public string? Sub { get; set; }

    /// <summary>Extra classes for the body (a stack of children, for example).</summary>
    public string? BodyClass { get; set; }

    public override async Task ProcessAsync(TagHelperContext context, TagHelperOutput output)
    {
        var body = (await output.GetChildContentAsync()).GetContent();
        var sb = new StringBuilder();
        sb.Append("<div class=\"mat-card");
        if (output.Attributes.TryGetAttribute("class", out var cls)) { sb.Append(' ').Append(WebUtility.HtmlEncode(cls.Value?.ToString())); }
        sb.Append("\">");
        if (!string.IsNullOrEmpty(Title))
        {
            sb.Append("<div class=\"mat-card__head\">");
            if (!string.IsNullOrEmpty(Icon)) { sb.Append(Ui.Icon(Icon).ToHtml()); }
            sb.Append("<h2 class=\"mat-card__title\">").Append(WebUtility.HtmlEncode(Title)).Append("</h2>");
            if (!string.IsNullOrEmpty(Sub)) { sb.Append("<span class=\"mat-card__sub\">").Append(WebUtility.HtmlEncode(Sub)).Append("</span>"); }
            sb.Append("</div>");
        }

        sb.Append("<div class=\"mat-card__body").Append(string.IsNullOrEmpty(BodyClass) ? "" : " " + BodyClass).Append("\">").Append(body).Append("</div></div>");
        output.TagName = null;
        output.Attributes.Clear();
        output.Content.SetHtmlContent(sb.ToString());
    }
}

/// <summary>A tab group: several panels, one visible. Several groups can share a page: every id is derived from <see cref="Id"/>.</summary>
[HtmlTargetElement("ms-tabs")]
public sealed class TabsTagHelper : TagHelper
{
    public sealed record TabInfo(string Id, string Title, string? Icon, int? Count, string Content);

    public required string Id { get; set; }

    public string? Label { get; set; }

    /// <summary>The id of the tab that is open; the first one when empty or unknown.</summary>
    public string? Selected { get; set; }

    public override async Task ProcessAsync(TagHelperContext context, TagHelperOutput output)
    {
        var tabs = new List<TabInfo>();
        context.Items[typeof(TabsTagHelper)] = tabs;
        await output.GetChildContentAsync();

        var selected = tabs.Any(t => t.Id == Selected) ? Selected : tabs.FirstOrDefault()?.Id;
        var sb = new StringBuilder();
        sb.Append("<div data-tabs id=\"").Append(Id).Append("\"><div class=\"mat-tabs\" role=\"tablist\" aria-label=\"").Append(WebUtility.HtmlEncode(Label ?? "Bereiche")).Append("\">");
        foreach (var t in tabs)
        {
            var isSelected = t.Id == selected;
            sb.Append("<button type=\"button\" class=\"mat-tab\" role=\"tab\" id=\"").Append(Id).Append("-tab-").Append(t.Id).Append("\" aria-controls=\"").Append(Id).Append("-panel-").Append(t.Id)
              .Append("\" aria-selected=\"").Append(isSelected ? "true" : "false").Append('"').Append(isSelected ? "" : " tabindex=\"-1\"").Append('>');
            if (t.Icon is not null) { sb.Append(Ui.Icon(t.Icon).ToHtml()); }
            sb.Append("<span>").Append(WebUtility.HtmlEncode(t.Title)).Append("</span>");
            if (t.Count is { } count) { sb.Append(" <span class=\"mat-badge mat-badge--soft\">").Append(count).Append("</span>"); }
            sb.Append("</button>");
        }

        sb.Append("</div>");
        foreach (var t in tabs)
        {
            sb.Append("<div class=\"mat-tabpanel\" role=\"tabpanel\" id=\"").Append(Id).Append("-panel-").Append(t.Id).Append("\" aria-labelledby=\"").Append(Id).Append("-tab-").Append(t.Id).Append('"')
              .Append(t.Id == selected ? "" : " hidden").Append('>').Append(t.Content).Append("</div>");
        }

        sb.Append("</div>");
        output.TagName = null;
        output.Content.SetHtmlContent(sb.ToString());
    }
}

[HtmlTargetElement("ms-tab", ParentTag = "ms-tabs")]
public sealed class TabTagHelper : TagHelper
{
    public required string Id { get; set; }

    public required string Title { get; set; }

    public string? Icon { get; set; }

    public int? Count { get; set; }

    public override async Task ProcessAsync(TagHelperContext context, TagHelperOutput output)
    {
        var content = (await output.GetChildContentAsync()).GetContent();
        ((List<TabsTagHelper.TabInfo>)context.Items[typeof(TabsTagHelper)]).Add(new TabsTagHelper.TabInfo(Id, Title, Icon, Count, content));
        output.SuppressOutput();
    }
}

/// <summary>The list of what is wrong with a form, at its top, each a link to its field. The toolkit's script fills it for client-side checks; here it also shows the server's.</summary>
[HtmlTargetElement("ms-summary")]
public sealed class SummaryTagHelper : TagHelper
{
    [HtmlAttributeNotBound, ViewContext]
    public ViewContext ViewContext { get; set; } = null!;

    public string Title { get; set; } = "Bitte prüfe das Formular.";

    public override void Process(TagHelperContext context, TagHelperOutput output)
    {
        var errors = ViewContext.ViewData.ModelState
            .Where(e => e.Value?.Errors.Count > 0)
            .SelectMany(e => e.Value!.Errors.Select(err => (Key: e.Key, err.ErrorMessage)))
            .ToList();

        var sb = new StringBuilder();
        sb.Append("<div class=\"mat-alert mat-alert--danger\" role=\"alert\" data-validation-summary").Append(errors.Count == 0 ? " hidden" : "").Append('>')
          .Append(Ui.Icon("x-circle").ToHtml()).Append("<div class=\"mat-alert__body\"><strong>").Append(WebUtility.HtmlEncode(Title)).Append("</strong><ul class=\"mat-alert__list\">");
        foreach (var (key, message) in errors)
        {
            var target = string.IsNullOrEmpty(key) ? "" : "#" + TagBuilder.CreateSanitizedId(key, "_");
            sb.Append("<li>").Append(target.Length == 0 ? WebUtility.HtmlEncode(message) : $"<a href=\"{target}\">{WebUtility.HtmlEncode(message)}</a>").Append("</li>");
        }

        sb.Append("</ul></div></div>");
        output.TagName = null;
        output.Content.SetHtmlContent(sb.ToString());
    }
}
