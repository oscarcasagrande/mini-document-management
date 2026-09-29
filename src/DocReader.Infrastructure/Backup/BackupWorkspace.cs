using DocReader.Application.Options;

namespace DocReader.Infrastructure.Backup;

/// <summary>A private temporary directory for one backup or restore run, removed with everything in it on dispose.</summary>
public sealed class BackupWorkspace : IAsyncDisposable
{
    private BackupWorkspace(string path)
    {
        Path = path;
    }

    public string Path { get; }

    public static BackupWorkspace Create(BackupOptions options)
    {
        var root = string.IsNullOrWhiteSpace(options.WorkingDirectory)
            ? System.IO.Path.Combine(System.IO.Path.GetTempPath(), "docreader-backup")
            : options.WorkingDirectory;

        var path = System.IO.Path.Combine(System.IO.Path.GetFullPath(root), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);

        return new BackupWorkspace(path);
    }

    public string CreateDirectory(string name)
    {
        var directory = System.IO.Path.Combine(Path, name);
        Directory.CreateDirectory(directory);

        return directory;
    }

    public ValueTask DisposeAsync()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
            // Already gone.
        }
        catch (IOException)
        {
            // A temporary directory left behind is not worth failing the run over.
        }
        catch (UnauthorizedAccessException)
        {
            // Same.
        }

        return ValueTask.CompletedTask;
    }
}
