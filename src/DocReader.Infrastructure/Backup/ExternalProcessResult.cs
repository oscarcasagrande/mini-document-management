namespace DocReader.Infrastructure.Backup;

/// <summary>Exit code of a child process and the tail of what it wrote to stderr.</summary>
public sealed record ExternalProcessResult(int ExitCode, IReadOnlyList<string> StandardErrorTail);
