using DotNetBoost.Settings.Core.Interfaces;
using Microsoft.Extensions.Caching.Distributed;
using System.Text;

namespace DotNetBoost.Settings.Core.Services;

/// <summary>
/// Default <see cref="ISettingChangeSignal"/>: signals nothing. Registered by
/// <c>AddSettings()</c> so the service always resolves, and so a single-instance application
/// needs no configuration at all.
/// </summary>
/// <remarks>
/// It always reports <c>null</c>, which always matches the token a cached copy was loaded
/// under, so every cache hit is served exactly as it was before change signalling existed.
/// </remarks>
internal sealed class NullSettingChangeSignal : ISettingChangeSignal
{
    private static readonly Task<string?> NoVersion = Task.FromResult<string?>(null);

    public Task<string?> GetVersionAsync(string groupName, CancellationToken ct = default) => NoVersion;

    public Task SignalChangeAsync(string groupName, CancellationToken ct = default) => Task.CompletedTask;
}

/// <summary>
/// <see cref="ISettingChangeSignal"/> over <see cref="IDistributedCache"/> — Redis, SQL Server,
/// or whatever the application already shares between instances. One small entry per settings
/// group, with no expiry; if the cache evicts one anyway, every instance reloads that group
/// once and carries on.
/// </summary>
/// <param name="cache">A cache every instance of the application shares.</param>
/// <param name="keyPrefix">
/// Prefixes the entry holding each group's token. Make it distinct per application if several
/// share a cache.
/// </param>
public sealed class DistributedCacheSettingChangeSignal(
    IDistributedCache cache,
    string keyPrefix = DistributedCacheSettingChangeSignal.DefaultKeyPrefix)
    : ISettingChangeSignal
{
    /// <summary>The key prefix used when none is given.</summary>
    public const string DefaultKeyPrefix = "DotNetBoost.Settings:version:";

    /// <inheritdoc/>
    public async Task<string?> GetVersionAsync(string groupName, CancellationToken ct = default)
        => Decode(await cache.GetAsync(Key(groupName), ct).ConfigureAwait(false));

    /// <inheritdoc/>
    public Task SignalChangeAsync(string groupName, CancellationToken ct = default)
        => cache.SetAsync(
            Key(groupName),
            Encoding.UTF8.GetBytes(Guid.NewGuid().ToString("N")),
            new DistributedCacheEntryOptions(),
            ct);

    private string Key(string groupName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(groupName);
        return keyPrefix + groupName;
    }

    private static string? Decode(byte[]? value) => value is null ? null : Encoding.UTF8.GetString(value);
}

/// <summary>
/// Publishes a change signal for every completed write. Registered as an
/// <see cref="ISettingWriteObserver"/> rather than called from inside the manager, so there is
/// one notification path rather than two — which is also what makes <c>ClearAsync</c> signal
/// without a second call site.
/// </summary>
/// <remarks>
/// An observer's exception is logged and swallowed, which is the right policy here: the write
/// has already committed, and a signal is an optimisation over the cache duration. A failed
/// signal costs other instances a slower reload, not a lost write.
/// </remarks>
internal sealed class ChangeSignalPublisher(ISettingChangeSignal signal) : ISettingWriteObserver
{
    public Task OnWrittenAsync(SettingWrite write, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(write);
        return signal.SignalChangeAsync(write.GroupName, ct);
    }
}
