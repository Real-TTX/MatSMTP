using System.Web;
using Microsoft.AspNetCore.Html;

namespace MatSmtp.UiKit;

public sealed record FilterOption(string Value, string Label);

/// <summary>One filter of the filter dialog: a select with an "Alle" option.</summary>
public sealed record FilterField(string Name, string Label, IReadOnlyList<FilterOption> Options, string? Selected);

public sealed record SortOption(string Value, string Label);

/// <summary>
/// Everything the toolbar partial needs. The toolbar sits above every list (search, filter, sort); filter and sort are buttons that open dialogs,
/// the search is a field in the bar. All their fields belong to ONE GET form, so every dialog sends the complete state.
/// Several toolbars can share a page: every id and form is derived from <see cref="Id"/>.
/// </summary>
public sealed class ToolbarModel
{
    public required string Id { get; init; }

    public required string Action { get; init; }

    /// <summary>Names the list for screen readers ("Nachrichten").</summary>
    public string Label { get; init; } = "Liste";

    public string? Search { get; init; }

    public string SearchPlaceholder { get; init; } = "Suchen";

    public IReadOnlyList<FilterField> Filters { get; init; } = [];

    public IReadOnlyList<SortOption> SortOptions { get; init; } = [];

    public string? Sort { get; init; }

    public bool Descending { get; init; }

    public string DefaultSort { get; init; } = string.Empty;

    public int Total { get; init; }

    public int Matching { get; init; }

    public string Noun { get; init; } = "Einträge";

    /// <summary>Query values that are neither search, filter nor sort (the id of a project) and must survive an "Anwenden".</summary>
    public IReadOnlyDictionary<string, string?> Keep { get; init; } = new Dictionary<string, string?>();

    public bool SearchActive => !string.IsNullOrWhiteSpace(Search);

    public int ActiveFilters => Filters.Count(f => !string.IsNullOrEmpty(f.Selected));

    public bool SortActive => !string.IsNullOrEmpty(Sort) && (Sort != DefaultSort || Descending);

    public string SortLabel => SortOptions.FirstOrDefault(o => o.Value == (Sort ?? DefaultSort))?.Label ?? string.Empty;

    /// <summary>The address of a page of the list, with every tool in effect.</summary>
    public string UrlWithPage(int page)
    {
        var url = UrlWithout(string.Empty);
        return page <= 1 ? url : url + (url.Contains('?') ? "&" : "?") + "page=" + page;
    }

    /// <summary>The address of the list with every tool in effect except <paramref name="remove"/>.</summary>
    public string UrlWithout(string remove)
    {
        var pairs = new List<(string, string?)>();
        foreach (var (k, v) in Keep)
        {
            pairs.Add((k, v));
        }

        if (remove != "q" && SearchActive)
        {
            pairs.Add(("q", Search));
        }

        foreach (var f in Filters.Where(f => !string.IsNullOrEmpty(f.Selected) && f.Name != remove))
        {
            pairs.Add((f.Name, f.Selected));
        }

        if (remove != "sort" && SortActive)
        {
            pairs.Add(("sort", Sort));
            if (Descending)
            {
                pairs.Add(("dir", "desc"));
            }
        }

        var query = string.Join("&", pairs.Where(p => !string.IsNullOrEmpty(p.Item2)).Select(p => $"{HttpUtility.UrlEncode(p.Item1)}={HttpUtility.UrlEncode(p.Item2)}"));
        return query.Length == 0 ? Action : $"{Action}?{query}";
    }
}

/// <summary>Pagination. The page number is read from the query string, never a route value.</summary>
public sealed class PagerModel
{
    public required int Page { get; init; }

    public required int PageSize { get; init; }

    public required int Total { get; init; }

    /// <summary>Builds the address of a page; the query of the list (search, filters, sort) is already part of it.</summary>
    public required Func<int, string> Url { get; init; }

    public int Pages => Math.Max(1, (int)Math.Ceiling(Total / (double)PageSize));

    public int From => Total == 0 ? 0 : (Page - 1) * PageSize + 1;

