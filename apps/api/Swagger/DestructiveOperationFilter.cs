using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace DocReader.Api.Swagger;

/// <summary>
/// Marks destructive endpoints so they are unmistakable in the Swagger page, as required by the
/// Swagger definition of done (PRD section 17).
/// </summary>
public sealed class DestructiveOperationFilter : IOperationFilter
{
    private const string Warning = "DESTRUCTIVE AND IRREVERSIBLE — ";

    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        var method = context.ApiDescription.HttpMethod;

        if (!string.Equals(method, "DELETE", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        operation.Summary = Warning + operation.Summary;
    }
}
