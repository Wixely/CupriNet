using CupriNet.Abstractions;
using CupriNet.Alembic;
using CupriNet.Alembic.Simulacrum;
using CupriNet.Persistence;
using Xunit;

namespace CupriNet.UnitTests;

public class SecretStoreTests
{
    private static ICryptoSuite Suite() => new SimulacrumSuite(InsecureConsent.IUnderstandThisProvidesNoSecurity());

    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cuprinet-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public async Task InMemory_RoundTrips_Overwrites_Deletes()
    {
        await AssertStoreContract(new InMemorySecretStore());
    }

    [Fact]
    public async Task File_RoundTrips_Overwrites_Deletes()
    {
        var dir = TempDir();
        try
        {
            var store = new FileSecretStore(dir, new NullDataProtector());
            await AssertStoreContract(store);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task File_WithAeadProtector_EncryptsAtRestFraming()
    {
        var dir = TempDir();
        try
        {
            var suite = Suite();
            var masterKey = new byte[suite.Aead.KeySize];
            var protector = new AeadDataProtector(suite, masterKey);
            var store = new FileSecretStore(dir, protector);

            byte[] secret = [1, 2, 3, 4, 5];
            await store.StoreAsync("identity/seal", secret);

            var loaded = await store.LoadAsync("identity/seal");
            Assert.NotNull(loaded);
            Assert.Equal(secret, loaded);

            // The on-disk blob is nonce-prefixed and longer than the plaintext (framing present).
            var file = Directory.EnumerateFiles(dir).Single();
            var onDisk = await File.ReadAllBytesAsync(file);
            Assert.True(onDisk.Length >= secret.Length + suite.Aead.NonceSize + suite.Aead.TagSize);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task File_OnUnix_RestrictsPermissions()
    {
        if (OperatingSystem.IsWindows())
            return; // POSIX-mode assertions only apply on Ubuntu/Linux/macOS

        var dir = TempDir();
        try
        {
            var store = new FileSecretStore(dir, new NullDataProtector());
            await store.StoreAsync("k", new byte[] { 9 });

            Assert.Equal(
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                File.GetUnixFileMode(dir));

            var file = Directory.EnumerateFiles(dir).Single();
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(file));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static async Task AssertStoreContract(ISecretStore store)
    {
        Assert.Null(await store.LoadAsync("missing"));

        await store.StoreAsync("a", new byte[] { 1, 2, 3 });
        Assert.Equal(new byte[] { 1, 2, 3 }, await store.LoadAsync("a"));

        await store.StoreAsync("a", new byte[] { 9 }); // overwrite
        Assert.Equal(new byte[] { 9 }, await store.LoadAsync("a"));

        await store.DeleteAsync("a");
        Assert.Null(await store.LoadAsync("a"));
    }
}
