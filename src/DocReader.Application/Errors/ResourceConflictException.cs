namespace DocReader.Application.Errors;

/// <summary>The request contradicts the current state: a duplicate, a resource in use, a rule that keeps one alive.</summary>
public sealed class ResourceConflictException(string errorCode, string message) : Exception(message)
{
    public string ErrorCode { get; } = errorCode;
}
