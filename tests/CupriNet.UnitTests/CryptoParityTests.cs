using System.Net;
using System.Text;
using CupriNet.Abstractions;
using CupriNet.Alembic;
using CupriNet.Conjunction;
using CupriNet.Core;
using CupriNet.Vessel;
using Xunit;

namespace CupriNet.UnitTests;

/// <summary>
/// Parity: every functional scenario built against the Simulacrum must behave identically once the real
/// BouncyCastle suite is swapped in behind the seam. Each theory runs on BOTH suites.
/// </summary>
public class CryptoParityTests
{
    private static readonly Concordium Network = new("example.chat");
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch.AddYears(56);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    [Theory]
    [MemberData(nameof(CryptoSuites.All), MemberType = typeof(CryptoSuites))]
    public void Signature_RoundTrips(ICryptoSuite suite)
    {
        var seal = suite.GenerateSeal();
        var signer = suite.CreateSigner(seal.PrivateKey);
        var message = "the quick brown fox"u8.ToArray();

        var signature = signer.Sign(message);
        Assert.True(suite.Verifier.Verify(message, signature, signer.PublicKey.Span));
    }

    [Theory]
    [MemberData(nameof(CryptoSuites.All), MemberType = typeof(CryptoSuites))]
    public void Aead_RoundTrips(ICryptoSuite suite)
    {
        var aead = suite.Aead;
        var key = new byte[aead.KeySize];
        var nonce = new byte[aead.NonceSize];
        byte[] plaintext = [1, 2, 3, 4, 5, 6, 7, 8];

        var sealed_ = aead.Seal(key, nonce, plaintext, associatedData: []);
        var opened = aead.Open(key, nonce, sealed_, associatedData: []);

        Assert.NotNull(opened);
        Assert.Equal(plaintext, opened);
    }

    [Theory]
    [MemberData(nameof(CryptoSuites.All), MemberType = typeof(CryptoSuites))]
    public void Intonation_MintThenValidate_IsValid(ICryptoSuite suite)
    {
        var identity = NodeIdentity.Generate(suite);
        var intonation = IntonationMint.Intone(identity, suite, new IntonationOptions
        {
            Network = Network,
            Beacons = [new Beacon(EndpointKind.Host, "127.0.0.1", 43820)],
        }, Now);

        var result = IntonationValidator.ValidateDocument(IntonationCodec.Encode(intonation), Network, suite, Now);
        Assert.Equal(IntonationStatus.Valid, result.Status);
    }

    [Theory]
    [MemberData(nameof(CryptoSuites.All), MemberType = typeof(CryptoSuites))]
    public void GuiseBinding_Verifies(ICryptoSuite suite)
    {
        var me = NodeIdentity.Generate(suite);
        var peer = NodeIdentity.Generate(suite);
        var record = Relationship.Establish(me, peer.Sigil, peer.PublicKey.ToArray(), suite, Now);

        Assert.True(GuiseBinding.Verify(
            suite, me.PublicKey.Span, peer.Sigil, record.GuisePublicKey, record.GuiseBindingSignature));
    }

    [Theory]
    [MemberData(nameof(CryptoSuites.All), MemberType = typeof(CryptoSuites))]
    public async Task Pairing_Authenticates_BothSides(ICryptoSuite suite)
    {
        using var cts = new CancellationTokenSource(Timeout);
        var ct = cts.Token;

        var host = NodeIdentity.Generate(suite);
        var joiner = NodeIdentity.Generate(suite);

        await using var listener = new VesselListener(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Start();
        var intonation = IntonationMint.Intone(host, suite, new IntonationOptions
        {
            Network = Network,
            Beacons = [new Beacon(EndpointKind.Host, "127.0.0.1", listener.LocalEndPoint.Port)],
        }, Now);

        var acceptTask = listener.AcceptAsync(ct);
        await using var joinerVessel = await TcpVessel.ConnectAsync("127.0.0.1", listener.LocalEndPoint.Port, cancellationToken: ct);
        await using var hostVessel = await acceptTask;

        var initiate = ConjunctionHandshake.InitiateAsync(joinerVessel, joiner, Network, suite, expectedPeer: intonation.InviterSigil, cancellationToken: ct);
        var accept = ConjunctionHandshake.AcceptAsync(hostVessel, host, Network, suite, cancellationToken: ct);

        var joinerView = await initiate;
        var hostView = await accept;

        Assert.Equal(host.Sigil, joinerView.PeerSigil);
        Assert.Equal(joiner.Sigil, hostView.PeerSigil);

        await joinerVessel.SendAsync(5, "hi"u8.ToArray(), ct);
        var frame = await hostVessel.ReceiveAsync(ct);
        Assert.Equal("hi", Encoding.UTF8.GetString(frame!.Value.Payload));
    }
}
