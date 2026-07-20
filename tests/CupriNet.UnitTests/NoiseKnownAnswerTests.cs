using CupriNet.Alembic;
using CupriNet.Noise;
using Xunit;

namespace CupriNet.UnitTests;

/// <summary>
/// Cross-implementation known-answer test for Noise_XX_25519_ChaChaPoly_SHA256, using an official vector
/// (from the snow/cacophony corpus). With the vector's fixed static and ephemeral keys and prologue, our
/// implementation must reproduce every handshake and transport message byte-for-byte, proving interop
/// (protocol-name hashing, prologue, HKDF direction, nonce endianness, DH mixing, and cipher split).
/// </summary>
public class NoiseKnownAnswerTests
{
    private const string Prologue = "5468657265206973206e6f20726967687420616e642077726f6e672e2054686572652773206f6e6c792066756e20616e6420626f72696e672e";
    private const string InitStatic = "7dec208517a3b81a2861d7a71266d5d6dc944c5a8816634a86fe63198a0148ee";
    private const string InitEphemeral = "a32daf21e93c0131495ce1d903181fde81cc46937daaeb990bae7c992709421e";
    private const string RespStatic = "4d0aed5098e3b4ef20357e9f686ce66204c792b358da2e475017d6c485304881";
    private const string RespEphemeral = "4eece0f195d026db035ff987597c429d3ad3bcc2944df37d649528951b2a27c5";

    private static readonly string[] Payloads =
    [
        "d03c489139e645d0711a3c9e810d776b46a84912463fafa87b884eebf242dc34",
        "d8190a92f7dc0c93dbea9118ba8055751fb7c6590c416ffbd419964132b99a85",
        "77891b19dcb92ef7c055b672c4a5aa7fdf1c84146b8b303459022729473ce254",
        "d7efdf988072881941db045a42882433817555128fbf5663e56081712ec7d212",
        "dd7bf01a588bafb52c6cfba952e5d8fe35cc2b3f92b4730ae2474615157345ce",
    ];

    private static readonly string[] Ciphertexts =
    [
        "f9fa868ba97ab8a2686deccfaad5a484ee10a5bb85e3d1dce015a84797f92818d03c489139e645d0711a3c9e810d776b46a84912463fafa87b884eebf242dc34",
        "8c4e6fdb7d09d501a86f7eca5c234522751706ed409182c05cdf5f827d4dae47b81c6c5f43b025692c24391eefee725c17d8cb0fbe3e4abb8aedf42c4fd2592d4ea48ac08989d6ae8b4adae08b2c34087c808c7aa55a63c02b0fab9e930612336bd43eaea04d3c670a0a146691aa9cc9d357872320dc735dbc48580cffb553db",
        "933ca6b5ed60df3df66121f0ab49a09e49efa45c613a86a3cecbf4c535cef2f83f72b42837b18e3572f2fdc2b74c331e2368a545cef54bdca081678ab0e9dd5348122459e0c034c851984d88ce610963d43cde6cfe73a67fbd5a63e8bfca96d0",
        "54ef0ff0629e1aaa7685a2806ab111cba76b52331f2642276736f415868eacb69ab2577f3bda0cbf72f879685f6ed25f",
        "356be70f110306d5c699bb834bb9d58d909e325924dfbec972e406e6f294dc63e1daebefe8a62a334facc8048ab4ad66",
    ];

    [Fact]
    public void NoiseXX_MatchesOfficialVector_ByteForByte()
    {
        var suite = CryptoSuites.Secure();
        var dh = suite.Agreement;
        ReadOnlyMemory<byte> prologue = Bytes(Prologue);

        var initiator = new NoiseHandshakeState(suite, initiator: true, StaticKey(dh, InitStatic), prologue, Ephemeral(dh, InitEphemeral));
        var responder = new NoiseHandshakeState(suite, initiator: false, StaticKey(dh, RespStatic), prologue, Ephemeral(dh, RespEphemeral));

        // Handshake messages: init -> resp -> init.
        AssertMessage(initiator, responder, 0);
        AssertMessage(responder, initiator, 1);
        AssertMessage(initiator, responder, 2);

        var initiatorTransport = initiator.Split();
        var responderTransport = responder.Split();

        // Transport messages continue the alternation: resp -> init, then init -> resp.
        Assert.Equal(Ciphertexts[3], Hex(responderTransport.Encrypt(Bytes(Payloads[3]))));
        Assert.Equal(Payloads[3], Hex(initiatorTransport.Decrypt(Bytes(Ciphertexts[3]))));

        Assert.Equal(Ciphertexts[4], Hex(initiatorTransport.Encrypt(Bytes(Payloads[4]))));
        Assert.Equal(Payloads[4], Hex(responderTransport.Decrypt(Bytes(Ciphertexts[4]))));
    }

    private static void AssertMessage(NoiseHandshakeState writer, NoiseHandshakeState reader, int index)
    {
        var message = writer.WriteMessage(Bytes(Payloads[index]));
        Assert.Equal(Ciphertexts[index], Hex(message));
        Assert.Equal(Payloads[index], Hex(reader.ReadMessage(message)));
    }

    private static (byte[] Private, byte[] Public) StaticKey(IKeyAgreement dh, string privateHex)
    {
        var priv = Bytes(privateHex);
        return (priv, dh.DerivePublicKey(priv));
    }

    private static Func<(byte[] Private, byte[] Public)> Ephemeral(IKeyAgreement dh, string privateHex)
        => () => StaticKey(dh, privateHex);

    private static byte[] Bytes(string hex) => Convert.FromHexString(hex);

    private static string Hex(byte[] bytes) => Convert.ToHexStringLower(bytes);
}
