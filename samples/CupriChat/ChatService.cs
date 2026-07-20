using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Buffers.Text;
using System.Text;
using System.Text.Json;
using CupriNet.Abstractions;
using CupriNet.Arcanum;
using CupriNet.Codex;
using CupriNet.Core;
using CupriNet.Hosting;
using CupriNet.Rites;

namespace CupriChat;

/// <summary>A chat line surfaced to the UI. AuthorId is the sender's Sigil (hex) — used for colour and disambiguation.</summary>
public sealed record ChatMessage(string User, string AuthorId, string Text, DateTimeOffset At, bool IsLocal);

/// <summary>A participant shown in the user list.</summary>
public sealed record UserView(string Id, string Display, bool IsSelf, bool IsDirectPeer);

/// <summary>An incoming file offer awaiting the user's decision.</summary>
public sealed record FileOffer(string TransferId, string FromDisplay, string FileName, long Size);

/// <summary>A completed incoming file.</summary>
public sealed record FileReceipt(string FileName, string SavePath);

/// <summary>The wire form inside an Epistle payload. Id is the author's Sigil (hex) so it survives relaying.</summary>
public sealed record ChatWire(string User, string Id, string Text)
{
    public static string Serialize(ChatWire wire) => JsonSerializer.Serialize(wire);

    public static ChatWire? Deserialize(string json)
    {
        try { return JsonSerializer.Deserialize<ChatWire>(json); }
        catch { return null; }
    }
}

/// <summary>
/// Drives a CupriNet node for CupriChat: pairing, deferred channel Consecration, chat over Epistles, and
/// direct file transfers over the Conduit rite using the Reliquary file protocol. The node relays chat to
/// its other peers (a hub); file transfers go only over the direct session to the chosen peer.
/// </summary>
public sealed class ChatService : IAsyncDisposable
{
    private const string NetworkId = "cuprichat";
    private const uint ReliquaryProtocol = 1;
    private const int ChunkSize = 64 * 1024;

    // Conduit frame flags for the file-transfer protocol.
    private const uint FlagOffer = 1;
    private const uint FlagAccept = 2;
    private const uint FlagDecline = 3;
    private const uint FlagChunk = 4;

    private readonly object _lock = new();
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private readonly List<PairedPeer> _pending = [];
    private readonly List<PeerSession> _sessions = [];
    private readonly Dictionary<string, string?> _users = new(StringComparer.Ordinal);
    private readonly Dictionary<string, OutgoingTransfer> _outgoing = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IncomingOffer> _offers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IncomingTransfer> _incoming = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _cts = new();

    private CupriNode? _node;
    private string _username = "anon";
    private Watchword _channel = ChannelFromName(DefaultChannelName);
    private string _selfId = string.Empty;
    private bool _joined;

    public const string DefaultChannelName = "CupriChat#Public";

    public event Action<ChatMessage>? MessageArrived;
    public event Action<string>? Status;
    public event Action<IReadOnlyList<UserView>>? UsersChanged;
    public event Action<FileOffer>? FileOfferReceived;
    public event Action<FileReceipt>? FileReceived;

    public bool FileTransfersEnabled { get; set; }

    public string SelfShortId => Short(_selfId);

    public string SelfId => _selfId;

    public string Username => _username;

    public async Task StartAsync()
    {
        var localIp = LocalIPv4();
        _node = await CupriNode.CreateAsync(new CupriNodeOptions
        {
            Concordium = NetworkId,
            ListenAddress = IPAddress.Parse(localIp),
        }, _cts.Token);

        _selfId = Convert.ToHexStringLower(_node.Identity.Sigil.Span);
        lock (_lock)
            _users[_selfId] = _username;
        RaiseUsers();

        _ = Task.Run(() => AcceptLoopAsync(_cts.Token));
        Status?.Invoke($"Ready on {localIp}:{_node.LocalEndPoint.Port}");
    }

    public string GenerateLink()
    {
        if (_node is null)
            throw new InvalidOperationException("The node is not started yet.");
        return _node.IntoneUri(TimeSpan.FromHours(24), DateTimeOffset.UtcNow);
    }

    public async Task ConnectAsync(string link)
    {
        if (_node is null)
            return;
        if (!IntonationUri.TryParse(link.Trim(), out var intonation, out _))
        {
            Status?.Invoke("That does not look like a CupriChat link.");
            return;
        }

        var peer = await _node.ConjoinAsync(intonation, DateTimeOffset.UtcNow, _cts.Token);
        Status?.Invoke("Paired with a peer.");
        await OnPeerPairedAsync(peer);
    }

