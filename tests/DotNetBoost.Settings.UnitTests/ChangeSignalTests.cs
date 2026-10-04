using DotNetBoost.Settings.Core;
using DotNetBoost.Settings.Core.Attributes;
using DotNetBoost.Settings.Core.Interfaces;
using DotNetBoost.Settings.Core.Models;
using DotNetBoost.Settings.Core.Services;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace DotNetBoost.Settings.UnitTests;

/// <summary>
/// "Change it, reload, done, no redeploy" is this library's central claim, and it silently
/// stopped being true at two replicas: a write on one instance evicted that instance's
/// <c>IMemoryCache</c> and nothing else, so every other instance went on serving the old
/// values for up to <c>CacheDuration</c>.
/// <para>
/// Each manager here gets its own <see cref="ISettingCache"/>, which is what two processes
/// have, and they share the signal — exactly the arrangement a Redis-backed deployment has.
/// </para>
/// </summary>
public class ChangeSignalTests
{
    [Fact]
    public async Task WithoutASignal_AnInstanceServesStaleValuesUntilItsCacheExpires()
    {
        // The behaviour being fixed, pinned so the test below is known to be about the signal
        // rather than about something else keeping the caches in step.
        var store = new FakeStore();

        var first  = NewManager(store, NewCache(), signal: null);
        var second = NewManager(store, NewCache(), signal: null);

        await first.For<SignalSettings>().SetAsync(new SignalSettings { Host = "original" });
        await second.For<SignalSettings>().GetAsync();            // second warms its cache

        await first.For<SignalSettings>().SetAsync(new SignalSettings { Host = "updated" });

        Assert.Equal("original", (await second.For<SignalSettings>().GetAsync()).Host);
    }

    /// <summary>
    /// <c>ChangeCheckInterval</c> is zero here so the token is compared on every read, which
    /// makes the test deterministic rather than dependent on wall-clock delay. In a real
    /// deployment it is 5 seconds, and that interval is exactly the staleness bound — see
    /// <see cref="TheTokenIsOnlyRechecked_OncePerCheckInterval"/>.
    /// </summary>
    [Fact]
    public async Task WithASharedSignal_AWriteOnOneInstanceInvalidatesTheOther()
    {
        var store  = new FakeStore();
        var signal = NewSignal();

        var first  = NewManager(store, NewCache(), signal, CheckAlways());
        var second = NewManager(store, NewCache(), signal, CheckAlways());

        await first.For<SignalSettings>().SetAsync(new SignalSettings { Host = "original" });
        await second.For<SignalSettings>().GetAsync();            // second warms its cache

        await first.For<SignalSettings>().SetAsync(new SignalSettings { Host = "updated" });

        Assert.Equal("updated", (await second.For<SignalSettings>().GetAsync()).Host);
    }

    [Fact]
    public async Task ClearOnOneInstance_InvalidatesTheOther()
    {
        var store  = new FakeStore();
        var signal = NewSignal();

        var first  = NewManager(store, NewCache(), signal, CheckAlways());
        var second = NewManager(store, NewCache(), signal, CheckAlways());

        await first.For<SignalSettings>().SetAsync(new SignalSettings { Host = "original" });
        await second.For<SignalSettings>().GetAsync();

        await first.For<SignalSettings>().ClearAsync();

        Assert.Null((await second.For<SignalSettings>().GetAsync()).Host);
    }

    /// <summary>
    /// A write to one group must not make every other group reload. The token is per group,
    /// so this is what proves the key carries the group name.
    /// </summary>
    [Fact]
    public async Task AWriteToOneGroup_DoesNotInvalidateAnother()
    {
        var store  = new FakeStore();
        var signal = NewSignal();

        var first  = NewManager(store, NewCache(), signal, CheckAlways());
        var second = NewManager(store, NewCache(), signal, CheckAlways());

        await first.For<OtherSignalSettings>().SetAsync(new OtherSignalSettings { Host = "other" });
        await second.For<OtherSignalSettings>().GetAsync();
        var readsAfterWarmup = store.GroupReads;

        await first.For<SignalSettings>().SetAsync(new SignalSettings { Host = "unrelated" });
        var readsAfterUnrelatedWrite = store.GroupReads;

        await second.For<OtherSignalSettings>().GetAsync();

        // The unrelated write cost the second instance no reload of its own group.
        Assert.Equal(readsAfterUnrelatedWrite, store.GroupReads);
        Assert.True(readsAfterUnrelatedWrite > readsAfterWarmup);   // the write itself did read
    }

