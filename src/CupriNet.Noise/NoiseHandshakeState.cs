using CupriNet.Alembic;

namespace CupriNet.Noise;

/// <summary>
/// The post-handshake transport: two CipherStates (one per direction) plus the peer's static public key
/// (its identity in the Noise sense) and the final handshake hash (a channel binding).
/// </summary>
public sealed class NoiseTransport
{
    private readonly NoiseCipherState _send;
    private readonly NoiseCipherState _receive;

    internal NoiseTransport(NoiseCipherState send, NoiseCipherState receive, byte[] remoteStaticPublicKey, byte[] handshakeHash)
    {
        _send = send;
        _receive = receive;
        RemoteStaticPublicKey = remoteStaticPublicKey;
        HandshakeHash = handshakeHash;
    }

    /// <summary>The peer's static public key, learned during the handshake.</summary>
    public byte[] RemoteStaticPublicKey { get; }

    /// <summary>The final handshake hash — a unique binding for this session.</summary>
    public byte[] HandshakeHash { get; }

    public byte[] Encrypt(ReadOnlySpan<byte> plaintext) => _send.EncryptWithAd([], plaintext);

    public byte[] Decrypt(ReadOnlySpan<byte> ciphertext) => _receive.DecryptWithAd([], ciphertext);
}

/// <summary>
/// A Noise_XX_25519_ChaChaPoly_SHA256 handshake. XX gives mutual authentication (both static keys are
/// exchanged and encrypted), identity hiding for the initiator, and forward secrecy. Drive it by
/// alternating <see cref="WriteMessage"/> / <see cref="ReadMessage"/>; when complete, call
/// <see cref="Split"/> for the transport ciphers.
/// </summary>
public sealed class NoiseHandshakeState
{
    private const string ProtocolName = "Noise_XX_25519_ChaChaPoly_SHA256";
    private const int DhLen = 32;

    private enum Token
    {
        E,
        S,
        EE,
        ES,
        SE,
        SS,
    }

    // XX message patterns, in order.
    private static readonly Token[][] MessagePatterns =
    [
        [Token.E],
        [Token.E, Token.EE, Token.S, Token.ES],
        [Token.S, Token.SE],
    ];

    private readonly ICryptoSuite _suite;
    private readonly IKeyAgreement _dh;
    private readonly NoiseSymmetricState _symmetric;
    private readonly bool _initiator;
    private readonly (byte[] Private, byte[] Public) _staticKey;
    private readonly Func<(byte[] Private, byte[] Public)> _generateEphemeral;

    private (byte[] Private, byte[] Public)? _ephemeral;
    private byte[]? _remoteStatic;
    private byte[]? _remoteEphemeral;
    private int _messageIndex;

    /// <summary>Creates a handshake for the given role and long-term static key pair.</summary>
    /// <param name="ephemeralFactory">
    /// Source of ephemeral key pairs. Leave null in production (uses the suite's secure generator);
    /// tests inject a fixed pair to reproduce known-answer vectors.
    /// </param>
    public NoiseHandshakeState(
        ICryptoSuite suite, bool initiator, (byte[] Private, byte[] Public) staticKey,
        ReadOnlyMemory<byte> prologue = default,
        Func<(byte[] Private, byte[] Public)>? ephemeralFactory = null)
    {
        _suite = suite ?? throw new ArgumentNullException(nameof(suite));
        _dh = suite.Agreement;
        _initiator = initiator;
        _staticKey = staticKey;
        _generateEphemeral = ephemeralFactory ?? _dh.Generate;
        _symmetric = new NoiseSymmetricState(suite, ProtocolName);
        _symmetric.MixHash(prologue.Span); // empty by default
    }

    /// <summary>True once all handshake messages have been processed.</summary>
    public bool HandshakeComplete => _messageIndex >= MessagePatterns.Length;

    /// <summary>The peer's static public key, available after it has been received.</summary>
    public byte[]? RemoteStaticPublicKey => _remoteStatic;

