namespace DocReader.Application.Retention;

/// <param name="DocumentType">Only policies of this document type.</param>
/// <param name="ProductServiceId">Only policies of this product or service.</param>
/// <param name="Page">One based page number.</param>
/// <param name="PageSize">Items per page.</param>
public sealed record RetentionPolicyFilter(string? DocumentType, Guid? ProductServiceId, int Page, int PageSize);