    [Fact]
    public async Task TheTokenIsOnlyRechecked_OncePerCheckInterval()
    {
        var store  = new FakeStore();
        var signal = new CountingSignal(NewSignal());

        // A long interval, so the second read inside it must not reach the signal.
        var mgr = NewManager(store, NewCache(), signal,
            new SettingOptions { ChangeCheckInterval = TimeSpan.FromMinutes(5) });

        await mgr.For<SignalSettings>().SetAsync(new SignalSettings { Host = "a" });
        await mgr.For<SignalSettings>().GetAsync();     // loads, records the token
        var readsAfterLoad = signal.VersionReads;

        await mgr.For<SignalSettings>().GetAsync();
        await mgr.For<SignalSettings>().GetAsync();

        Assert.Equal(readsAfterLoad, signal.VersionReads);
    }

    /// <summary>
    /// A signal is an optimisation over the cache duration. Failing a read because the
    /// signalling store is down would turn a slower reload into an outage.
    /// </summary>
    [Fact]
    public async Task AnUnreachableSignal_DoesNotFailReadsOrWrites()
    {
        var store = new FakeStore();
        var mgr   = NewManager(store, NewCache(), new BrokenSignal());

        var writeError = await Record.ExceptionAsync(
            () => mgr.For<SignalSettings>().SetAsync(new SignalSettings { Host = "a" }));

        var read = await mgr.For<SignalSettings>().GetAsync();

        Assert.Null(writeError);
        Assert.Equal("a", read.Host);
    }

    [Fact]
    public async Task TheDefaultSignal_LeavesSingleInstanceBehaviourUnchanged()
    {
        // No signal registered: a cache hit is a cache hit, with no extra store round trip.
        var store = new FakeStore();
        var mgr   = NewManager(store, NewCache(), signal: null);

        await mgr.For<SignalSettings>().SetAsync(new SignalSettings { Host = "a" });
        await mgr.For<SignalSettings>().GetAsync();
        var readsAfterLoad = store.GroupReads;

        await mgr.For<SignalSettings>().GetAsync();
        await mgr.For<SignalSettings>().GetAsync();

        Assert.Equal(readsAfterLoad, store.GroupReads);
    }

    [Fact]
    public async Task DistributedCacheSignal_PublishesAFreshTokenPerWrite()
    {
        var signal = NewSignal();

        Assert.Null(await signal.GetVersionAsync("Group"));

        await signal.SignalChangeAsync("Group");
        var first = await signal.GetVersionAsync("Group");

        await signal.SignalChangeAsync("Group");
        var second = await signal.GetVersionAsync("Group");

        Assert.NotNull(first);
        Assert.NotEqual(first, second);
        Assert.Null(await signal.GetVersionAsync("AnotherGroup"));
    }

    [Fact]
    public void SynchronizeThroughDistributedCache_WithoutACache_SaysWhatToRegister()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(new Mock.NoopStore());
        services.AddSingleton<ISettingStore>(sp => sp.GetRequiredService<Mock.NoopStore>());
        services.AddSettings().SynchronizeThroughDistributedCache();

        var provider = services.BuildServiceProvider();

        var ex = Assert.Throws<InvalidOperationException>(
            () => provider.GetRequiredService<ISettingChangeSignal>());

        Assert.Contains("IDistributedCache", ex.Message, StringComparison.Ordinal);
        Assert.Contains("AddStackExchangeRedisCache", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Compare the token on every read. Only reachable by building options directly — the
    /// builder rejects a non-positive interval, because in production it would mean a round
    /// trip to the signalling store on every settings read.
    /// </summary>
    private static SettingOptions CheckAlways() => new() { ChangeCheckInterval = TimeSpan.Zero };

    private static SettingCache NewCache() => new(new MemoryCache(new MemoryCacheOptions()));

    private static DistributedCacheSettingChangeSignal NewSignal()
        => new(new MemoryDistributedCache(
            Microsoft.Extensions.Options.Options.Create(new MemoryDistributedCacheOptions())));

    private static SettingManager NewManager(
        ISettingStore store, ISettingCache cache, ISettingChangeSignal? signal, SettingOptions? options = null)
    {
        var services = new ServiceCollection();
        if (signal is not null)
            services.AddScoped<ISettingWriteObserver>(_ => new PublishingObserver(signal));

        return new SettingManager(store, cache, services.BuildServiceProvider(),
            NullLogger<SettingManager>.Instance, encryptor: null, actorAccessor: null,
            signal: signal, options: options);
    }

    /// <summary>
    /// Stands in for the <c>ChangeSignalPublisher</c> the builder registers, which is internal
    /// to Core. Same one line, so this exercises the same arrangement.
    /// </summary>
    private sealed class PublishingObserver(ISettingChangeSignal signal) : ISettingWriteObserver
    {
        public Task OnWrittenAsync(SettingWrite write, CancellationToken ct = default)
            => signal.SignalChangeAsync(write.GroupName, ct);
    }

    private sealed class CountingSignal(ISettingChangeSignal inner) : ISettingChangeSignal
    {
        private int _versionReads;

        public int VersionReads => Volatile.Read(ref _versionReads);

        public Task<string?> GetVersionAsync(string groupName, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _versionReads);
            return inner.GetVersionAsync(groupName, ct);
        }

        public Task SignalChangeAsync(string groupName, CancellationToken ct = default)
            => inner.SignalChangeAsync(groupName, ct);
    }

