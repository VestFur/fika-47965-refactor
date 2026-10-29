using SPTarkov.DI.Annotations;

namespace FikaServer.Services;

[Injectable(InjectionType.Singleton)]
public class ClientModHashesService
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, uint> _hashes = new();

    public bool MatchesOrAdd(string pluginId, uint hash)
    {
        // The first concurrent request establishes the baseline. Every other
        // request must compare with that same value, including a losing insert.
        return _hashes.GetOrAdd(pluginId, hash) == hash;
    }

    public int GetLength()
    {
        return _hashes.Count;
    }

    public bool Exists(string pluginId)
    {
        return _hashes.ContainsKey(pluginId);
    }

    public uint GetHash(string pluginId)
    {
        return _hashes[pluginId];
    }

    public void AddHash(string pluginId, uint hash)
    {
        if (!_hashes.TryAdd(pluginId, hash))
            throw new ArgumentException("A hash is already registered for " + pluginId, nameof(pluginId));
    }
}
