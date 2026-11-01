using System.Text.RegularExpressions;
using Erp.SharedKernel.Application;

namespace Erp.Infrastructure.Files;

/// <summary>
/// Storage:FilesRoot is the folder files are kept in, relative to the program folder or absolute. Without it files go next to the
/// program folder (<c>..\files</c>, e.g. C:\ERP\files), never inside it: an update replaces the program folder.
/// </summary>
public sealed class StorageOptions
{
    public const string Section = "Storage";
    public string? FilesRoot { get; set; }

    public string ResolveRoot(string contentRoot) =>
        Path.GetFullPath(string.IsNullOrWhiteSpace(FilesRoot) ? Path.Combine(contentRoot, "..", "files") : Path.Combine(contentRoot, FilesRoot));
}

/// <summary>Files on this computer's disk, under one root folder. Backups copy that folder (scripts/backup.ps1).</summary>
public sealed partial class LocalFileStore : IFileStore
{
    private readonly string root;

    /// <summary>The folder is made on the first save, so a missing or read-only folder never stops the program from starting.</summary>
    public LocalFileStore(string root) =>
        this.root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    [GeneratedRegex("^[a-z0-9][a-z0-9_-]*(/[a-z0-9][a-z0-9_-]*)*(\\.[a-z0-9]{1,8})?$")]
    private static partial Regex KeyPattern();

    private string PathOf(string key)
    {
        if (key.Length > 300 || !KeyPattern().IsMatch(key)) throw new ArgumentException($"Not a valid file key: {key}", nameof(key));
        var path = Path.GetFullPath(Path.Combine(root, key.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"Not a valid file key: {key}", nameof(key));
        return path;
    }

    public async Task SaveAsync(string key, Stream content, CancellationToken ct)
    {
        var path = PathOf(key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // Written beside the file and then moved in, so a reader never sees half a file.
        var temp = $"{path}.{Guid.NewGuid():N}.part";
        try
        {
            await using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                await content.CopyToAsync(file, ct);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    public Task<Stream?> OpenAsync(string key, CancellationToken ct)
    {
        var path = PathOf(key);
        return Task.FromResult<Stream?>(File.Exists(path) ? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true) : null);
    }

    public Task DeleteAsync(string key, CancellationToken ct)
    {
        var path = PathOf(key);
        if (File.Exists(path)) File.Delete(path);
        return Task.CompletedTask;
    }
}
