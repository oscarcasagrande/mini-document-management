namespace DocReader.Application.Catalog;

/// <param name="Code">Partial code, case insensitive.</param>
/// <param name="Name">Partial name, case insensitive.</param>
/// <param name="Active">Only active (true) or only inactive (false) products; null for both.</param>
/// <param name="Page">One based page number.</param>
/// <param name="PageSize">Items per page.</param>
public sealed record ProductServiceFilter(string? Code, string? Name, bool? Active, int Page, int PageSize);