    public int To => Math.Min(Total, Page * PageSize);

    /// <summary>The page numbers to show: first, last and two on each side of the current one; 0 stands for a gap.</summary>
    public IReadOnlyList<int> Window()
    {
        var shown = new SortedSet<int> { 1, Pages };
        for (var p = Page - 2; p <= Page + 2; p++)
        {
            if (p >= 1 && p <= Pages)
            {
                shown.Add(p);
            }
        }

        var result = new List<int>();
        var last = 0;
        foreach (var p in shown)
        {
            if (last != 0 && p - last > 1)
            {
                result.Add(0);
            }

            result.Add(p);
            last = p;
        }

        return result;
    }
}

/// <summary>One tab of a group of tabs that are links.</summary>
public sealed record LinkTab(string Key, string Title, string Href, string? Icon = null, int? Count = null);

/// <summary>A group of tabs that are links: each one is a page of its own, the address says which is open.</summary>
public sealed record LinkTabsModel(string Label, IReadOnlyList<LinkTab> Tabs, string Selected);

/// <summary>A tool of a row: an icon button at the end of it (open, edit).</summary>
public sealed record RowAction(string Icon, string Label, string Href);

public sealed record TableColumn(string Header, bool Numeric = false, bool HideOnPhone = false, bool Mono = false);

/// <summary>A row; <paramref name="SelectValue"/> is the value of its check when the table is selectable, <paramref name="SelectLabel"/> names it for a screen reader.</summary>
public sealed record TableRow(IReadOnlyList<IHtmlContent> Cells, IReadOnlyList<RowAction> Actions, string? SelectValue = null, string? SelectLabel = null);

/// <summary>
/// The one table control: header, rows, the empty state, the actions at the end of a row. On a phone the rows become cards (each cell carries its
/// <c>data-label</c>). Several tables can share a page: the id makes the caption and the empty state unique.
/// </summary>
public sealed record TableModel
{
    public required string Id { get; init; }

    public required string Caption { get; init; }

    public required IReadOnlyList<TableColumn> Columns { get; init; }

    public required IReadOnlyList<TableRow> Rows { get; init; }

    public string EmptyTitle { get; init; } = "Nichts gefunden";

    public string EmptyText { get; init; } = "Ändere die Suche oder den Filter.";

    public string EmptyIcon { get; init; } = "search";

    /// <summary>The form the checks of a selectable table belong to (the actions below the list); null: no checks.</summary>
    public string? SelectForm { get; init; }

    public string SelectName { get; init; } = "ids";

    public bool HasActions => Rows.Any(r => r.Actions.Count > 0);

    /// <summary>Builds a table from items: one column definition per column, a cell for each.</summary>
    public static TableModel Of<T>(string id, string caption, IEnumerable<T> items, IReadOnlyList<(TableColumn Column, Func<T, IHtmlContent> Cell)> columns, Func<T, IReadOnlyList<RowAction>>? actions = null, Func<T, (string Value, string Label)>? select = null)
        => new()
        {
            Id = id,
            Caption = caption,
            Columns = [.. columns.Select(c => c.Column)],
            Rows = [.. items.Select(item => new TableRow([.. columns.Select(c => c.Cell(item))], actions?.Invoke(item) ?? [], select?.Invoke(item).Value, select?.Invoke(item).Label))],
        };
}

/// <summary>Reading the common list parameters from a query string.</summary>
public static class ListQuery
{
    public static int Page(HttpRequest request)
        => int.TryParse(request.Query["page"], out var p) && p > 0 ? p : 1;

    public static string? Text(HttpRequest request, string name)
    {
        var value = request.Query[name].ToString().Trim();
        return value.Length == 0 ? null : value;
    }

    public static bool Descending(HttpRequest request) => string.Equals(request.Query["dir"], "desc", StringComparison.OrdinalIgnoreCase);
}

/// <summary>The breadcrumb a page hands to the layout (below the app name). The last item is the current page.</summary>
public static class Crumbs
{
    public static IReadOnlyList<(string Text, string? Url)> Of(params (string Text, string? Url)[] items) => items;
}
