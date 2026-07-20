using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Buffers.Text;
using System.Text;
using System.Text.Json;
using CupriNet.Arcanum;
using CupriNet.Core;
using CupriNet.Hosting;
using CupriNet.Rites;

namespace CupriChat;

/// <summary>A chat line surfaced to the UI.</summary>
public sealed record ChatMessage(string User, string Text, DateTimeOffset At, bool IsLocal);

/// <summary>The wire form of a chat message inside an Epistle payload.</summary>
public sealed record ChatWire(string User, string Text)
{
    public static string Serialize(ChatWire wire) => JsonSerializer.Serialize(wire);

    public static ChatWire Deserialize(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<ChatWire>(json) ?? new ChatWire("?", json);
        }
        catch
        {
            return new ChatWire("?", json);
        }
    }
}

/// <summary>
/// Drives a CupriNet node for CupriChat: it hosts a node, mints invite links, connects to links, accepts
/// inbound peers, Consecrates the shared "CupriChat#Public" channel with each, and exchanges Epistles.
/// The node relays received messages to its other peers at the application layer (a hub), so everyone
/// connected through this instance sees each other's messages. Duplicate delivery is suppressed by MessageId.
/// </summary>
public sealed class ChatService : IAsyncDisposable
{
    private const string NetworkId = "cuprichat";

    private readonly List<ArcanumSession> _sessions = [];
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private readonly object _lock = new();
    private readonly Watchword _publicChannel = PublicChannelWatchword();
    private readonly CancellationTokenSource _cts = new();

    private CupriNode? _node;

    /// <summary>Display name attached to outgoing messages.</summary>
    public string Username { get; set; } = "anon";

    /// <summary>Raised when a message should be shown (local or remote).</summary>
    public event Action<ChatMessage>? MessageArrived;

    /// <summary>Raised with human-readable status updates.</summary>
    public event Action<string>? Status;

    /// <summary>The friendly label for the default channel.</summary>
    public static string PublicChannelName => "CupriChat#Public";

    public async Task StartAsync()
    {
        var localIp = LocalIPv4();
        _node = await CupriNode.CreateAsync(new CupriNodeOptions
        {
            Concordium = NetworkId,
            ListenAddress = IPAddress.Parse(localIp),
        }, _cts.Token);

        _ = Task.Run(() => AcceptLoopAsync(_cts.Token));
        Status?.Invoke($"Ready on {localIp}:{_node.LocalEndPoint.Port} — channel {PublicChannelName}");
    }

    /// <summary>Mints an invite link others can use to connect.</summary>
    public string GenerateLink()
    {
        if (_node is null)
            throw new InvalidOperationException("The node is not started yet.");
        return _node.IntoneUri(TimeSpan.FromHours(24), DateTimeOffset.UtcNow);
    }

    /// <summary>Connects to an invite link and joins the public channel with that peer.</summary>
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
        var session = await _node.ConsecrateAsync(peer, _publicChannel, DateTimeOffset.UtcNow, _cts.Token);
        AddSession(session);
        Status?.Invoke("Connected — joined the public channel.");
    }

    /// <summary>Sends a chat message to every connected peer.</summary>
    public async Task SendAsync(string text)
    {
        var epistle = Epistle.Text(ChatWire.Serialize(new ChatWire(Username, text)), DateTimeOffset.UtcNow);
        lock (_lock)
            _seen.Add(Key(epistle.MessageId));

        await BroadcastAsync(epistle, except: null, _cts.Token);
        MessageArrived?.Invoke(new ChatMessage(Username, text, DateTimeOffset.Now, IsLocal: true));
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
                var session = await _node.ConsecrateAsync(peer, _publicChannel, DateTimeOffset.UtcNow, cancellationToken);
                AddSession(session);
                Status?.Invoke("A peer joined the public channel.");
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

    private void AddSession(ArcanumSession session)
    {
        lock (_lock)
            _sessions.Add(session);
        _ = Task.Run(() => ReceiveLoopAsync(session, _cts.Token));
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
                    isNew = _seen.Add(Key(message.Epistle.MessageId));
                if (!isNew)
                    continue;

                var wire = ChatWire.Deserialize(message.Epistle.AsText());
                MessageArrived?.Invoke(new ChatMessage(wire.User, wire.Text, DateTimeOffset.Now, IsLocal: false));

                // Act as a hub: relay to every other connected peer (application-layer, not L2 transport).
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
                // a failed peer is cleaned up by its own receive loop
            }
        }
    }

    private static string Key(byte[] messageId) => Convert.ToHexStringLower(messageId);

    // A deterministic Watchword for the public channel, so every CupriChat client derives the same
    // channel keys from the friendly name "CupriChat#Public".
    private static Watchword PublicChannelWatchword()
    {
        var salt = SHA256.HashData(Encoding.UTF8.GetBytes("CupriChat#Public/v1")).AsSpan(0, 16).ToArray();
        var code = $"CupriChat#{Base64Url.EncodeToString(salt)}";
        if (!Watchword.TryParse(code, out var watchword))
            throw new InvalidOperationException("Failed to derive the public channel watchword.");
        return watchword;
    }

    // Best-effort local LAN IPv4 (the route to the internet), so invite links are reachable on the LAN.
    private static string LocalIPv4()
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Connect("8.8.8.8", 65530); // no packets are sent; this just selects the outbound interface
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
