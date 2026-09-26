namespace DocReader.Application.Errors;

/// <summary>A configuration resource (product, policy, repository, webhook) that does not exist.</summary>
/// <param name="resource">Kind of resource, in kebab case: <c>product-service</c>.</param>
/// <param name="identifier">Id or code that was asked for.</param>
public sealed class ResourceNotFoundException(string resource, string identifier)
    : Exception($"{resource} {identifier} was not found.")
{
    public string Resource { get; } = resource;

    public string Identifier { get; } = identifier;
}
