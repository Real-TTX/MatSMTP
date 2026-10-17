using System.Globalization;
using System.Net;
using System.Text;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Razor.TagHelpers;

namespace MatSmtp.Controls;

/// <summary>
/// The shared part of every field of a form: label above, the control, the hint below, the error at the field. A field can depend on another
/// (<c>show-if</c> names the other field, <c>show-value</c> the values that make it appear): it is shown only when it is needed.
/// Attributes that are not the field's own (<c>autocomplete</c>, <c>min</c>, <c>data-*</c> ...) are passed to the control.
/// </summary>
public abstract class FieldTagHelper : TagHelper
{
    [HtmlAttributeNotBound, ViewContext]
    public ViewContext ViewContext { get; set; } = null!;

    [HtmlAttributeName("for")]
    public ModelExpression? For { get; set; }

    /// <summary>The name of the field when there is no model expression.</summary>
    public string? Name { get; set; }

    public string? Label { get; set; }

    public string? Hint { get; set; }

    public string? Id { get; set; }

    /// <summary>The label gets the star and the control the required attribute. Defaults to the [Required] of the model.</summary>
    public bool? Required { get; set; }

    /// <summary>The field spans the whole width of the form grid.</summary>
    public bool Full { get; set; }

    public string? ShowIf { get; set; }

    public string? ShowValue { get; set; }

    public bool Disabled { get; set; }

    public bool Readonly { get; set; }

    protected string FieldName => For?.Name ?? Name ?? throw new InvalidOperationException("A field needs for or name.");

    protected string DomId => Id ?? TagBuilder.CreateSanitizedId(FieldName, "_");

    protected bool IsRequired => Required ?? For?.Metadata.IsRequired == true && For.Metadata.ModelType != typeof(bool);

    protected string? Error
        => ViewContext.ViewData.ModelState.TryGetValue(FieldName, out var entry) && entry.Errors.Count > 0 ? entry.Errors[0].ErrorMessage : null;

    protected string ValueText()
    {
        if (ViewContext.ViewData.ModelState.TryGetValue(FieldName, out ModelStateEntry? entry) && entry.AttemptedValue is not null)
        {
            return entry.AttemptedValue;
        }

        return Format(For?.Model);
    }

