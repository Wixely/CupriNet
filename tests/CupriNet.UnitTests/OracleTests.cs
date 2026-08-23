using System.Net;
using System.Security.Cryptography;
using CupriNet.Codex;
using CupriNet.Marks;
using CupriNet.Rites;
using CupriNet.Vessel;
using Xunit;
using VesselSession = CupriNet.Vessel.Vessel;

namespace CupriNet.UnitTests;

public class OracleTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    [Fact]
    public void OracleRequest_Codec_RoundTrips()
    {
        var request = new OracleRequest
        {
            Method = "GET",
            Path = "/index.html",
            Headers = [new OracleHeader("accept", "text/html"), new OracleHeader("x-thing", "42")],
            Body = [1, 2, 3],
        };

        var decoded = OracleCodec.DecodeRequest(OracleCodec.EncodeRequest(request));

        Assert.Equal("GET", decoded.Method);
        Assert.Equal("/index.html", decoded.Path);
        Assert.Equal(2, decoded.Headers.Count);
        Assert.Equal("text/html", decoded.Header("accept"));
        Assert.Equal("text/html", decoded.Header("ACCEPT")); // case-insensitive
        Assert.Equal("42", decoded.Header("x-thing"));
        Assert.Null(decoded.Header("missing"));
        Assert.Equal(new byte[] { 1, 2, 3 }, decoded.Body);
    }

    [Fact]
    public void OracleResponse_Codec_RoundTrips()
    {
        var response = OracleResponse.Ok("<h1>hi</h1>"u8, "text/html");

        var decoded = OracleCodec.DecodeResponse(OracleCodec.EncodeResponse(response));

        Assert.Equal(200u, decoded.Status);
        Assert.Equal("text/html", decoded.ContentType);
        Assert.Equal("<h1>hi</h1>"u8.ToArray(), decoded.Body);
    }

    [Fact]
    public void Codec_EnforcesTheBodyCeiling_BothWays()
    {
        var big = new byte[OracleCodec.MaxBodyBytes + 1];

        // Encoding an over-ceiling body is refused before any I/O, with the reason pointing at the Relic rite …
        var request = new OracleRequest { Method = "POST", Path = "/up", Body = big };
        var reqEx = Assert.Throws<ArgumentOutOfRangeException>(() => OracleCodec.EncodeRequest(request));
        Assert.Contains("relic", reqEx.Message);
        Assert.Throws<ArgumentOutOfRangeException>(
            () => OracleCodec.EncodeResponse(OracleResponse.Ok(big, "application/octet-stream")));

        // … and a hostile frame declaring one is refused at decode: fail at the rite, not the transport.
        var w = new CodexWriter();
        w.WriteByte((byte)CupriMarks.Supported(CupriMarks.Oracle).Max);
        w.WriteUInt32(200);
        w.WriteVarUInt(0);                                 // no headers
        w.WriteBytes(big);
        Assert.Throws<CodexFormatException>(() => OracleCodec.DecodeResponse(w.ToArray()));
    }

    [Fact]
    public void Codec_StampsAndChecksTheFrameVersion()
    {
        // Both directions lead with the catalogue version, so a peer that predates the rite (or speaks a version this
        // build has buried) gets a typed refusal rather than a mis-parse.
        var request = OracleCodec.EncodeRequest(OracleRequest.Get("/"));
        var response = OracleCodec.EncodeResponse(OracleResponse.NotFound());
        var expected = (byte)CupriMarks.Supported(CupriMarks.Oracle).Max;
        Assert.Equal(expected, request[0]);
        Assert.Equal(expected, response[0]);

        var future = request.ToArray();
        future[0] = 99;
        Assert.Throws<CodexFormatException>(() => OracleCodec.DecodeRequest(future));

        var futureResponse = response.ToArray();
        futureResponse[0] = 99;
        Assert.Throws<CodexFormatException>(() => OracleCodec.DecodeResponse(futureResponse));
    }

    [Fact]
    public void OracleResponse_NotFound_IsEmpty()
    {
        var decoded = OracleCodec.DecodeResponse(OracleCodec.EncodeResponse(OracleResponse.NotFound()));

        Assert.Equal(404u, decoded.Status);
        Assert.Empty(decoded.Body);
        Assert.Empty(decoded.Headers);
    }

    [Fact]
    public async Task Oracle_Consult_RoundTrips_OverEncryptedSession()
    {
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

        // Shrine host: read one request, answer it.
        var hostTask = Task.Run(async () =>
        {
            var evt = await shrine.ReceiveAsync(ct);
            var request = Assert.IsType<RequestReceived>(evt).Request;
            Assert.Equal("GET", request.Method);
            Assert.Equal("/index.html", request.Path);
            Assert.Equal("text/html", request.Header("accept"));
            await shrine.SendResponseAsync(OracleResponse.Ok("<h1>hi</h1>"u8, "text/html"), ct);
        }, ct);

        var response = await pilgrim.ConsultAsync(
            OracleRequest.Get("/index.html", new OracleHeader("accept", "text/html")), ct);
        await hostTask;

        Assert.Equal(200u, response.Status);
        Assert.Equal("text/html", response.ContentType);
        Assert.Equal("<h1>hi</h1>", response.AsText());
    }
}
