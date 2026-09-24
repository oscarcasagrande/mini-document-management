namespace DocReader.Application.Documents;

/// <summary>
/// Canonical fields of the original 202 response, stored with the idempotency key so a replay is
/// answered with exactly the same identity.
/// </summary>
/// <param name="Id">Document id.</param>
/// <param name="Protocol">Protocol issued at the time.</param>
/// <param name="Status">Status reported at the time.</param>
public sealed record UploadResponseSnapshot(Guid Id, string Protocol, string Status);
