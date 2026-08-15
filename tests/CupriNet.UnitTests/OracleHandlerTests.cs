using System.Net;
using System.Security.Cryptography;
using CupriNet.Rites;
using CupriNet.Vessel;
using Xunit;
using VesselSession = CupriNet.Vessel.Vessel;

namespace CupriNet.UnitTests;

public class OracleHandlerTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("cuprinet-oracle-");

    public void Dispose()
    {
        try { _root.Delete(recursive: true); } catch { /* best effort cleanup */ }
    }

    private void Write(string relative, string content)
    {
        var full = Path.Combine(_root.FullName, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    [Fact]
    public async Task Delegate_Handler_ReturnsDelegateResult()
    {
        var handler = new DelegateOracleHandler(req => OracleResponse.Text($"you asked for {req.Path}"));
        var response = await handler.HandleAsync(OracleRequest.Get("/hello"));
        Assert.Equal(200u, response.Status);
        Assert.Equal("you asked for /hello", response.AsText());
    }

    [Fact]
    public async Task StaticFile_ServesIndex_ForRoot()
    {
        Write("index.html", "<h1>home</h1>");
        var handler = new StaticFileOracleHandler(_root.FullName);
        var response = await handler.HandleAsync(OracleRequest.Get("/"));
        Assert.Equal(200u, response.Status);
        Assert.Equal("text/html; charset=utf-8", response.ContentType);
        Assert.Equal("<h1>home</h1>", response.AsText());
    }

    [Fact]
    public async Task StaticFile_ServesNestedFile_AndMapsContentType()
    {
        Write("assets/app.wasm", "\0asm");
        var handler = new StaticFileOracleHandler(_root.FullName);
        var response = await handler.HandleAsync(OracleRequest.Get("/assets/app.wasm"));
        Assert.Equal(200u, response.Status);
        Assert.Equal("application/wasm", response.ContentType);
    }

    [Fact]
    public async Task StaticFile_404_ForMissingFile()
    {
        var handler = new StaticFileOracleHandler(_root.FullName);
        var response = await handler.HandleAsync(OracleRequest.Get("/nope.html"));
        Assert.Equal(404u, response.Status);
    }

    [Fact]
    public async Task StaticFile_BlocksTraversal()
    {
        Write("index.html", "ok");
        var handler = new StaticFileOracleHandler(_root.FullName);
        var response = await handler.HandleAsync(OracleRequest.Get("/../../etc/passwd"));
        Assert.Equal(404u, response.Status);
    }

    [Fact]
    public async Task StaticFile_405_ForNonGet()
    {
        var handler = new StaticFileOracleHandler(_root.FullName);
        var response = await handler.HandleAsync(new OracleRequest { Method = "POST", Path = "/index.html", Body = [1] });
        Assert.Equal(405u, response.Status);
    }

    [Fact]
    public async Task OracleHost_Serves_MultipleRequests_OverOneSession()
    {
        Write("index.html", "<h1>home</h1>");
        using var cts = new CancellationTokenSource(Timeout);
        var ct = cts.Token;
        var suite = CryptoSuites.Secure();
        var sessionKey = RandomNumberGenerator.GetBytes(suite.Aead.KeySize);

        var listener = new VesselListener(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Start();
        var acceptTask = listener.AcceptAsync(ct);
        VesselSession va = await TcpVessel.ConnectAsync("127.0.0.1", listener.LocalEndPoint.Port, cancellationToken: ct);
        VesselSession vb = await acceptTask;
        await using var _a = va;
        await using var _b = vb;
        await using var _l = listener;

        var pilgrim = new OracleSession(va, sessionKey, suite);
        var shrine = new OracleSession(vb, sessionKey, suite);

        using var hostCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var hostTask = OracleHost.ServeAsync(shrine, new StaticFileOracleHandler(_root.FullName), hostCts.Token);

        var ok = await pilgrim.ConsultAsync(OracleRequest.Get("/index.html"), ct);
        Assert.Equal(200u, ok.Status);
        Assert.Equal("<h1>home</h1>", ok.AsText());

        var missing = await pilgrim.ConsultAsync(OracleRequest.Get("/missing"), ct);
        Assert.Equal(404u, missing.Status);

        await hostCts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => hostTask);
    }
}
