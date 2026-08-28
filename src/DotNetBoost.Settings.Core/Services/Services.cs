using DotNetBoost.Settings.Core.Interfaces;
using Microsoft.Extensions.Caching.Memory;
using System.Linq.Expressions;

namespace DotNetBoost.Settings.Core.Services;

internal sealed class SettingAccessor<T> : ISettingAccessor<T> where T : new()
{
    private readonly SettingManager _m;
    internal SettingAccessor(SettingManager m) => _m = m;

    public Task<T> GetAsync(bool refreshCache = false, CancellationToken cancellationToken = default)
        => _m.GetAsync<T>(refreshCache, cancellationToken);

    public Task<TProp> GetAsync<TProp>(Expression<Func<T, TProp>> selector, bool refreshCache = false, CancellationToken cancellationToken = default)
        => _m.GetAsync(selector, refreshCache, cancellationToken);

    public Task SetAsync(T model, CancellationToken cancellationToken = default)
        => _m.SetAsync(model, null, cancellationToken);

    public Task SetAsync(T model, string? expectedVersion, CancellationToken cancellationToken = default)
        => _m.SetAsync(model, expectedVersion, cancellationToken);

    public Task<string> GetVersionAsync(CancellationToken cancellationToken = default)
        => _m.GetVersionAsync<T>(cancellationToken);

    public Task SetAsync<TProp>(Expression<Func<T, TProp>> selector, TProp value, CancellationToken cancellationToken = default)
        => _m.SetAsync(selector, value, cancellationToken);

    public Task<bool> ExistsAsync(bool allProperties = false, CancellationToken cancellationToken = default)
        => _m.ExistsAsync<T>(allProperties, cancellationToken);

    public Task ClearAsync(CancellationToken cancellationToken = default)
        => _m.ClearAsync<T>(cancellationToken);
}

/// <summary>Default in-process cache backed by <see cref="IMemoryCache"/>.</summary>
public sealed class SettingCache : ISettingCache
{
    private readonly IMemoryCache _mc;

    /// <summary>Creates a cache over the supplied <see cref="IMemoryCache"/>.</summary>
    public SettingCache(IMemoryCache mc)
        => _mc = mc ?? throw new ArgumentNullException(nameof(mc));

    /// <inheritdoc/>
    public bool TryGetValue<T>(string key, out T? value)
    {
        if (_mc.TryGetValue(key, out var cached) && cached is T typed)
        {
            value = typed;
            return true;
        }
        value = default;
        return false;
    }

    /// <inheritdoc/>
    public void Set<T>(string key, T value, TimeSpan duration)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(value);
        _mc.Set(key, value, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = duration });
    }

    /// <inheritdoc/>
    public void Remove(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        _mc.Remove(key);
    }
}

/// <summary>
/// Pass-through encryptor. Replace with a real implementation via
/// <c>UseAesEncryption()</c> or <c>UseCustomEncryption&lt;T&gt;()</c>.
/// </summary>
internal sealed class NullSettingEncryptor : ISettingEncryptor
{
    public string Encrypt(string plaintext)  => plaintext;
    public string Decrypt(string ciphertext) => ciphertext;
}