    public void SetIdentity(string username, string channelName)
    {
        _username = string.IsNullOrWhiteSpace(username) ? "anon" : username.Trim();
        _channel = ChannelFromName(string.IsNullOrWhiteSpace(channelName) ? DefaultChannelName : channelName.Trim());
        if (_selfId.Length > 0)
        {
            lock (_lock)
                _users[_selfId] = _username;
            RaiseUsers();
        }
    }

    public void JoinChannel()
    {
        List<PairedPeer> toJoin;
        lock (_lock)
        {
            _joined = true;
            toJoin = [.. _pending];
            _pending.Clear();
        }

        foreach (var peer in toJoin)
            _ = Task.Run(() => ConsecrateAsync(peer, _cts.Token));
    }

    public async Task SendAsync(string text)
    {
        var epistle = Epistle.Text(ChatWire.Serialize(new ChatWire(_username, _selfId, text)), DateTimeOffset.UtcNow);
        lock (_lock)
            _seen.Add(Convert.ToHexStringLower(epistle.MessageId));

        await BroadcastAsync(epistle, except: null, _cts.Token);
        MessageArrived?.Invoke(new ChatMessage(_username, _selfId, text, DateTimeOffset.Now, IsLocal: true));
    }

    // ---- File transfer -------------------------------------------------------------------------

    /// <summary>Offers a file to a directly-connected peer (identified by its Sigil hex).</summary>
    public async Task SendFileAsync(string peerIdHex, string filePath)
    {
        if (_node is null)
            return;

        var session = SessionFor(peerIdHex);
        if (session is null)
        {
            Status?.Invoke("That user is not directly connected — cannot send a file.");
            return;
        }

        var content = await File.ReadAllBytesAsync(filePath, _cts.Token);
        var name = Path.GetFileName(filePath);
        var manifest = ReliquaryBuilder.Build([(name, content)], ChunkSize, _node.Suite);
        var transferId = Convert.ToHexStringLower(manifest.TransferId);

        lock (_lock)
            _outgoing[transferId] = new OutgoingTransfer(transferId, manifest, content, session);

        await session.Conduits.SendAsync(Frame(FlagOffer, ReliquaryCodec.Encode(manifest)), _cts.Token);
        Status?.Invoke($"Offered '{name}' ({FormatSize(content.Length)}). Waiting for the peer to accept…");
    }

    /// <summary>Accepts a pending offer and starts receiving into <paramref name="savePath"/>.</summary>
    public async Task AcceptFileAsync(string transferId, string savePath)
    {
        IncomingOffer? offer;
        lock (_lock)
            _offers.Remove(transferId, out offer);
        if (offer is null || _node is null)
            return;

        var file = offer.Manifest.Files[0];
        lock (_lock)
            _incoming[transferId] = new IncomingTransfer(transferId, file, new ReliquaryAssembler(file, _node.Suite), savePath, offer.Session);

        await offer.Session.Conduits.SendAsync(Frame(FlagAccept, offer.Manifest.TransferId), _cts.Token);
        Status?.Invoke($"Receiving '{file.RelativePath}'…");
    }

    /// <summary>Declines a pending offer.</summary>
    public async Task DeclineFileAsync(string transferId)
    {
        IncomingOffer? offer;
        lock (_lock)
            _offers.Remove(transferId, out offer);
        if (offer is null)
            return;
        await offer.Session.Conduits.SendAsync(Frame(FlagDecline, offer.Manifest.TransferId), _cts.Token);
    }

    private async Task ConduitLoopAsync(PeerSession peer, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var frame = await peer.Session.Conduits.ReceiveAsync(cancellationToken);
                if (frame is null)
                    break;
                if (frame.ProtocolId != ReliquaryProtocol)
                    continue;

                switch (frame.Flags)
                {
                    case FlagOffer:
                        await HandleOfferAsync(peer, frame.Payload, cancellationToken);
                        break;
                    case FlagAccept:
                        await HandleAcceptAsync(frame.Payload, cancellationToken);
                        break;
                    case FlagDecline:
                        HandleDecline(frame.Payload);
                        break;
                    case FlagChunk:
                        await HandleChunkAsync(frame.Payload, cancellationToken);
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Status?.Invoke($"File channel closed: {ex.Message}");
        }
    }

