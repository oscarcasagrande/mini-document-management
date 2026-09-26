using DocReader.Domain.Storage;

namespace DocReader.Application.Storage;

/// <param name="Code">Partial code, case insensitive.</param>
/// <param name="Provider">Only repositories of this provider.</param>
/// <param name="Active">Only active (true) or inactive (false) repositories.</param>
/// <param name="Page">One based page number.</param>
/// <param name="PageSize">Items per page.</param>
public sealed record StorageRepositoryFilter(string? Code, StorageProvider? Provider, bool? Active, int Page, int PageSize);