    /// <summary>Writes the next handshake message, embedding an optional payload.</summary>
    public byte[] WriteMessage(ReadOnlySpan<byte> payload = default)
    {
        EnsureTurn(writing: true);
        var buffer = new List<byte>();

        foreach (var token in MessagePatterns[_messageIndex])
        {
            switch (token)
            {
                case Token.E:
                    _ephemeral = _generateEphemeral();
                    buffer.AddRange(_ephemeral.Value.Public);
                    _symmetric.MixHash(_ephemeral.Value.Public);
                    break;
                case Token.S:
                    buffer.AddRange(_symmetric.EncryptAndHash(_staticKey.Public));
                    break;
                default:
                    MixDh(token);
                    break;
            }
        }

        buffer.AddRange(_symmetric.EncryptAndHash(payload));
        _messageIndex++;
        return buffer.ToArray();
    }

    /// <summary>Reads the next handshake message, returning any embedded payload.</summary>
    public byte[] ReadMessage(ReadOnlySpan<byte> message)
    {
        EnsureTurn(writing: false);
        var offset = 0;

        foreach (var token in MessagePatterns[_messageIndex])
        {
            switch (token)
            {
                case Token.E:
                    if (offset + DhLen > message.Length)
                        throw new NoiseException("Handshake message is too short for the ephemeral key.");
                    _remoteEphemeral = message.Slice(offset, DhLen).ToArray();
                    offset += DhLen;
                    _symmetric.MixHash(_remoteEphemeral);
                    break;
                case Token.S:
                    var length = _symmetric.HasKey ? DhLen + 16 : DhLen;
                    if (offset + length > message.Length)
                        throw new NoiseException("Handshake message is too short for the static key.");
                    _remoteStatic = _symmetric.DecryptAndHash(message.Slice(offset, length));
                    offset += length;
                    break;
                default:
                    MixDh(token);
                    break;
            }
        }

        var payload = _symmetric.DecryptAndHash(message[offset..]);
        _messageIndex++;
        return payload;
    }

    /// <summary>After the handshake completes, derives the transport ciphers for this role.</summary>
    public NoiseTransport Split()
    {
        if (!HandshakeComplete)
            throw new NoiseException("Handshake is not complete.");
        if (_remoteStatic is null)
            throw new NoiseException("Remote static key was not established.");

        var (first, second) = _symmetric.Split();
        // Initiator sends with the first cipher and receives with the second; responder is the mirror.
        var send = _initiator ? first : second;
        var receive = _initiator ? second : first;
        return new NoiseTransport(send, receive, _remoteStatic, _symmetric.HandshakeHash);
    }

    private void MixDh(Token token)
    {
        var dh = token switch
        {
            Token.EE => _dh.Agree(Ephemeral().Private, RemoteEphemeral()),
            Token.ES => _initiator ? _dh.Agree(Ephemeral().Private, RemoteStatic()) : _dh.Agree(_staticKey.Private, RemoteEphemeral()),
            Token.SE => _initiator ? _dh.Agree(_staticKey.Private, RemoteEphemeral()) : _dh.Agree(Ephemeral().Private, RemoteStatic()),
            Token.SS => _dh.Agree(_staticKey.Private, RemoteStatic()),
            _ => throw new NoiseException($"Unexpected token {token}."),
        };
        _symmetric.MixKey(dh);
    }

    private void EnsureTurn(bool writing)
    {
        if (HandshakeComplete)
            throw new NoiseException("Handshake is already complete.");
        var initiatorTurn = _messageIndex % 2 == 0;
        var myTurn = initiatorTurn == _initiator;
        if (myTurn != writing)
            throw new NoiseException(writing ? "Not this party's turn to write." : "Not this party's turn to read.");
    }

    private (byte[] Private, byte[] Public) Ephemeral()
        => _ephemeral ?? throw new NoiseException("Local ephemeral key is not set.");

    private byte[] RemoteEphemeral() => _remoteEphemeral ?? throw new NoiseException("Remote ephemeral key is not set.");

    private byte[] RemoteStatic() => _remoteStatic ?? throw new NoiseException("Remote static key is not set.");
}