    private sealed class BrokenSignal : ISettingChangeSignal
    {
        public Task<string?> GetVersionAsync(string groupName, CancellationToken ct = default)
            => throw new InvalidOperationException("signalling store is down");

        public Task SignalChangeAsync(string groupName, CancellationToken ct = default)
            => throw new InvalidOperationException("signalling store is down");
    }

    [SettingGroup("signal-settings")]
    public class SignalSettings { public string? Host { get; set; } }

    [SettingGroup("other-signal-settings")]
    public class OtherSignalSettings { public string? Host { get; set; } }

    internal static class Mock
    {
        internal sealed class NoopStore : ISettingStore
        {
            public Task<IReadOnlyList<Setting>> GetGroupAsync(string g, CancellationToken ct = default)
                => Task.FromResult<IReadOnlyList<Setting>>([]);
            public Task<Setting?> GetAsync(string g, string k, CancellationToken ct = default)
                => Task.FromResult<Setting?>(null);
            public Task UpsertAsync(Setting s, CancellationToken ct = default) => Task.CompletedTask;
            public Task UpsertManyAsync(IEnumerable<Setting> s, CancellationToken ct = default) => Task.CompletedTask;
            public Task DeleteAsync(string g, string k, CancellationToken ct = default) => Task.CompletedTask;
            public Task DeleteGroupAsync(string g, CancellationToken ct = default) => Task.CompletedTask;
            public Task<int> CountAsync(string g, CancellationToken ct = default) => Task.FromResult(0);
        }
    }

    /// <summary>A store whose reads reflect its writes, counting group reads.</summary>
    private sealed class FakeStore : ISettingStore
    {
        private readonly Dictionary<string, Setting> _data = new(StringComparer.Ordinal);
        private int _groupReads;
        private static string K(string g, string k) => g + "|" + k;

        public int GroupReads => Volatile.Read(ref _groupReads);

        public Task<IReadOnlyList<Setting>> GetGroupAsync(string group, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _groupReads);
            return Task.FromResult<IReadOnlyList<Setting>>(
                _data.Values.Where(x => x.Group == group).Select(Copy).ToList());
        }

        public Task<Setting?> GetAsync(string group, string key, CancellationToken ct = default)
            => Task.FromResult(_data.TryGetValue(K(group, key), out var s) ? Copy(s) : null);

        public Task UpsertAsync(Setting setting, CancellationToken ct = default)
            => UpsertManyAsync([setting], ct);

        public Task UpsertManyAsync(IEnumerable<Setting> settings, CancellationToken ct = default)
        {
            foreach (var s in settings)
            {
                var stored = Copy(s);
                stored.RowVersion = Setting.NewRowVersion();
                _data[K(s.Group, s.Key)] = stored;
            }
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string group, string key, CancellationToken ct = default)
        {
            _data.Remove(K(group, key));
            return Task.CompletedTask;
        }

        public Task DeleteGroupAsync(string group, CancellationToken ct = default)
        {
            foreach (var k in _data.Where(x => x.Value.Group == group).Select(x => x.Key).ToList())
                _data.Remove(k);
            return Task.CompletedTask;
        }

        public Task<int> CountAsync(string group, CancellationToken ct = default)
            => Task.FromResult(_data.Values.Count(x => x.Group == group));

        private static Setting Copy(Setting s) => new()
        {
            Id = s.Id, Group = s.Group, Key = s.Key, Value = s.Value, Type = s.Type,
            IsEncrypted = s.IsEncrypted, UpdatedAt = s.UpdatedAt, UpdatedBy = s.UpdatedBy,
            RowVersion = s.RowVersion
        };
    }
}
