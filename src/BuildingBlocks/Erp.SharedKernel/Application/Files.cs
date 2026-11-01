namespace Erp.SharedKernel.Application;

/// <summary>
/// Files kept outside the database, such as product photos. Keys are made by the caller from ids: lower-case, '/'-separated,
/// starting with the tenant's id (e.g. <c>{tenant}/commerce/{image}.jpg</c>). Who may read a file is decided by the database row
/// that names it (tenant filters and row-level security), never by the key; never build a key from user input.
/// </summary>
public interface IFileStore
{
    /// <summary>Saves the content under the key, replacing a file already there.</summary>
    Task SaveAsync(string key, Stream content, CancellationToken ct);

    /// <summary>The file for reading, or null when there is none.</summary>
    Task<Stream?> OpenAsync(string key, CancellationToken ct);

    /// <summary>Deletes the file; nothing happens when there is none.</summary>
    Task DeleteAsync(string key, CancellationToken ct);
}
