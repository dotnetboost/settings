using DotNetBoost.Settings.Core;
using DotNetBoost.Settings.Core.Attributes;
using DotNetBoost.Settings.Core.Interfaces;
using DotNetBoost.Settings.Core.Models;
using DotNetBoost.Settings.Core.Services;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace DotNetBoost.Settings.UnitTests;

/// <summary>
/// <c>SetAsync(selector, value)</c> used to rebuild the whole model from the <em>cache</em> and
/// put it through the group write. A property another writer had changed while this instance's
/// cache was warm read back stale, counted as a difference, and was written back — so a call
/// that only meant to touch property A reverted someone's edit to property B, over a window as
/// wide as <c>CacheDuration</c> rather than a round trip.
/// </summary>
public class SinglePropertyWriteTests
{
    /// <summary>
    /// The scenario itself: a warm cache on instance one, an out-of-band change to a different
    /// property through instance two, then a single-property write through instance one.
    /// <para>
    /// The two managers get <em>separate</em> caches on purpose. That is what two replicas
    /// behind a load balancer have, and it is the only arrangement in which the bug is
    /// reachable: sharing one cache would let instance two's eviction clear instance one's
    /// stale copy, and the stale read that does the damage would never happen.
    /// </para>
    /// </summary>
    [Fact]
    public async Task SettingOneProperty_DoesNotRevertAnotherWritersChangeToADifferentProperty()
    {
        var store = new RecordingStore();

        var first  = NewManager(store, SharedCache());
        var second = NewManager(store, SharedCache());

        await first.For<MailSettings>().SetAsync(new MailSettings { Host = "original.example.com", Port = 25 });

        // Instance one warms its cache on the group as it is now.
        await first.For<MailSettings>().GetAsync();

        // Someone else changes a *different* property, out of band.
        await second.For<MailSettings>().SetAsync(x => x.Host, "theirs.example.com");

        // Instance one now writes the property it actually meant to.
        await first.For<MailSettings>().SetAsync(x => x.Port, 587);

        var current = await first.For<MailSettings>().GetAsync(refreshCache: true);
        Assert.Equal("theirs.example.com", current.Host);   // not reverted
        Assert.Equal(587, current.Port);                    // and the intended write landed
    }

    [Fact]
    public async Task SettingOneProperty_WritesOnlyThatProperty()
    {
        var store = new RecordingStore();
        var mgr   = NewManager(store, SharedCache());

        await mgr.For<MailSettings>().SetAsync(new MailSettings { Host = "h", Port = 25 });
        store.WrittenKeys.Clear();

        await mgr.For<MailSettings>().SetAsync(x => x.Port, 587);

        Assert.Equal([nameof(MailSettings.Port)], store.WrittenKeys);
    }

    [Fact]
    public async Task SettingOneProperty_ToItsCurrentValue_WritesNothing()
    {
        var store = new RecordingStore();
        var mgr   = NewManager(store, SharedCache());

        await mgr.For<MailSettings>().SetAsync(new MailSettings { Host = "h", Port = 25 });
        store.WrittenKeys.Clear();

        await mgr.For<MailSettings>().SetAsync(x => x.Port, 25);

        Assert.Empty(store.WrittenKeys);
    }

    /// <summary>
    /// A validator takes a model, not a property, so the single-property path has to build the
    /// group as it will be and validate that. Skipping it would make the selector overload a
    /// way around every registered validator.
    /// </summary>
    [Fact]
    public async Task SettingOneProperty_StillRunsValidators_AndWritesNothingOnFailure()
    {
        var store    = new RecordingStore();
        var services = new ServiceCollection();
        services.AddSingleton<ISettingValidator, PortRangeValidator>();

        var mgr = new SettingManager(store, new SettingCache(new MemoryCache(new MemoryCacheOptions())),
            services.BuildServiceProvider(), NullLogger<SettingManager>.Instance);

        await mgr.For<MailSettings>().SetAsync(new MailSettings { Host = "h", Port = 25 });
        store.WrittenKeys.Clear();

        await Assert.ThrowsAsync<SettingValidationException>(
            () => mgr.For<MailSettings>().SetAsync(x => x.Port, 70_000));

        Assert.Empty(store.WrittenKeys);
    }

    /// <summary>
    /// The validated model is the group as it will be, so a validator sees the stored values of
    /// the properties the caller did not name — not the defaults of a blank instance.
    /// </summary>
    [Fact]
    public async Task Validators_SeeTheStoredValuesOfUntouchedProperties()
    {
        var store     = new RecordingStore();
        var validator = new CapturingValidator();
        var services  = new ServiceCollection();
        services.AddSingleton<ISettingValidator>(validator);

        var mgr = new SettingManager(store, new SettingCache(new MemoryCache(new MemoryCacheOptions())),
            services.BuildServiceProvider(), NullLogger<SettingManager>.Instance);

        await mgr.For<MailSettings>().SetAsync(new MailSettings { Host = "stored.example.com", Port = 25 });
        validator.Seen.Clear();

        await mgr.For<MailSettings>().SetAsync(x => x.Port, 587);

        var seen = Assert.IsType<MailSettings>(Assert.Single(validator.Seen));
        Assert.Equal("stored.example.com", seen.Host);
        Assert.Equal(587, seen.Port);
    }

