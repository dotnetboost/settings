namespace DotNetBoost.Settings.Core.Interfaces;

/// <summary>
/// Abstraction over an in-memory or distributed cache layer.
/// Implement this to plug in Redis, Garnet, or any other cache.
/// </summary>
public interface ISettingCache
{
    /// <summary>
    /// Retrieves a cached value. Returns <c>false</c> on a miss, or when the cached entry is
    /// not of type <typeparamref name="T"/>.
    /// </summary>
    bool TryGetValue<T>(string key, out T? value);

    /// <summary>Stores a value, expiring it after <paramref name="duration"/>.</summary>
    void Set<T>(string key, T value, TimeSpan duration);

    /// <summary>Evicts a key. Succeeds silently when it is not cached.</summary>
    void Remove(string key);
}
