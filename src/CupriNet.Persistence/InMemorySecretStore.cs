using System.Collections.Concurrent;
using CupriNet.Abstractions;

namespace CupriNet.Persistence;

/// <summary>An in-memory <see cref="ISecretStore"/> for tests and ephemeral nodes. Not persisted.</summary>
public sealed class InMemorySecretStore : ISecretStore
{
    private readonly ConcurrentDictionary<string, byte[]> _entries = new(StringComparer.Ordinal);

    public ValueTask StoreAsync(string key, ReadOnlyMemory<byte> secret, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        _entries[key] = secret.ToArray();
        return ValueTask.CompletedTask;
    }

    public ValueTask<byte[]?> LoadAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        return ValueTask.FromResult(_entries.TryGetValue(key, out var value) ? (byte[]?)value.ToArray() : null);
    }

    public ValueTask DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        _entries.TryRemove(key, out _);
        return ValueTask.CompletedTask;
    }
}
