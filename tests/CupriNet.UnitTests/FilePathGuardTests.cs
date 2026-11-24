using CupriNet.Rites;
using Xunit;

namespace CupriNet.UnitTests;

public class FilePathGuardTests
{
    [Theory]
    [InlineData("notes.txt", "notes.txt")]
    [InlineData("docs/report.pdf", "docs/report.pdf")]
    [InlineData("a/b/c/d.bin", "a/b/c/d.bin")]
    [InlineData("mixed\\sep\\file", "mixed/sep/file")] // backslashes normalized to forward slashes
    public void Normalize_AcceptsSafeRelativePaths(string input, string expected)
    {
        Assert.Equal(expected, FilePathGuard.Normalize(input));
        Assert.True(FilePathGuard.IsSafe(input));
    }

    [Theory]
    [InlineData("")]
    [InlineData("../secret")]                 // traversal
    [InlineData("a/../../etc/passwd")]        // traversal in the middle
    [InlineData("/etc/passwd")]               // absolute (unix)
    [InlineData("C:\\Windows\\system32")]     // absolute (windows, drive colon)
    [InlineData("\\\\server\\share\\x")]     // UNC
    [InlineData("a//b")]                        // empty segment
    [InlineData("./relative")]                  // current-dir segment
    [InlineData("stream:name")]                 // colon / alternate data stream
    [InlineData("what?.txt")]                   // Windows-invalid characters, refused on every OS
    [InlineData("a<b")]
    [InlineData("pipe|name")]
    [InlineData("star*.bin")]
    [InlineData("quo\"te")]
    [InlineData("NUL")]                         // Windows device names, in any case, with any extension, at any depth
    [InlineData("docs/con.txt")]
    [InlineData("Com1.log")]
    [InlineData("lpt9")]
    [InlineData("trailing.")]                   // Windows strips a trailing dot or space
    [InlineData("dir /file")]
    public void Normalize_RejectsUnsafePaths(string input)
    {
        Assert.Null(FilePathGuard.Normalize(input));
        Assert.False(FilePathGuard.IsSafe(input));
    }

    [Fact]
    public void Normalize_RejectsOverlongPaths()
    {
        var longPath = new string('a', 2000);
        Assert.Null(FilePathGuard.Normalize(longPath, maxLength: 1024));
    }
}
