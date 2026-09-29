using System.ComponentModel;
using System.Diagnostics;

namespace DocReader.Infrastructure.Backup;

/// <summary>
/// Runs a child process without a shell: the arguments go through <see cref="ProcessStartInfo.ArgumentList"/>, so
/// nothing is parsed or expanded, and secrets travel only in environment variables of the child. stdout and stderr are
/// drained concurrently (a full pipe would otherwise block the child forever); stdout is discarded, the last lines of
/// stderr are kept. On cancellation or timeout the whole process tree is killed.
/// </summary>
public static class ExternalProcess
{
    private const int StandardErrorLinesKept = 40;

    private static readonly string[] InheritedVariables = ["PATH", "TMPDIR", "TEMP", "TMP", "SystemRoot", "LANG"];

    public static async Task<ExternalProcessResult> RunAsync(
        string fileName,
        IEnumerable<string> arguments,
        IReadOnlyDictionary<string, string> environment,
        TimeSpan timeout,
        CancellationToken ct)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetTempPath()
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        // The child gets a minimal environment, not a copy of ours: the worker's own variables include the connection
        // string and the encryption key, which the tool has no business seeing.
        startInfo.Environment.Clear();
        foreach (var inherited in InheritedVariables)
        {
            if (Environment.GetEnvironmentVariable(inherited) is { } value)
            {
                startInfo.Environment[inherited] = value;
            }
        }

        foreach (var (name, value) in environment)
        {
            startInfo.Environment[name] = value;
        }

        using var process = new Process { StartInfo = startInfo };
        var stderr = new Queue<string>();

        process.OutputDataReceived += (_, _) => { };
        process.ErrorDataReceived += (_, line) =>
        {
            if (line.Data is null)
            {
                return;
            }

            lock (stderr)
            {
                stderr.Enqueue(line.Data);
                while (stderr.Count > StandardErrorLinesKept)
                {
                    stderr.Dequeue();
                }
            }
        };

        try
        {
            process.Start();
        }
        catch (Win32Exception exception)
        {
            throw new PostgresToolException(
                $"Could not start {Path.GetFileName(fileName)}: it is not installed or not on PATH. The worker image installs postgresql-client-17.",
                exception);
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        // No tool here reads stdin; closing it makes a stray prompt fail at once instead of hanging.
        process.StandardInput.Close();

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);

        try
        {
            // Also waits for the redirected streams to reach end of file.
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Kill(process);

            if (ct.IsCancellationRequested)
            {
                throw;
            }

            throw new PostgresToolException(
                $"{Path.GetFileName(fileName)} did not finish within {timeout} and was stopped (DocReader:Backup:CommandTimeout).");
        }

        lock (stderr)
        {
            return new ExternalProcessResult(process.ExitCode, [.. stderr]);
        }
    }

    private static void Kill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit(TimeSpan.FromSeconds(10));
        }
        catch (InvalidOperationException)
        {
            // Already exited.
        }
        catch (Win32Exception)
        {
            // Exiting as we speak.
        }
    }
}
