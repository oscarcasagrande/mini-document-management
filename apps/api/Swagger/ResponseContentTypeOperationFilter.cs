using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace DocReader.Api.Swagger;

/// <summary>
/// Narrows the media types of each documented response to what the API really answers: JSON or the
/// original bytes on success, and <c>application/problem+json</c> on every failure. Without this the
/// document lists whatever the output formatters happen to accept, such as <c>text/plain</c>, which
/// only makes the Swagger page harder to read.
/// </summary>
public sealed class ResponseContentTypeOperationFilter : IOperationFilter
{
    private const string ProblemJson = "application/problem+json";

    private static readonly string[] NoiseOnSuccess = ["text/plain", "text/json"];

    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        if (operation.Responses is null)
        {
            return;
        }

        foreach (var (statusCode, response) in operation.Responses)
        {
            if (response.Content is null || response.Content.Count == 0)
            {
                continue;
            }

            var isFailure = int.TryParse(statusCode, out var code) && code >= 400;

            if (isFailure)
            {
                if (response.Content.ContainsKey(ProblemJson))
                {
                    RemoveAllExcept(response.Content, ProblemJson);
                }

                continue;
            }

            response.Content.Remove(ProblemJson);
            foreach (var noise in NoiseOnSuccess)
            {
                response.Content.Remove(noise);
            }
        }
    }

    private static void RemoveAllExcept(IDictionary<string, OpenApiMediaType> content, string keep)
    {
        foreach (var mediaType in content.Keys.Where(key => key != keep).ToArray())
        {
            content.Remove(mediaType);
        }
    }
}
