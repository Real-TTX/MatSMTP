using Microsoft.EntityFrameworkCore;

namespace MatSmtp.UiKit;

/// <summary>One filter of a list: its options and what it does to the query.</summary>
public sealed record FilterSpec<T>(string Name, string Label, IReadOnlyList<FilterOption> Options, Func<IQueryable<T>, string, IQueryable<T>> Apply);

/// <summary>What a list page says about its list; <see cref="ListBuilder.RunAsync"/> turns it into the toolbar, the pager and the rows of the page.</summary>
public sealed class ListSpec<T>
{
    public required string Id { get; init; }

    public required string Action { get; init; }

    public string Label { get; init; } = "Liste";

    public string Noun { get; init; } = "Einträge";

    public string SearchPlaceholder { get; init; } = "Suchen";

    /// <summary>What a search for a text does to the query; null: the list has no search.</summary>
    public Func<IQueryable<T>, string, IQueryable<T>>? Search { get; init; }

    public IReadOnlyList<FilterSpec<T>> Filters { get; init; } = [];

    /// <summary>The sorts by name: label and how to order (the bool says descending).</summary>
    public IReadOnlyDictionary<string, (string Label, Func<IQueryable<T>, bool, IOrderedQueryable<T>> Order)> Sorts { get; init; }
        = new Dictionary<string, (string, Func<IQueryable<T>, bool, IOrderedQueryable<T>>)>();

    public required string DefaultSort { get; init; }

    public bool DefaultDescending { get; init; }

    public int PageSize { get; init; } = 20;

    public IReadOnlyDictionary<string, string?> Keep { get; init; } = new Dictionary<string, string?>();
}

/// <summary><see cref="Filtered"/> is the query with search and filters applied, before sorting and paging: for the numbers above a list (totals, charts).</summary>
public sealed record ListResult<T>(IReadOnlyList<T> Items, ToolbarModel Toolbar, PagerModel Pager, IQueryable<T> Filtered);

public static class ListBuilder
{
    /// <summary>Applies search, filters, sort and the page of the request to <paramref name="all"/> and returns the rows with the models of the toolbar and the pager.</summary>
    public static async Task<ListResult<T>> RunAsync<T>(IQueryable<T> all, HttpRequest request, ListSpec<T> spec, CancellationToken ct = default)
    {
        var total = await all.CountAsync(ct);
        var query = all;

        var search = ListQuery.Text(request, "q");
        if (search is not null && spec.Search is not null)
        {
            query = spec.Search(query, search);
        }

        var selected = new Dictionary<string, string?>();
        foreach (var filter in spec.Filters)
        {
            var value = ListQuery.Text(request, filter.Name);
            if (value is not null && filter.Options.Any(o => o.Value == value))
            {
                query = filter.Apply(query, value);
                selected[filter.Name] = value;
            }
        }

        var sort = ListQuery.Text(request, "sort");
        if (sort is null || !spec.Sorts.ContainsKey(sort))
        {
            sort = spec.DefaultSort;
        }

        var descending = request.Query.ContainsKey("dir") ? ListQuery.Descending(request) : sort == spec.DefaultSort && spec.DefaultDescending;
        var matching = await query.CountAsync(ct);

        var pageSize = spec.PageSize;
        var pages = Math.Max(1, (int)Math.Ceiling(matching / (double)pageSize));
        var page = Math.Min(ListQuery.Page(request), pages);

        IQueryable<T> ordered = spec.Sorts.TryGetValue(sort, out var order) ? order.Order(query, descending) : query;
        var items = await ordered.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);

        var toolbar = new ToolbarModel
        {
            Id = spec.Id,
            Action = spec.Action,
            Label = spec.Label,
            Noun = spec.Noun,
            Search = search,
            SearchPlaceholder = spec.SearchPlaceholder,
            Filters = [.. spec.Filters.Select(f => new FilterField(f.Name, f.Label, f.Options, selected.GetValueOrDefault(f.Name)))],
            SortOptions = [.. spec.Sorts.Select(s => new SortOption(s.Key, s.Value.Label))],
            Sort = sort,
            Descending = descending,
            DefaultSort = spec.DefaultSort,
            Total = total,
            Matching = matching,
            Keep = spec.Keep,
        };
        var pager = new PagerModel { Page = page, PageSize = pageSize, Total = matching, Url = toolbar.UrlWithPage };
        return new ListResult<T>(items, toolbar, pager, query);
    }

    /// <summary>The options of a filter from an enum: the value is the name, the label comes from <paramref name="label"/>.</summary>
    public static IReadOnlyList<FilterOption> EnumOptions<TEnum>(Func<TEnum, string> label) where TEnum : struct, Enum
        => [.. Enum.GetValues<TEnum>().Select(v => new FilterOption(v.ToString(), label(v)))];
}
