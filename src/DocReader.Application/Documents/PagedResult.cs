namespace DocReader.Application.Documents;

/// <summary>
/// One page of a listing.
/// </summary>
/// <param name="Items">Items of the current page.</param>
/// <param name="Page">One based page number.</param>
/// <param name="PageSize">Requested page size.</param>
/// <param name="TotalCount">Total number of items matching the filter.</param>
public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, long TotalCount)
{
    public int TotalPages => PageSize <= 0 ? 0 : (int)Math.Ceiling(TotalCount / (double)PageSize);
}
