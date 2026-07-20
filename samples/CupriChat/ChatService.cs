using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Buffers.Text;
using System.Text;
using System.Text.Json;
using CupriNet.Abstractions;
using CupriNet.Arcanum;
using CupriNet.Core;
using CupriNet.Hosting;
using CupriNet.Rites;

namespace CupriChat;

/// <summary>A chat line surfaced to the UI. ShortId is a hash of the author's identity (their key).</summary>
public sealed record ChatMessage(string User, string ShortId, string Text, DateTimeOffset At, bool IsLocal);

/// <summary>A participant shown in the user list.</summary>
public sealed record UserView(string Display, bool IsSelf);

/// <summary>The wire form inside an Epistle payload. Id is the author's Sigil (hex) so it survives relaying.</summary>
public sealed record ChatWire(string User, string Id, string Text)
{
    public static string Serialize(ChatWire wire) => JsonSerializer.Serialize(wire);

    public static ChatWire? Deserialize(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<ChatWire>(json);
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>
/// Drives a CupriNet node for CupriChat. Pairing (Step 1) is separate from joining a channel (Step 2/3):
/// peers pair immediately, but Consecration of the chosen channel is deferred until the user joins, so
/// both sides Consecrate the same channel around the same time. The node relays received messages to its
/// other peers (an application-layer hub); the author's identity travels inside the message so relayed
/// lines keep their real sender. Users are keyed by Sigil, so identical display names stay distinct.
/// </summary>
public sealed class ChatService : IAsyncDisposable
{
    private const string NetworkId = "cuprichat";

    private readonly object _lock = new();
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private readonly List<PairedPeer> _pending = [];
    private readonly List<ArcanumSession> _sessions = [];
    private readonly Dictionary<string, string?> _users = new(StringComparer.Ordinal); // sigil hex -> display name
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

    /// <summary>This node's own identity hash (short), available after Start.</summary>
    public string SelfShortId => _selfId.Length >= 6 ? _selfId[..6] : _selfId;

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

    /// <summary>Step 1: mint an invite link others can use to connect.</summary>
    public string GenerateLink()
    {
        if (_node is null)
            throw new InvalidOperationException("The node is not started yet.");
        return _node.IntoneUri(TimeSpan.FromHours(24), DateTimeOffset.UtcNow);
    }

    /// <summary>Step 1: pair with an invite link (channel Consecration is deferred to Join).</summary>
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

    /// <summary>Step 2: set the display name and channel (both peers must use the same channel name).</summary>
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

    /// <summary>Step 3: join the channel — Consecrate every pending pair (in the background).</summary>
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

    /// <summary>Sends a chat message to every connected peer.</summary>
    public async Task SendAsync(string text)
    {
        var epistle = Epistle.Text(ChatWire.Serialize(new ChatWire(_username, _selfId, text)), DateTimeOffset.UtcNow);
        lock (_lock)
            _seen.Add(Convert.ToHexStringLower(epistle.MessageId));

        await BroadcastAsync(epistle, except: null, _cts.Token);
        MessageArrived?.Invoke(new ChatMessage(_username, SelfShortId, text, DateTimeOffset.Now, IsLocal: true));
    }

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
            var peerId = Convert.ToHexStringLower(peer.PeerSigil.Span);

            lock (_lock)
            {
                _sessions.Add(session);
                _users.TryAdd(peerId, null); // name learned when they speak
            }
            RaiseUsers();
            Status?.Invoke("A peer joined the channel.");
            _ = Task.Run(() => ReceiveLoopAsync(session, cancellationToken));
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

    private async Task ReceiveLoopAsync(ArcanumSession session, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var received = await session.Epistles.ReceiveAsync(cancellationToken);
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
                MessageArrived?.Invoke(new ChatMessage(wire.User, Short(wire.Id), wire.Text, DateTimeOffset.Now, IsLocal: false));

                // Hub: relay to the other peers so everyone connected through us sees it.
                await BroadcastAsync(message.Epistle, except: session, cancellationToken);
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
                _sessions.Remove(session);
            await session.DisposeAsync();
        }
    }

    private async Task BroadcastAsync(Epistle epistle, ArcanumSession? except, CancellationToken cancellationToken)
    {
        List<ArcanumSession> targets;
        lock (_lock)
            targets = _sessions.Where(s => !ReferenceEquals(s, except)).ToList();

        foreach (var session in targets)
        {
            try
            {
                await session.Epistles.SendMessageAsync(epistle, cancellationToken);
            }
            catch
            {
                // failed peers are cleaned up by their own receive loop
            }
        }
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
            snapshot = _users
                .Select(kv => new UserView(FormatUser(kv.Key, kv.Value), kv.Key == _selfId))
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

    private static string Short(string idHex) => idHex.Length >= 6 ? idHex[..6] : idHex;

    // Deterministic Watchword from a friendly channel name — same name yields the same channel keys.
    private static Watchword ChannelFromName(string name)
    {
        var seed = Encoding.UTF8.GetBytes("cuprichat/channel/" + name.ToLowerInvariant());
        var salt = SHA256.HashData(seed).AsSpan(0, 16).ToArray();
        var code = $"channel#{Base64Url.EncodeToString(salt)}";
        if (!Watchword.TryParse(code, out var watchword))
            throw new InvalidOperationException("Failed to derive the channel watchword.");
        return watchword;
    }

    // Best-effort LAN IPv4 so invite links are reachable on the local network.
    private static string LocalIPv4()
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Connect("8.8.8.8", 65530); // selects the outbound interface; no packets are sent
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
        List<ArcanumSession> sessions;
        lock (_lock)
            sessions = [.. _sessions];
        foreach (var session in sessions)
            await session.DisposeAsync();
        if (_node is not null)
            await _node.DisposeAsync();
        _cts.Dispose();
    }
}