    /// <summary>A value as an input shows it: invariant, so that a number input and a date input can read it.</summary>
    public static string Format(object? model) => model switch
    {
        null => string.Empty,
        DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        TimeOnly t => t.ToString("HH:mm", CultureInfo.InvariantCulture),
        DateTime dt => dt.ToString("yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture),
        decimal m => m.ToString("0.############", CultureInfo.InvariantCulture),
        double db => db.ToString("0.############", CultureInfo.InvariantCulture),
        float f => f.ToString("0.############", CultureInfo.InvariantCulture),
        IFormattable fmt => fmt.ToString(null, CultureInfo.InvariantCulture),
        _ => model.ToString() ?? string.Empty,
    };

    /// <summary>The attributes the author wrote that the field did not take: they belong to the control.</summary>
    protected static string PassThrough(TagHelperAttributeList attributes, params string[] except)
    {
        var sb = new StringBuilder();
        foreach (var a in attributes)
        {
            if (except.Contains(a.Name, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            sb.Append(' ').Append(a.Name);
            var value = a.Value?.ToString();
            if (a.ValueStyle != HtmlAttributeValueStyle.Minimized && value is not null)
            {
                sb.Append("=\"").Append(WebUtility.HtmlEncode(value)).Append('"');
            }
        }

        return sb.ToString();
    }

    protected abstract string Control(string id, TagHelperAttributeList attributes);

    public override void Process(TagHelperContext context, TagHelperOutput output)
    {
        var id = DomId;
        var error = Error;
        var label = Label ?? For?.Metadata.DisplayName ?? For?.Metadata.PropertyName ?? FieldName;
        var sb = new StringBuilder();

        sb.Append("<div class=\"mat-field");
        if (Full) { sb.Append(" mat-field--full"); }
        if (error is not null) { sb.Append(" is-invalid"); }
        sb.Append('"');
        if (ShowIf is not null)
        {
            sb.Append(" data-show-if=\"").Append(WebUtility.HtmlEncode(ShowIf)).Append('"');
            if (ShowValue is not null)
            {
                sb.Append(" data-show-value=\"").Append(WebUtility.HtmlEncode(ShowValue)).Append('"');
            }
        }

        sb.Append('>');
        if (LabelFirst)
        {
            sb.Append("<label class=\"mat-label\" for=\"").Append(id).Append("\">").Append(WebUtility.HtmlEncode(label));
            if (IsRequired) { sb.Append(" <span class=\"mat-req\" aria-hidden=\"true\">*</span>"); }
            sb.Append("</label>");
        }

        sb.Append(Control(id, output.Attributes));

        if (!string.IsNullOrWhiteSpace(Hint))
        {
            sb.Append("<span class=\"mat-hint\" id=\"").Append(id).Append("-hint\">").Append(WebUtility.HtmlEncode(Hint)).Append("</span>");
        }

        sb.Append("<span class=\"mat-error\" id=\"").Append(id).Append("-error\" aria-live=\"polite\">").Append(WebUtility.HtmlEncode(error ?? string.Empty)).Append("</span>");
        sb.Append("</div>");

        output.TagName = null;
        output.Attributes.Clear();
        output.Content.SetHtmlContent(sb.ToString());
    }

    /// <summary>A switch carries its label inside the control; every other field has it above.</summary>
    protected virtual bool LabelFirst => true;

    protected string CommonAttributes(string id)
    {
        var sb = new StringBuilder();
        sb.Append(" id=\"").Append(id).Append("\" name=\"").Append(WebUtility.HtmlEncode(FieldName)).Append('"');
        if (IsRequired) { sb.Append(" required"); }
        if (Disabled) { sb.Append(" disabled"); }
        if (Readonly) { sb.Append(" readonly"); }
        if (Error is not null) { sb.Append(" aria-invalid=\"true\""); }
        var describedBy = !string.IsNullOrWhiteSpace(Hint) ? id + "-hint " + id + "-error" : id + "-error";
        sb.Append(" aria-describedby=\"").Append(describedBy).Append('"');
        return sb.ToString();
    }
}

/// <summary>A one-line field: text, e-mail, password, number, date (a picker), tel, url.</summary>
[HtmlTargetElement("ms-input")]
public sealed class InputTagHelper : FieldTagHelper
{
    public string Type { get; set; } = "text";

    /// <summary>The message the toolkit shows when a required field is empty.</summary>
    public string? MsgRequired { get; set; }

    /// <summary>For a date: the user can empty it.</summary>
    public bool Clearable { get; set; }

    protected override string Control(string id, TagHelperAttributeList attributes)
    {
        var pass = PassThrough(attributes);
        var value = Type == "password" ? string.Empty : ValueText();
        var message = IsRequired ? $" data-msg-required=\"{WebUtility.HtmlEncode(MsgRequired ?? "Bitte fülle dieses Feld aus.")}\"" : string.Empty;
        var step = Type == "number" && !pass.Contains("step=", StringComparison.Ordinal) ? " step=\"any\"" : string.Empty;

        if (Type == "date")
        {
            var min = attributes.TryGetAttribute("min", out var minAttr) ? $" data-min=\"{WebUtility.HtmlEncode(minAttr.Value?.ToString())}\"" : string.Empty;
            var max = attributes.TryGetAttribute("max", out var maxAttr) ? $" data-max=\"{WebUtility.HtmlEncode(maxAttr.Value?.ToString())}\"" : string.Empty;
            return $"<div class=\"mat-picker\" data-picker=\"date\"{(Clearable || !IsRequired ? " data-clearable" : "")}{min}{max}>" +
                   $"<input class=\"mat-picker__input\"{CommonAttributes(id)} value=\"{WebUtility.HtmlEncode(value)}\"{message} /></div>";
        }

        var autocomplete = Type == "password" && !attributes.ContainsName("autocomplete") ? " autocomplete=\"current-password\"" : string.Empty;
        return $"<input class=\"mat-input\" type=\"{WebUtility.HtmlEncode(Type)}\"{CommonAttributes(id)} value=\"{WebUtility.HtmlEncode(value)}\"{message}{step}{autocomplete}{pass} />";
    }
}

/// <summary>A choice from a list: a select. More than five choices are a select, fewer are better as radios (<c>ms-radios</c>).</summary>
[HtmlTargetElement("ms-select")]
public sealed class SelectTagHelper : FieldTagHelper
{
    public IEnumerable<SelectListItem> Items { get; set; } = [];

    /// <summary>The text of an empty first option; without it the field has no empty choice.</summary>
    public string? Empty { get; set; }

    protected override string Control(string id, TagHelperAttributeList attributes)
    {
        var current = ValueText();
        var sb = new StringBuilder();
        sb.Append("<select class=\"mat-select\"").Append(CommonAttributes(id)).Append(PassThrough(attributes)).Append('>');
        if (Empty is not null)
        {
            sb.Append("<option value=\"\">").Append(WebUtility.HtmlEncode(Empty)).Append("</option>");
        }

        foreach (var item in Items)
        {
            var selected = item.Selected || (current.Length > 0 && item.Value == current);
            sb.Append("<option value=\"").Append(WebUtility.HtmlEncode(item.Value)).Append('"').Append(selected ? " selected" : "").Append('>')
              .Append(WebUtility.HtmlEncode(item.Text)).Append("</option>");
        }

        sb.Append("</select>");
        return sb.ToString();
    }
}

[HtmlTargetElement("ms-textarea")]
public sealed class TextareaTagHelper : FieldTagHelper
{
    public int Rows { get; set; } = 4;

    protected override string Control(string id, TagHelperAttributeList attributes)
        => $"<textarea class=\"mat-textarea\" rows=\"{Rows}\"{CommonAttributes(id)}{PassThrough(attributes)}>{WebUtility.HtmlEncode(ValueText())}</textarea>";
}

/// <summary>A switch: it applies a setting that is saved with the form. Posts true or false.</summary>
[HtmlTargetElement("ms-switch")]
public sealed class SwitchTagHelper : FieldTagHelper
{
    protected override bool LabelFirst => false;

    protected override string Control(string id, TagHelperAttributeList attributes)
    {
        var on = For?.Model is true || (ValueText() is "true" or "True" or "on");
        var label = Label ?? For?.Metadata.DisplayName ?? FieldName;
        return $"<label class=\"mat-switch\"><input type=\"checkbox\" value=\"true\"{CommonAttributes(id)}{(on ? " checked" : "")}{PassThrough(attributes)} />" +
               $"<span class=\"mat-switch__track\"></span><span>{WebUtility.HtmlEncode(label)}</span></label>" +
               $"<input type=\"hidden\" name=\"{WebUtility.HtmlEncode(FieldName)}\" value=\"false\" />";
    }
}

/// <summary>A choice of up to five options as radio buttons in a row.</summary>
[HtmlTargetElement("ms-radios")]
public sealed class RadiosTagHelper : FieldTagHelper
{
    public IEnumerable<SelectListItem> Items { get; set; } = [];

    protected override string Control(string id, TagHelperAttributeList attributes)
    {
        var current = ValueText();
        var sb = new StringBuilder("<div class=\"mat-checks mat-checks--inline\" role=\"radiogroup\">");
        var i = 0;
        foreach (var item in Items)
        {
            var itemId = $"{id}_{i++}";
            var checkedAttr = item.Selected || item.Value == current ? " checked" : "";
            sb.Append($"<label class=\"mat-check\"><input type=\"radio\" id=\"{itemId}\" name=\"{WebUtility.HtmlEncode(FieldName)}\" value=\"{WebUtility.HtmlEncode(item.Value)}\"{checkedAttr}{(IsRequired ? " required" : "")} />")
              .Append($"<span class=\"mat-check__text\">{WebUtility.HtmlEncode(item.Text)}</span></label>");
        }

        sb.Append("</div>");
        return sb.ToString();
    }
}

/// <summary>
/// A choice from a long list, with a search; one value or several (<c>multiple</c>). It posts the keys of the options, several joined by a comma and a space
/// (read them with <see cref="Services.IdList.Parse"/>). The description of an option is the name of the group of its <see cref="SelectListItem"/>.
/// </summary>
[HtmlTargetElement("ms-picker")]
public sealed class PickerTagHelper : FieldTagHelper
{
    public IEnumerable<SelectListItem> Items { get; set; } = [];

    public bool Multiple { get; set; }

    public bool Search { get; set; } = true;

    public string? Placeholder { get; set; }

    protected override string Control(string id, TagHelperAttributeList attributes)
    {
        var sb = new StringBuilder();
        sb.Append("<div class=\"mat-picker\" data-picker=\"select\"").Append(Multiple ? " data-multiple" : "").Append(Search ? " data-search" : "").Append(IsRequired ? "" : " data-clearable");
        if (Placeholder is not null)
        {
            sb.Append(" data-placeholder=\"").Append(WebUtility.HtmlEncode(Placeholder)).Append('"');
        }

        sb.Append("><input class=\"mat-picker__input\"").Append(CommonAttributes(id)).Append(" value=\"").Append(WebUtility.HtmlEncode(ValueText())).Append('"');
        if (IsRequired)
        {
            sb.Append(" data-msg-required=\"Bitte wähle etwas aus.\"");
        }

        sb.Append(" /><div class=\"mat-picker__options\" hidden>");
        foreach (var item in Items)
        {
            sb.Append("<button type=\"button\" class=\"mat-picker__option\" data-value=\"").Append(WebUtility.HtmlEncode(item.Value)).Append("\"><span class=\"mat-picker__text\"><span class=\"mat-picker__name\">")
              .Append(WebUtility.HtmlEncode(item.Text)).Append("</span>");
            if (item.Group?.Name is { Length: > 0 } description)
            {
                sb.Append("<span class=\"mat-picker__desc\">").Append(WebUtility.HtmlEncode(description)).Append("</span>");
            }

            sb.Append("</span></button>");
        }

        sb.Append("</div></div>");
        return sb.ToString();
    }
}

/// <summary>Select options from an enum, with German labels.</summary>
public static class Options
{
    public static List<SelectListItem> Of<TEnum>(Func<TEnum, string> label, IEnumerable<TEnum>? only = null) where TEnum : struct, Enum
        => [.. (only ?? Enum.GetValues<TEnum>()).Select(v => new SelectListItem(label(v), v.ToString()))];
}
