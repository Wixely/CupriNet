using System.Security.Cryptography;

namespace CupriNet.Persistence;

/// <summary>
/// Loads (or, on first use, creates) a random master key held in a permission-restricted file. This is
/// the pure-managed, cross-platform key-at-rest strategy: identical on Windows and Ubuntu, with no native
/// dependency. On Ubuntu the key file is mode 0600; on Windows it lives under the per-user profile.
/// </summary>
/// <remarks>
/// A file-guarded key is weaker than an OS-sealed key (e.g. DPAPI/Keychain). Those can be added later as
/// alternative <see cref="Abstractions.IDataProtector"/> implementations without changing callers.
/// </remarks>
public static class KeyFileMasterKey
{
    /// <summary>Default master-key length (256 bits), matching the AEAD key size.</summary>
    public const int DefaultKeySize = 32;

    /// <summary>Returns the master key at <paramref name="path"/>, creating it atomically if absent.</summary>
    public static byte[] LoadOrCreate(string path, int keySize = DefaultKeySize)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(keySize);

        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
            FilePermissions.RestrictDirectoryToOwner(directory);
        }

        if (File.Exists(fullPath))
        {
            var existing = File.ReadAllBytes(fullPath);
            if (existing.Length != keySize)
                throw new InvalidOperationException($"Master key at '{fullPath}' has unexpected length {existing.Length}.");
            return existing;
        }

        var key = RandomNumberGenerator.GetBytes(keySize);

        // Write to a sibling temp file, restrict it, then atomically move into place.
        var temp = fullPath + ".tmp-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6));
        File.WriteAllBytes(temp, key);
        FilePermissions.RestrictFileToOwner(temp);
        try
        {
            File.Move(temp, fullPath, overwrite: false);
            return key;
        }
        catch (IOException) when (File.Exists(fullPath))
        {
            // Another process won the race; discard ours and use the established key.
            File.Delete(temp);
            return File.ReadAllBytes(fullPath);
        }
    }
}
