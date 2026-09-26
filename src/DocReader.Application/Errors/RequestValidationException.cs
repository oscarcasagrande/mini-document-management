namespace DocReader.Application.Errors;

/// <summary>A request that is well formed but names something invalid.</summary>
public sealed class RequestValidationException(string errorCode, string message) : Exception(message)
{
    public string ErrorCode { get; } = errorCode;
}
