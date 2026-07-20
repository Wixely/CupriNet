using CupriNet.Persistence;
using Xunit;

namespace CupriNet.UnitTests;

public class MasterKeyTests
{
    [Fact]
    public void LoadOrCreate_CreatesThenReturnsSameKey()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cuprinet-mk-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(dir, "master.key");
        try
        {
            var first = KeyFileMasterKey.LoadOrCreate(path);
            Assert.Equal(KeyFileMasterKey.DefaultKeySize, first.Length);

            var second = KeyFileMasterKey.LoadOrCreate(path);
            Assert.Equal(first, second); // stable across calls / process restarts
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void LoadOrCreate_OnUnix_KeyFileIsOwnerOnly()
    {
        if (OperatingSystem.IsWindows())
            return;

        var dir = Path.Combine(Path.GetTempPath(), "cuprinet-mk-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(dir, "master.key");
        try
        {
            KeyFileMasterKey.LoadOrCreate(path);
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }
}
