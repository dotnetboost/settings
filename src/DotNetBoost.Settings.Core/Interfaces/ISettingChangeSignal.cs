namespace DotNetBoost.Settings.Core.Interfaces;

/// <summary>
/// Tells every instance of the application that a settings group changed, so each one reloads
/// its cached copy within seconds instead of waiting out
/// <see cref="DotNetBoost.Settings.Core.SettingOptions.CacheDuration"/>.
/// <para>
/// A version token per group, in some store every instance can reach. Every write publishes a
/// new token through <see cref="SignalChangeAsync"/>, and a read compares
/// <see cref="GetVersionAsync"/> against the token its cached copy was loaded under, at most
/// once per <see cref="DotNetBoost.Settings.Core.SettingOptions.ChangeCheckInterval"/>. The
/// built-in implementation uses <c>IDistributedCache</c>
/// (<c>SynchronizeThroughDistributedCache()</c>); register your own with
/// <c>UseChangeSignal&lt;T&gt;()</c>.
/// </para>
/// <para>
/// The default does nothing, and a single-instance application needs no other. Without a real
/// one, an instance only sees another's write when its own cache entry expires. Failures are
/// logged and tolerated on both sides: a signal is an optimisation over the cache duration,
/// never the thing that makes a write durable.
/// </para>
/// </summary>
/// <remarks>
/// This is about cache coherence, and nothing else. <see cref="ISettingChangedHandler{T}"/> and
/// <see cref="ISettingWriteObserver"/> stay in-process by design: a signal makes other
/// instances notice a new <em>value</em>, it does not run their handlers.
/// </remarks>
public interface ISettingChangeSignal
{
    /// <summary>
    /// The current token for <paramref name="groupName"/>, or <c>null</c> when no change has
    /// been signalled for it (or the entry was evicted).
    /// </summary>
    Task<string?> GetVersionAsync(string groupName, CancellationToken ct = default);

    /// <summary>Publishes a new token for <paramref name="groupName"/>.</summary>
    Task SignalChangeAsync(string groupName, CancellationToken ct = default);
}