    private async Task HandleOfferAsync(PeerSession peer, byte[] payload, CancellationToken cancellationToken)
    {
        ReliquaryManifest manifest;
        try { manifest = ReliquaryCodec.Decode(payload); }
        catch { return; }
        if (manifest.Files.Count == 0)
            return;

        var transferId = Convert.ToHexStringLower(manifest.TransferId);

        if (!FileTransfersEnabled)
        {
            await peer.Session.Conduits.SendAsync(Frame(FlagDecline, manifest.TransferId), cancellationToken);
            return;
        }

        string fromName;
        lock (_lock)
        {
            _offers[transferId] = new IncomingOffer(transferId, manifest, peer.Session);
            fromName = _users.TryGetValue(Convert.ToHexStringLower(peer.Sigil.Span), out var n) && n is not null ? n : "(peer)";
        }

        var file = manifest.Files[0];
        FileOfferReceived?.Invoke(new FileOffer(transferId, $"{fromName}#{Short(Convert.ToHexStringLower(peer.Sigil.Span))}", file.RelativePath, file.Length));
    }

    private async Task HandleAcceptAsync(byte[] transferIdBytes, CancellationToken cancellationToken)
    {
        var transferId = Convert.ToHexStringLower(transferIdBytes);
        OutgoingTransfer? transfer;
        lock (_lock)
            _outgoing.TryGetValue(transferId, out transfer);
        if (transfer is null)
            return;

        var file = transfer.Manifest.Files[0];
        for (var i = 0; i < file.ChunkCount; i++)
        {
            var start = i * file.ChunkSize;
            var length = Math.Min(file.ChunkSize, transfer.Content.Length - start);
            var w = new CodexWriter();
            w.WriteBytes(transfer.Manifest.TransferId);
            w.WriteVarUInt((ulong)i);
            w.WriteBytes(transfer.Content.AsSpan(start, length));
            await transfer.Session.Conduits.SendAsync(Frame(FlagChunk, w.ToArray()), cancellationToken);
        }

        lock (_lock)
            _outgoing.Remove(transferId);
        Status?.Invoke($"Sent '{file.RelativePath}'.");
    }

    private void HandleDecline(byte[] transferIdBytes)
    {
        var transferId = Convert.ToHexStringLower(transferIdBytes);
        lock (_lock)
            _outgoing.Remove(transferId);
        Status?.Invoke("The peer declined the file.");
    }

    private async Task HandleChunkAsync(byte[] payload, CancellationToken cancellationToken)
    {
        var reader = new CodexReader(payload);
        var transferId = Convert.ToHexStringLower(reader.ReadBytes());
        var chunkIndex = (int)reader.ReadVarUInt();
        var data = reader.ReadBytes();

        IncomingTransfer? transfer;
        lock (_lock)
            _incoming.TryGetValue(transferId, out transfer);
        if (transfer is null)
            return;

        if (!transfer.Assembler.AcceptChunk(chunkIndex, data))
            return;

        if (!transfer.Assembler.IsComplete)
            return;

        byte[] bytes;
        try { bytes = transfer.Assembler.Assemble(); }
        catch (Exception ex) { Status?.Invoke($"File failed verification: {ex.Message}"); return; }

        await File.WriteAllBytesAsync(transfer.SavePath, bytes, cancellationToken);
        lock (_lock)
            _incoming.Remove(transferId);
        FileReceived?.Invoke(new FileReceipt(Path.GetFileName(transfer.SavePath), transfer.SavePath));
    }

