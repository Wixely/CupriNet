using System.Buffers.Text;
using System.Text;
using CupriNet.Abstractions;

namespace CupriNet.Persistence;

/// <summary>
/// A file-backed <see cref="ISecretStore"/>: each logical key becomes one file, its bytes wrapped by an
/// <see cref="IDataProtector"/>. Works identically on Windows and Ubuntu; on Ubuntu the directory and
/// files are restricted to the owner (0700 / 0600). Writes are atomic (temp file + move).
/// </summary>
public sealed class FileSecretStore : ISecretStore
{
    private const string Extension = ".secret";

    private readonly string _directory;
    private readonly IDataProtector _protector;

    public FileSecretStore(string directory, IDataProtector protector)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        ArgumentNullException.ThrowIfNull(protector);

        _directory = Path.GetFullPath(directory);
        _protector = protector;

        Directory.CreateDirectory(_directory);
        FilePermissions.RestrictDirectoryToOwner(_directory);
    }

    public async ValueTask StoreAsync(string key, ReadOnlyMemory<byte> secret, CancellationToken cancellationToken = default)
    {
        var path = PathFor(key);
        var protectedBytes = _protector.Protect(secret.Span);

        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        await File.WriteAllBytesAsync(temp, protectedBytes, cancellationToken).ConfigureAwait(false);
        FilePermissions.RestrictFileToOwner(temp);
        File.Move(temp, path, overwrite: true);
    }

    public async ValueTask<byte[]?> LoadAsync(string key, CancellationToken cancellationToken = default)
    {
        var path = PathFor(key);
        if (!File.Exists(path))
            return null;

        var protectedBytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        return _protector.Unprotect(protectedBytes);
    }

    public ValueTask DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        var path = PathFor(key);
        if (File.Exists(path))
            File.Delete(path);
        return ValueTask.CompletedTask;
    }

    private string PathFor(string key)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        // Base64Url of the UTF-8 key gives a filesystem-safe, collision-free name.
        var fileName = Base64Url.EncodeToString(Encoding.UTF8.GetBytes(key)) + Extension;
        return Path.Combine(_directory, fileName);
    }
}
