using System.Text;
using CupriNet.Noise;
using Xunit;

namespace CupriNet.UnitTests;

public class NoiseTests
{
    // RFC 7748 §6.1 known-answer vectors for X25519.
    private const string AlicePriv = "77076d0a7318a57d3c16c17251b26645df4c2f87ebc0992ab177fba51db92c2a";
    private const string AlicePub = "8520f0098930a754748b7ddcb43ef75a0dbf3a0d26381af4eba4a98eaa9b4e6a";
    private const string BobPriv = "5dab087e624a8a4b79e17f8b83800ee66f3bb1292618b6fd1c2f8b27ff88e0eb";
    private const string BobPub = "de9edb7d7b7dc1b4d35b61c2ece435373f8343c85b78674dadfc7e146f882b4f";
    private const string Shared = "4a5d9d5ba4ce2de1728e3bf480350f25e07e21c947d19e3376f09b3c1e161742";

    [Fact]
    public void X25519_MatchesRfc7748_KnownAnswers()
    {
        var dh = CryptoSuites.Secure().Agreement;

        Assert.Equal(AlicePub, Hex(dh.DerivePublicKey(Bytes(AlicePriv))));
        Assert.Equal(BobPub, Hex(dh.DerivePublicKey(Bytes(BobPriv))));
        Assert.Equal(Shared, Hex(dh.Agree(Bytes(AlicePriv), Bytes(BobPub))));
        Assert.Equal(Shared, Hex(dh.Agree(Bytes(BobPriv), Bytes(AlicePub))));
    }

    [Fact]
    public void Simulacrum_DoesNotSupportAgreement()
    {
        Assert.Throws<NotSupportedException>(() => CryptoSuites.Simulacrum().Agreement.Generate());
    }

    [Fact]
    public void NoiseXX_CompletesMutually_AndDerivesSharedTransport()
    {
        var suite = CryptoSuites.Secure();
        var initiatorStatic = suite.Agreement.Generate();
        var responderStatic = suite.Agreement.Generate();

        var initiator = new NoiseHandshakeState(suite, initiator: true, initiatorStatic);
        var responder = new NoiseHandshakeState(suite, initiator: false, responderStatic);

        // -> e
        responder.ReadMessage(initiator.WriteMessage());
        // <- e, ee, s, es
        initiator.ReadMessage(responder.WriteMessage());
        // -> s, se  (with a payload to prove in-handshake payloads work)
        var handshakePayload = responder.ReadMessage(initiator.WriteMessage("greetings"u8));

        Assert.Equal("greetings", Encoding.UTF8.GetString(handshakePayload));
        Assert.True(initiator.HandshakeComplete);
        Assert.True(responder.HandshakeComplete);

        var initiatorTransport = initiator.Split();
        var responderTransport = responder.Split();

        // Both sides agree on the handshake hash (channel binding).
        Assert.Equal(initiatorTransport.HandshakeHash, responderTransport.HandshakeHash);

        // Each side learned the other's static key (mutual authentication).
        Assert.Equal(responderStatic.PublicKey, initiatorTransport.RemoteStaticPublicKey);
        Assert.Equal(initiatorStatic.PublicKey, responderTransport.RemoteStaticPublicKey);

        // Transport works in both directions and is really encrypted.
        var ciphertext = initiatorTransport.Encrypt("hello"u8);
        Assert.False(ciphertext.AsSpan(0, 5).SequenceEqual("hello"u8));
        Assert.Equal("hello", Encoding.UTF8.GetString(responderTransport.Decrypt(ciphertext)));

        var back = responderTransport.Encrypt("hi back"u8);
        Assert.Equal("hi back", Encoding.UTF8.GetString(initiatorTransport.Decrypt(back)));
    }

    [Fact]
    public void NoiseXX_TamperedHandshakeMessage_IsRejected()
    {
        var suite = CryptoSuites.Secure();
        var initiator = new NoiseHandshakeState(suite, true, suite.Agreement.Generate());
        var responder = new NoiseHandshakeState(suite, false, suite.Agreement.Generate());

        responder.ReadMessage(initiator.WriteMessage());
        var m1 = responder.WriteMessage();
        m1[^1] ^= 0xFF; // corrupt the authenticated payload

        Assert.Throws<NoiseException>(() => initiator.ReadMessage(m1));
    }

    [Fact]
    public void NoiseXX_TransportRejectsTamperedCiphertext()
    {
        var suite = CryptoSuites.Secure();
        var initiator = new NoiseHandshakeState(suite, true, suite.Agreement.Generate());
        var responder = new NoiseHandshakeState(suite, false, suite.Agreement.Generate());
        responder.ReadMessage(initiator.WriteMessage());
        initiator.ReadMessage(responder.WriteMessage());
        responder.ReadMessage(initiator.WriteMessage());

        var ti = initiator.Split();
        var tr = responder.Split();
        var ct = (byte[])ti.Encrypt("secret"u8).Clone();
        ct[^1] ^= 0xFF;

        Assert.Throws<NoiseException>(() => tr.Decrypt(ct));
    }

    private static byte[] Bytes(string hex) => Convert.FromHexString(hex);

    private static string Hex(byte[] bytes) => Convert.ToHexStringLower(bytes);
}
