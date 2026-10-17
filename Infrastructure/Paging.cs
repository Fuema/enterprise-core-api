using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;

namespace AngularCrudApi.Infrastructure;

/// <summary>
/// Query string of every list endpoint (template contract):
///   ?page=1&amp;pageSize=20&amp;search=ana&amp;sort=lastName.asc,dateOfBirth.desc
/// Bind with <c>[AsParameters] PageRequest paging</c>.
/// </summary>
public sealed record PageRequest(int? Page, int? PageSize, string? Search, string? Sort)
{
    public const int DefaultPageSize = 20;
    public const int MaxPageSize = 100;

    public int SafePage => Math.Max(1, Page ?? 1);
    public int SafePageSize => Math.Clamp(PageSize ?? DefaultPageSize, 1, MaxPageSize);

    /// <summary>Whitespace-separated search terms (each must match at least one searchable column).</summary>
    public string[] SearchTerms =>
        string.IsNullOrWhiteSpace(Search)
            ? []
            : Search.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(5).ToArray();
}

/// <summary>Response body of every list endpoint: { data, totalCount, pageSize, currentPage }.</summary>
public sealed record PagedResponse<T>(IReadOnlyList<T> Data, int TotalCount, int PageSize, int CurrentPage);

public static class PagingExtensions
{
    public static async Task<PagedResponse<T>> ToPagedAsync<T>(
        this IQueryable<T> query, PageRequest paging, CancellationToken cancellationToken)
    {
        var totalCount = await query.CountAsync(cancellationToken);
        var data = await query
            .Skip((paging.SafePage - 1) * paging.SafePageSize)
            .Take(paging.SafePageSize)
            .ToListAsync(cancellationToken);
        return new PagedResponse<T>(data, totalCount, paging.SafePageSize, paging.SafePage);
    }
}

/// <summary>
/// Allow-list of sortable fields for one resource. Parses "field.asc,field2.desc", ignores
/// unknown fields (never builds SQL from user input) and falls back to a default sort.
/// </summary>
/// <example>
/// static readonly SortMap&lt;EmployeeResponse&gt; Sorts = new SortMap&lt;EmployeeResponse&gt;()
///     .Add("lastName", e => e.LastName)
///     .Add("dateOfBirth", e => e.DateOfBirth);
/// query = Sorts.Apply(query, paging.Sort, "lastName.asc").ThenBy(e => e.Id);
/// </example>
public sealed class SortMap<T>
{
    private const int MaxSortFields = 5;

    private readonly Dictionary<string, Func<IQueryable<T>, bool, bool, IOrderedQueryable<T>>> _fields =
        new(StringComparer.OrdinalIgnoreCase);

    public SortMap<T> Add<TKey>(string field, Expression<Func<T, TKey>> key)
    {
        _fields[field] = (query, descending, isFirst) => isFirst
            ? descending ? query.OrderByDescending(key) : query.OrderBy(key)
            : descending ? ((IOrderedQueryable<T>)query).ThenByDescending(key) : ((IOrderedQueryable<T>)query).ThenBy(key);
        return this;
    }

    public IOrderedQueryable<T> Apply(IQueryable<T> query, string? sort, string defaultSort)
    {
        var ordered = ApplyTokens(query, sort) ?? ApplyTokens(query, defaultSort);
        return ordered ?? throw new InvalidOperationException($"Default sort '{defaultSort}' uses no registered field.");
    }

    private IOrderedQueryable<T>? ApplyTokens(IQueryable<T> query, string? sort)
    {
        if (string.IsNullOrWhiteSpace(sort)) return null;

        IOrderedQueryable<T>? ordered = null;
        foreach (var token in sort.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(MaxSortFields))
        {
            var parts = token.Split('.', 2);
            if (!_fields.TryGetValue(parts[0], out var apply)) continue;
            var descending = parts.Length > 1 && parts[1].Equals("desc", StringComparison.OrdinalIgnoreCase);
            ordered = apply(ordered ?? query, descending, ordered is null);
        }
        return ordered;
    }
}
