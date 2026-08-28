using DotNetBoost.Settings.Core.Interfaces;
using DotNetBoost.Settings.Core.Models;
using DotNetBoost.Settings.Core.Services;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace DotNetBoost.Settings.UnitTests;

/// <summary>
/// Stampede protection has to hold across <c>SettingManager</c> instances, because
/// <c>ISettingManager</c> is registered scoped — one instance per request — while the cache
/// it guards is a singleton. Each test here uses a distinct settings type so the shared
/// static lock table cannot couple them.
/// </summary>
public class CacheStampedeTests
{
    [Fact]
    public async Task ConcurrentReads_AcrossManagerInstances_HitTheStoreOnce()
    {
        using var memory = new MemoryCache(new MemoryCacheOptions());
        var cache = new SettingCache(memory);
        var store = new BlockingStore();

        // Two managers stand in for two concurrent request scopes.
        var first  = NewManager(store, cache);
        var second = NewManager(store, cache);

        var readA = first.For<StampedeSettings>().GetAsync();
        await store.Entered.Task;   // A is now inside the store, holding the load lock

        var readB = second.For<StampedeSettings>().GetAsync();
        await Task.Delay(100);      // ample time for B to reach the store if it were free to

        Assert.Equal(1, store.Calls);

        store.Release.SetResult();
        var models = await Task.WhenAll(readA, readB);

        Assert.Equal(1, store.Calls);                  // B was served from cache, not the store
        Assert.All(models, m => Assert.Equal(587, m.Port));
    }

    [Fact]
    public async Task RefreshCache_StillReachesTheStore()
    {
        using var memory = new MemoryCache(new MemoryCacheOptions());
        var cache = new SettingCache(memory);
        var store = new BlockingStore();
        store.Release.SetResult();   // never block

        var mgr = NewManager(store, cache);

        await mgr.For<RefreshSettings>().GetAsync();
        await mgr.For<RefreshSettings>().GetAsync();                      // cached
        await mgr.For<RefreshSettings>().GetAsync(refreshCache: true);    // forced reload

        Assert.Equal(2, store.Calls);
    }

    private static SettingManager NewManager(ISettingStore store, ISettingCache cache)
        => new SettingManager(store, cache,
            new ServiceCollection().BuildServiceProvider(),
            NullLogger<SettingManager>.Instance);

    public class StampedeSettings { public int Port { get; set; } }
    public class RefreshSettings  { public int Port { get; set; } }

    /// <summary>Counts reads and holds the first one open until the test releases it.</summary>
    private sealed class BlockingStore : ISettingStore
    {
        private int _calls;

        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls => Volatile.Read(ref _calls);

        public async Task<IReadOnlyList<Setting>> GetGroupAsync(string group, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _calls);
            Entered.TrySetResult();
            await Release.Task.ConfigureAwait(false);
            return [new Setting { Group = group, Key = "Port", Value = "587", Type = "System.Int32" }];
        }

        public Task<Setting?> GetAsync(string group, string key, CancellationToken ct = default)
            => Task.FromResult<Setting?>(null);
        public Task UpsertAsync(Setting setting, CancellationToken ct = default) => Task.CompletedTask;
        public Task UpsertManyAsync(IEnumerable<Setting> settings, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeleteAsync(string group, string key, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeleteGroupAsync(string group, CancellationToken ct = default) => Task.CompletedTask;
        public Task<int> CountAsync(string group, CancellationToken ct = default) => Task.FromResult(0);
    }
}
