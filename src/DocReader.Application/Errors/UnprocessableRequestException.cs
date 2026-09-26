namespace DocReader.Application.Errors;

/// <summary>The request is well formed but refers to something that does not exist, so it cannot be carried out (422).</summary>
public sealed class UnprocessableRequestException(string errorCode, string message) : Exception(message)
{
    public string ErrorCode { get; } = errorCode;
}