    [Fact]
    public async Task SettingOneProperty_RaisesAWriteNotification_WithThatOneChange()
    {
        var store    = new RecordingStore();
        var observer = new CollectingObserver();
        var services = new ServiceCollection();
        services.AddScoped<ISettingWriteObserver>(_ => observer);

        var mgr = new SettingManager(store, new SettingCache(new MemoryCache(new MemoryCacheOptions())),
            services.BuildServiceProvider(), NullLogger<SettingManager>.Instance);

        await mgr.For<MailSettings>().SetAsync(new MailSettings { Host = "h", Port = 25 });
        observer.Writes.Clear();

        await mgr.For<MailSettings>().SetAsync(x => x.Port, 587);

        var change = Assert.Single(Assert.Single(observer.Writes).Changes);
        Assert.Equal(nameof(MailSettings.Port), change.PropertyName);
        Assert.Equal("25",  change.OldValue);
        Assert.Equal("587", change.NewValue);
    }

    [Fact]
    public async Task SettingOneProperty_EvictsTheCache()
    {
        var store = new RecordingStore();
        var mgr   = NewManager(store, SharedCache());

        await mgr.For<MailSettings>().SetAsync(new MailSettings { Host = "h", Port = 25 });
        await mgr.For<MailSettings>().GetAsync();                 // warm

        await mgr.For<MailSettings>().SetAsync(x => x.Port, 587);

        Assert.Equal(587, (await mgr.For<MailSettings>().GetAsync()).Port);
    }

    /// <summary>One cache instance, as a single application instance has.</summary>
    private static SettingCache SharedCache() => new(new MemoryCache(new MemoryCacheOptions()));

    private static SettingManager NewManager(ISettingStore store, ISettingCache cache)
        => new(store, cache, new ServiceCollection().BuildServiceProvider(),
               NullLogger<SettingManager>.Instance);

    [SettingGroup("single-property-mail")]
    public class MailSettings
    {
        public string? Host { get; set; }
        public int     Port { get; set; }
    }

    private sealed class PortRangeValidator : ISettingValidator
    {
        public bool CanValidate(Type type) => type == typeof(MailSettings);

        public Task<(bool IsValid, IDictionary<string, string[]> Errors)> ValidateAsync(object model)
        {
            var mail = (MailSettings)model;
            return Task.FromResult(mail.Port is > 0 and <= 65_535
                ? (true, (IDictionary<string, string[]>)new Dictionary<string, string[]>())
                : (false, new Dictionary<string, string[]>
                {
                    [nameof(MailSettings.Port)] = ["Port must be between 1 and 65535."]
                }));
        }
    }

    private sealed class CapturingValidator : ISettingValidator
    {
        public List<object> Seen { get; } = [];

        public bool CanValidate(Type type) => type == typeof(MailSettings);

        public Task<(bool IsValid, IDictionary<string, string[]> Errors)> ValidateAsync(object model)
        {
            Seen.Add(model);
            return Task.FromResult((true, (IDictionary<string, string[]>)new Dictionary<string, string[]>()));
        }
    }

    private sealed class CollectingObserver : ISettingWriteObserver
    {
        public List<SettingWrite> Writes { get; } = [];

        public Task OnWrittenAsync(SettingWrite write, CancellationToken ct = default)
        {
            Writes.Add(write);
            return Task.CompletedTask;
        }
    }

    /// <summary>A store whose reads reflect its writes, recording which keys were written.</summary>
    private sealed class RecordingStore : ISettingStore
    {
        private readonly Dictionary<string, Setting> _data = new(StringComparer.Ordinal);
        private static string K(string g, string k) => g + "|" + k;

        public List<string> WrittenKeys { get; } = [];

        public Task<IReadOnlyList<Setting>> GetGroupAsync(string group, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Setting>>(
                _data.Values.Where(x => x.Group == group).Select(Copy).ToList());

        public Task<Setting?> GetAsync(string group, string key, CancellationToken ct = default)
            => Task.FromResult(_data.TryGetValue(K(group, key), out var s) ? Copy(s) : null);

        public Task UpsertAsync(Setting setting, CancellationToken ct = default)
            => UpsertManyAsync([setting], ct);

        public Task UpsertManyAsync(IEnumerable<Setting> settings, CancellationToken ct = default)
        {
            foreach (var s in settings)
            {
                if (_data.TryGetValue(K(s.Group, s.Key), out var existing) &&
                    s.RowVersion is not null &&
                    !existing.RowVersion.AsSpan().SequenceEqual(s.RowVersion))
                {
                    throw new SettingConcurrencyException(s.Group, s.Key);
                }

                var stored = Copy(s);
                stored.RowVersion = Setting.NewRowVersion();
                _data[K(s.Group, s.Key)] = stored;
                WrittenKeys.Add(s.Key);
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