    // ---- Pairing / channel ---------------------------------------------------------------------

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        if (_node is null)
            return;

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var peer = await _node.AcceptAsync(cancellationToken);
                Status?.Invoke("A peer paired with us.");
                await OnPeerPairedAsync(peer);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Status?.Invoke($"Connection dropped: {ex.Message}");
            }
        }
    }

    private async Task OnPeerPairedAsync(PairedPeer peer)
    {
        bool joined;
        lock (_lock)
        {
            joined = _joined;
            if (!joined)
                _pending.Add(peer);
        }

        if (joined)
            await ConsecrateAsync(peer, _cts.Token);
    }

    private async Task ConsecrateAsync(PairedPeer peer, CancellationToken cancellationToken)
    {
        try
        {
            Status?.Invoke("Joining channel with a peer…");
            var session = await _node!.ConsecrateAsync(peer, _channel, DateTimeOffset.UtcNow, cancellationToken);
            var peerSession = new PeerSession(peer.PeerSigil, session);

            lock (_lock)
            {
                _sessions.Add(peerSession);
                _users.TryAdd(Convert.ToHexStringLower(peer.PeerSigil.Span), null);
            }
            RaiseUsers();
            Status?.Invoke("A peer joined the channel.");
            _ = Task.Run(() => ReceiveLoopAsync(peerSession, cancellationToken));
            _ = Task.Run(() => ConduitLoopAsync(peerSession, cancellationToken));
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Status?.Invoke($"Could not join channel with a peer: {ex.Message}");
            await peer.DisposeAsync();
        }
    }

    private async Task ReceiveLoopAsync(PeerSession peer, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var received = await peer.Session.Epistles.ReceiveAsync(cancellationToken);
                if (received is null)
                    break;
                if (received is not MessageReceived message)
                    continue;

                bool isNew;
                lock (_lock)
                    isNew = _seen.Add(Convert.ToHexStringLower(message.Epistle.MessageId));
                if (!isNew)
                    continue;

                var wire = ChatWire.Deserialize(message.Epistle.AsText());
                if (wire is null)
                    continue;

                UpdateUser(wire.Id, wire.User);
                MessageArrived?.Invoke(new ChatMessage(wire.User, wire.Id, wire.Text, DateTimeOffset.Now, IsLocal: false));
                await BroadcastAsync(message.Epistle, except: peer.Session, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Status?.Invoke($"A peer disconnected: {ex.Message}");
        }
        finally
        {
            lock (_lock)
                _sessions.RemoveAll(p => ReferenceEquals(p.Session, peer.Session));
            RaiseUsers();
            await peer.Session.DisposeAsync();
        }
    }

    private async Task BroadcastAsync(Epistle epistle, ArcanumSession? except, CancellationToken cancellationToken)
    {
        List<ArcanumSession> targets;
        lock (_lock)
            targets = _sessions.Where(p => !ReferenceEquals(p.Session, except)).Select(p => p.Session).ToList();

        foreach (var session in targets)
        {
            try { await session.Epistles.SendMessageAsync(epistle, cancellationToken); }
            catch { }
        }
    }

    private ArcanumSession? SessionFor(string peerIdHex)
    {
        lock (_lock)
            return _sessions.FirstOrDefault(p => Convert.ToHexStringLower(p.Sigil.Span) == peerIdHex)?.Session;
    }

    private void UpdateUser(string id, string name)
    {
        lock (_lock)
            _users[id] = name;
        RaiseUsers();
    }

    private void RaiseUsers()
    {
        List<UserView> snapshot;
        lock (_lock)
        {
            var directIds = _sessions.Select(p => Convert.ToHexStringLower(p.Sigil.Span)).ToHashSet(StringComparer.Ordinal);
            snapshot = _users
                .Select(kv => new UserView(kv.Key, FormatUser(kv.Key, kv.Value), kv.Key == _selfId, directIds.Contains(kv.Key)))
                .OrderByDescending(u => u.IsSelf)
                .ThenBy(u => u.Display, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        UsersChanged?.Invoke(snapshot);
    }

    private string FormatUser(string id, string? name)
    {
        var display = $"{name ?? "(joining…)"}#{Short(id)}";
        return id == _selfId ? $"{display} (you)" : display;
    }

    private static ConduitFrame Frame(uint flags, byte[] payload)
        => new() { ProtocolId = ReliquaryProtocol, SchemaVersion = 1, Flags = flags, Payload = payload };

    private static string Short(string idHex) => idHex.Length >= 6 ? idHex[..6] : idHex;

    private static string FormatSize(long bytes)
        => bytes < 1024 ? $"{bytes} B" : bytes < 1024 * 1024 ? $"{bytes / 1024.0:0.#} KB" : $"{bytes / (1024.0 * 1024):0.#} MB";

    private static Watchword ChannelFromName(string name)
    {
        var seed = Encoding.UTF8.GetBytes("cuprichat/channel/" + name.ToLowerInvariant());
        var salt = SHA256.HashData(seed).AsSpan(0, 16).ToArray();
        var code = $"channel#{Base64Url.EncodeToString(salt)}";
        if (!Watchword.TryParse(code, out var watchword))
            throw new InvalidOperationException("Failed to derive the channel watchword.");
        return watchword;
    }

    private static string LocalIPv4()
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Connect("8.8.8.8", 65530);
            return ((IPEndPoint)socket.LocalEndPoint!).Address.ToString();
        }
        catch
        {
            return "127.0.0.1";
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        List<PeerSession> sessions;
        lock (_lock)
            sessions = [.. _sessions];
        foreach (var peer in sessions)
            await peer.Session.DisposeAsync();
        if (_node is not null)
            await _node.DisposeAsync();
        _cts.Dispose();
    }

    private sealed record PeerSession(Sigil Sigil, ArcanumSession Session);

    private sealed record OutgoingTransfer(string Id, ReliquaryManifest Manifest, byte[] Content, ArcanumSession Session);

    private sealed record IncomingOffer(string Id, ReliquaryManifest Manifest, ArcanumSession Session);

    private sealed record IncomingTransfer(string Id, ReliquaryFile File, ReliquaryAssembler Assembler, string SavePath, ArcanumSession Session);
}
