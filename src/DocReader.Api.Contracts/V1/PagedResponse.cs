namespace DocReader.Api.Contracts.V1;

/// <summary>
/// Envelope of every paginated listing.
/// </summary>
/// <param name="Items">Items of the current page, newest first.</param>
/// <param name="Page">One based page number.</param>
/// <param name="PageSize">Number of items per page.</param>
/// <param name="TotalCount">Total number of items matching the filters.</param>
/// <param name="TotalPages">Number of pages for the current page size.</param>
public sealed record PagedResponse<T>(
    IReadOnlyList<T> Items,
    int Page,
    int PageSize,
    long TotalCount,
    int TotalPages);
