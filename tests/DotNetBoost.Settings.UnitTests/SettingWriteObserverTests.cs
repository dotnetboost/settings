using DotNetBoost.Settings.Core.Attributes;
using DotNetBoost.Settings.Core.Interfaces;
using DotNetBoost.Settings.Core.Models;
using DotNetBoost.Settings.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Diagnostics.Metrics;
using System.Security.Cryptography;

namespace DotNetBoost.Settings.UnitTests;

/// <summary>
/// The write hook that replaced the audit trail. These use a stateful fake store rather than
/// a mock: the before-values an observer is handed come from a read the write itself performs,
/// which is invisible to a store whose reads are unaffected by its writes.
/// </summary>
public class SettingWriteObserverTests
{
    [Fact]
    public async Task Write_RaisesOneNotification_PerSave()
    {
        var (mgr, _, observer) = Build();

        await mgr.For<ObservedSettings>().SetAsync(new ObservedSettings { Host = "a", Port = 25 });

        var write = Assert.Single(observer.Writes);
        Assert.Equal(nameof(ObservedSettings), write.GroupName);
        Assert.Equal("observed", write.Route);
        Assert.Equal(SettingWriteKind.Updated, write.Kind);
    }

    /// <summary>
    /// The N properties of one save are one edit. Separate notifications per row would make
    /// that unrecoverable downstream, which is why the correlation id is shared.
    /// </summary>
    [Fact]
    public async Task Write_CarriesEveryChangedProperty_UnderOneCorrelationId()
    {
        var (mgr, _, observer) = Build();

        await mgr.For<ObservedSettings>().SetAsync(new ObservedSettings { Host = "a", Port = 25 });

        var write = Assert.Single(observer.Writes);
        Assert.Equal(
            [nameof(ObservedSettings.Host), nameof(ObservedSettings.Port)],
            write.Changes.Select(c => c.PropertyName).OrderBy(n => n, StringComparer.Ordinal));
        Assert.False(string.IsNullOrWhiteSpace(write.CorrelationId));
    }

    [Fact]
    public async Task Write_SeparateSaves_GetDifferentCorrelationIds()
    {
        var (mgr, _, observer) = Build();

        await mgr.For<ObservedSettings>().SetAsync(new ObservedSettings { Host = "a" });
        await mgr.For<ObservedSettings>().SetAsync(new ObservedSettings { Host = "b" });

        Assert.Equal(2, observer.Writes.Count);
        Assert.NotEqual(observer.Writes[0].CorrelationId, observer.Writes[1].CorrelationId);
    }

    [Fact]
    public async Task Change_CarriesBeforeAndAfterValues()
    {
        var (mgr, _, observer) = Build();

        await mgr.For<ObservedSettings>().SetAsync(new ObservedSettings { Host = "old.example.com" });
        observer.Writes.Clear();
        await mgr.For<ObservedSettings>().SetAsync(new ObservedSettings { Host = "new.example.com" });

        var change = Single(observer, nameof(ObservedSettings.Host));
        Assert.Equal("old.example.com", change.OldValue);
        Assert.Equal("new.example.com", change.NewValue);
    }

    [Fact]
    public async Task Change_FirstWrite_HasNullOldValue()
    {
        var (mgr, _, observer) = Build();

        await mgr.For<ObservedSettings>().SetAsync(new ObservedSettings { Host = "first.example.com" });

        var change = Single(observer, nameof(ObservedSettings.Host));
        Assert.Null(change.OldValue);
        Assert.Equal("first.example.com", change.NewValue);
    }

    /// <summary>
    /// The library decides what is sensitive; an observer only learns that it changed. An
    /// observer is exactly the component that forwards writes somewhere else, so a secret
    /// must not be reachable through it even by accident.
    /// </summary>
    [Fact]
    public async Task Change_SensitiveProperty_IsRedactedWithNoValues()
    {
        var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var (mgr, _, observer) = Build(new AesSettingEncryptor(key));

        await mgr.For<ObservedSettings>().SetAsync(new ObservedSettings { Secret = "s3cret" });
        observer.Writes.Clear();
        await mgr.For<ObservedSettings>().SetAsync(new ObservedSettings { Secret = "n3wer" });

        var change = Single(observer, nameof(ObservedSettings.Secret));
        Assert.True(change.IsRedacted);
        Assert.Null(change.OldValue);
        Assert.Null(change.NewValue);
    }

    [Fact]
    public async Task Change_SensitiveProperty_IsRedacted_EvenWithNoEncryptorConfigured()
    {
        // Redaction follows [Sensitive], not whether the value happened to be encrypted at
        // rest. Tying it to the encryptor would leak every secret in an app without one.
        var (mgr, _, observer) = Build();

        await mgr.For<ObservedSettings>().SetAsync(new ObservedSettings { Secret = "s3cret" });

        var change = Single(observer, nameof(ObservedSettings.Secret));
        Assert.True(change.IsRedacted);
        Assert.Null(change.NewValue);
    }

    [Fact]
    public async Task Write_ReportsOnlyTheChangedProperty()
    {
        var (mgr, _, observer) = Build();

        await mgr.For<ObservedSettings>().SetAsync(new ObservedSettings { Host = "a", Port = 25 });
        observer.Writes.Clear();
        await mgr.For<ObservedSettings>().SetAsync(new ObservedSettings { Host = "a", Port = 587 });

        var change = Assert.Single(Assert.Single(observer.Writes).Changes);
        Assert.Equal(nameof(ObservedSettings.Port), change.PropertyName);
        Assert.Equal("25", change.OldValue);
        Assert.Equal("587", change.NewValue);
    }

    [Fact]
    public async Task Write_ThatChangesNothing_RaisesNoNotification()
    {
        var (mgr, _, observer) = Build();

        await mgr.For<ObservedSettings>().SetAsync(new ObservedSettings { Host = "same.example.com" });
        observer.Writes.Clear();
        await mgr.For<ObservedSettings>().SetAsync(new ObservedSettings { Host = "same.example.com" });

        Assert.Empty(observer.Writes);
    }

    [Fact]
    public async Task Write_UnchangedSensitiveProperty_RaisesNothing_DespiteFreshCiphertext()
    {
        // The real AES-GCM encryptor, not a stub: it draws a new nonce per call, so an
        // unchanged secret serialises to different ciphertext on every save. Comparing
        // ciphertext would report a change here; comparing plaintext must not.
        var key       = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var encryptor = new AesSettingEncryptor(key);

        // Guards the premise: re-encrypting the same plaintext really does produce different
        // bytes, so comparing stored ciphertext would report a change on every save.
        Assert.NotEqual(encryptor.Encrypt("s3cret"), encryptor.Encrypt("s3cret"));

        var (mgr, store, observer) = Build(encryptor);

        await mgr.For<ObservedSettings>().SetAsync(new ObservedSettings { Secret = "s3cret" });
        var firstCiphertext = (await store.GetAsync(nameof(ObservedSettings), nameof(ObservedSettings.Secret)))!.Value;
        observer.Writes.Clear();

        await mgr.For<ObservedSettings>().SetAsync(new ObservedSettings { Secret = "s3cret" });

        Assert.Empty(observer.Writes);
        // Unchanged properties are not rewritten at all, so the stored bytes stay put.
        Assert.Equal(
            firstCiphertext,
            (await store.GetAsync(nameof(ObservedSettings), nameof(ObservedSettings.Secret)))!.Value);
    }

    /// <summary>
    /// The reason <see cref="SettingWriteKind"/> exists. Deleting every row in a group is the
    /// most destructive thing the library does, and under the old audit trail it was the one
    /// operation that recorded nothing at all. It reports what it deleted, so an audit of a
    /// clear can say more than that it happened.
    /// </summary>
    [Fact]
    public async Task Clear_RaisesAClearedNotification_ListingWhatItDeleted()
    {
        var (mgr, _, observer) = Build();

        await mgr.For<ObservedSettings>().SetAsync(new ObservedSettings { Host = "a", Port = 25 });
        observer.Writes.Clear();

        await mgr.For<ObservedSettings>().ClearAsync();

        var write = Assert.Single(observer.Writes);
        Assert.Equal(SettingWriteKind.Cleared, write.Kind);
        Assert.Equal(nameof(ObservedSettings), write.GroupName);
        Assert.Equal(
            [
                new SettingChange(nameof(ObservedSettings.Host), "a",  null, IsRedacted: false),
                new SettingChange(nameof(ObservedSettings.Port), "25", null, IsRedacted: false)
            ],
            write.Changes);
    }

    [Fact]
    public async Task Clear_RedactsSensitiveValues_ItDeleted()
    {
        var (mgr, _, observer) = Build();

        await mgr.For<ObservedSettings>().SetAsync(new ObservedSettings { Secret = "s3cret" });
        observer.Writes.Clear();

        await mgr.For<ObservedSettings>().ClearAsync();

        var change = Single(observer, nameof(ObservedSettings.Secret));
        Assert.True(change.IsRedacted);
        Assert.Null(change.OldValue);
    }

    [Fact]
    public async Task Clear_OfAnEmptyGroup_StillNotifies_WithNoChanges()
    {
        var (mgr, _, observer) = Build();

        await mgr.For<ObservedSettings>().ClearAsync();

        var write = Assert.Single(observer.Writes);
        Assert.Equal(SettingWriteKind.Cleared, write.Kind);
        Assert.Empty(write.Changes);
    }

    /// <summary>
    /// What an observer needs to read the group's attributes without scanning assemblies for
    /// a class whose resolved name matches. Checked on all three write paths, since each
    /// builds its notification separately.
    /// </summary>
    [Fact]
    public async Task Write_CarriesTheGroupType_OnEveryWritePath()
    {
        var (mgr, _, observer) = Build();
        var accessor = mgr.For<ObservedSettings>();

        await accessor.SetAsync(new ObservedSettings { Host = "a" });
        await accessor.SetAsync(x => x.Port, 587);
        await accessor.ClearAsync();

        Assert.Equal(
            [SettingWriteKind.Updated, SettingWriteKind.Updated, SettingWriteKind.Cleared],
            observer.Writes.Select(w => w.Kind));
        Assert.All(observer.Writes, w => Assert.Equal(typeof(ObservedSettings), w.GroupType));
    }

    /// <summary>
    /// An observer runs after the store write committed and the cache entry was evicted, so
    /// failing the call would report a write that in fact succeeded.
    /// </summary>
    [Fact]
    public async Task Observer_ThatThrows_DoesNotFailTheWrite_AndLaterObserversStillRun()
    {
        var throwing = new ThrowingObserver();
        var after    = new RecordingObserver();
        var store    = new FakeStore();

        var services = new ServiceCollection();
        services.AddScoped<ISettingWriteObserver>(_ => throwing);
        services.AddScoped<ISettingWriteObserver>(_ => after);

        var mgr = new SettingManager(store, new NoCache(), services.BuildServiceProvider(),
            NullLogger<SettingManager>.Instance);

        var ex = await Record.ExceptionAsync(
            () => mgr.For<ObservedSettings>().SetAsync(new ObservedSettings { Host = "a" }));

        Assert.Null(ex);
        Assert.True(throwing.WasCalled);
        Assert.Single(after.Writes);

        // And the write really did land, rather than being rolled back with the notification.
        Assert.Equal("a", (await store.GetAsync(nameof(ObservedSettings), nameof(ObservedSettings.Host)))!.Value);
    }

    /// <summary>
    /// A swallowed exception is still logged; the counter is what makes a rate of them visible
    /// without anyone reading logs.
    /// </summary>
    [Fact]
    public async Task Observer_ThatThrows_IsCounted_WithItsTypeGroupAndError()
    {
        using var metrics = new MetricsCapture();
        var services = new ServiceCollection();
        services.AddScoped<ISettingWriteObserver, ThrowingObserver>();
        var mgr = Build(services, metrics);

        await mgr.For<ObservedSettings>().SetAsync(new ObservedSettings { Host = "a" });

        var m = Assert.Single(metrics.Measurements);
        Assert.Equal(SettingMetrics.WriteObserverFailuresName, m.Instrument);
        Assert.Equal(1, m.Value);
        Assert.Equal(nameof(ThrowingObserver),                 m.Tags[SettingMetrics.ObserverTag]);
        Assert.Equal(nameof(ObservedSettings),                 m.Tags[SettingMetrics.GroupTag]);
        Assert.Equal(typeof(InvalidOperationException).FullName, m.Tags[SettingMetrics.ErrorTag]);
    }

    [Fact]
    public async Task ChangeHandler_ThatThrows_IsCounted_WithItsTypeGroupAndError()
    {
        using var metrics = new MetricsCapture();
        var services = new ServiceCollection();
        services.AddScoped<ISettingChangedHandler<ObservedSettings>, ThrowingChangeHandler>();
        var mgr = Build(services, metrics);

        var ex = await Record.ExceptionAsync(
            () => mgr.For<ObservedSettings>().SetAsync(new ObservedSettings { Host = "a" }));

        Assert.Null(ex);
        var m = Assert.Single(metrics.Measurements);
        Assert.Equal(SettingMetrics.ChangeHandlerFailuresName, m.Instrument);
        Assert.Equal(nameof(ThrowingChangeHandler),            m.Tags[SettingMetrics.HandlerTag]);
        Assert.Equal(nameof(ObservedSettings),                 m.Tags[SettingMetrics.GroupTag]);
        Assert.Equal(typeof(InvalidOperationException).FullName, m.Tags[SettingMetrics.ErrorTag]);
    }

    [Fact]
    public async Task Write_WhoseObserversAndHandlersSucceed_CountsNothing()
    {
        using var metrics = new MetricsCapture();
        var services = new ServiceCollection();
        services.AddScoped<ISettingWriteObserver, RecordingObserver>();
        var mgr = Build(services, metrics);

        await mgr.For<ObservedSettings>().SetAsync(new ObservedSettings { Host = "a" });
        await mgr.For<ObservedSettings>().ClearAsync();

        Assert.Empty(metrics.Measurements);
    }

    /// <summary>
    /// <c>AddSettings()</c> takes the container's meter factory when there is one, so a host's
    /// measurements stay on its own meter, and still resolves when there is none.
    /// </summary>
    [Fact]
    public void AddSettings_CreatesTheMeter_FromTheContainersFactory_WhenThereIsOne()
    {
        using var metrics = new MetricsCapture();

        var withFactory = new ServiceCollection()
            .AddSingleton<IMeterFactory>(metrics.Factory);
        withFactory.AddSettings();
        using var sp = withFactory.BuildServiceProvider();

        sp.GetRequiredService<SettingMetrics>()
            .WriteObserverFailed(typeof(ThrowingObserver), "g", new InvalidOperationException());
        Assert.Single(metrics.Measurements);

        var withoutFactory = new ServiceCollection();
        withoutFactory.AddSettings();
        using var bare = withoutFactory.BuildServiceProvider();

        Assert.NotNull(bare.GetRequiredService<SettingMetrics>());
    }

    [Fact]
    public async Task Actor_IsNull_WhenNoAccessorIsRegistered()
    {
        var (mgr, _, observer) = Build();

        await mgr.For<ObservedSettings>().SetAsync(new ObservedSettings { Host = "a" });

        Assert.Null(Assert.Single(observer.Writes).Actor);
    }

    [Fact]
    public async Task Actor_CarriesTheAccessorsValue_WhenOneIsRegistered()
    {
        var (mgr, _, observer) = Build(actor: new StubActorAccessor("alex@example.com"));

        await mgr.For<ObservedSettings>().SetAsync(new ObservedSettings { Host = "a" });

        Assert.Equal("alex@example.com", Assert.Single(observer.Writes).Actor);
    }

    [Fact]
    public async Task LoggingObserver_LogsPropertyNames_AndNeverValues()
    {
        var logger   = new CapturingLogger<LoggingSettingWriteObserver>();
        var observer = new LoggingSettingWriteObserver(logger);

        await observer.OnWrittenAsync(new SettingWrite(
            "ObservedSettings", "observed", SettingWriteKind.Updated,
            [
                new SettingChange("Host",   "old.example.com", "new.example.com", IsRedacted: false),
                new SettingChange("Secret", null,              null,              IsRedacted: true)
            ],
            Actor: "alex@example.com", CorrelationId: "corr-1", OccurredAt: DateTime.UtcNow));

        var line = Assert.Single(logger.Messages);
        Assert.Contains("Host", line, StringComparison.Ordinal);
        Assert.Contains("Secret", line, StringComparison.Ordinal);
        Assert.Contains("alex@example.com", line, StringComparison.Ordinal);

        // The property names, never what they were set to.
        Assert.DoesNotContain("old.example.com", line, StringComparison.Ordinal);
        Assert.DoesNotContain("new.example.com", line, StringComparison.Ordinal);
    }

    private static SettingChange Single(RecordingObserver observer, string propertyName)
        => Assert.Single(observer.Writes).Changes.Single(c => c.PropertyName == propertyName);

    private static (ISettingManager, FakeStore, RecordingObserver) Build(
        ISettingEncryptor? encryptor = null, ISettingActorAccessor? actor = null)
    {
        var store    = new FakeStore();
        var observer = new RecordingObserver();

        var services = new ServiceCollection();
        services.AddScoped<ISettingWriteObserver>(_ => observer);

        var mgr = new SettingManager(store, new NoCache(), services.BuildServiceProvider(),
            NullLogger<SettingManager>.Instance, encryptor, actor);

        return (mgr, store, observer);
    }

    private static SettingManager Build(ServiceCollection services, MetricsCapture metrics)
    {
        services.AddSingleton(new SettingMetrics(metrics.Factory));
        return new SettingManager(new FakeStore(), new NoCache(), services.BuildServiceProvider(),
            NullLogger<SettingManager>.Instance);
    }

    [SettingGroup("observed")]
    public class ObservedSettings
    {
        public string? Host { get; set; }
        public int Port { get; set; }
        [Sensitive] public string? Secret { get; set; }
    }

    private sealed class RecordingObserver : ISettingWriteObserver
    {
        public List<SettingWrite> Writes { get; } = [];

        public Task OnWrittenAsync(SettingWrite write, CancellationToken ct = default)
        {
            Writes.Add(write);
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingObserver : ISettingWriteObserver
    {
        public bool WasCalled { get; private set; }

        public Task OnWrittenAsync(SettingWrite write, CancellationToken ct = default)
        {
            WasCalled = true;
            throw new InvalidOperationException("observer is down");
        }
    }

    private sealed class ThrowingChangeHandler : ISettingChangedHandler<ObservedSettings>
    {
        public Task OnChangedAsync(ObservedSettings previous, ObservedSettings current, CancellationToken ct = default)
            => throw new InvalidOperationException("handler is down");
    }

    /// <summary>
    /// Collects measurements from meters made by its own factory only. Listening by meter name
    /// would also catch measurements from tests running in parallel.
    /// </summary>
    private sealed class MetricsCapture : IDisposable
    {
        private readonly MeterListener _listener = new();

        public TestMeterFactory Factory { get; } = new();
        public List<(string Instrument, long Value, Dictionary<string, object?> Tags)> Measurements { get; } = [];

        public MetricsCapture()
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (ReferenceEquals(instrument.Meter.Scope, Factory))
                    listener.EnableMeasurementEvents(instrument);
            };
            _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            {
                var bag = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (var tag in tags) bag[tag.Key] = tag.Value;
                lock (Measurements) Measurements.Add((instrument.Name, value, bag));
            });
            _listener.Start();
        }

        public void Dispose()
        {
            _listener.Dispose();
            Factory.Dispose();
        }
    }

    private sealed class TestMeterFactory : IMeterFactory
    {
        private readonly List<Meter> _meters = [];

        public Meter Create(MeterOptions options)
        {
            options.Scope = this;
            var meter = new Meter(options);
            _meters.Add(meter);
            return meter;
        }

        public void Dispose()
        {
            foreach (var meter in _meters) meter.Dispose();
        }
    }

    private sealed class StubActorAccessor(string? actor) : ISettingActorAccessor
    {
        public string? GetActor() => actor;
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                                Func<TState, Exception?, string> formatter)
            => Messages.Add(formatter(state, exception));
    }

    /// <summary>A store whose reads reflect its writes, like a real one.</summary>
    private sealed class FakeStore : ISettingStore
    {
        private readonly Dictionary<string, Setting> _data = new(StringComparer.Ordinal);
        private static string K(string g, string k) => g + "|" + k;

        public Task<IReadOnlyList<Setting>> GetGroupAsync(string group, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Setting>>(_data.Values.Where(x => x.Group == group).ToList());

        public Task<Setting?> GetAsync(string group, string key, CancellationToken ct = default)
            => Task.FromResult(_data.GetValueOrDefault(K(group, key)));

        public Task UpsertAsync(Setting setting, CancellationToken ct = default)
        {
            _data[K(setting.Group, setting.Key)] = Copy(setting);
            return Task.CompletedTask;
        }

        public Task UpsertManyAsync(IEnumerable<Setting> settings, CancellationToken ct = default)
        {
            foreach (var s in settings) _data[K(s.Group, s.Key)] = Copy(s);
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

        // Persist a copy so later mutations of the caller's instance cannot alter stored state.
        private static Setting Copy(Setting s) => new()
        {
            Id = s.Id, Group = s.Group, Key = s.Key, Value = s.Value, Type = s.Type,
            IsEncrypted = s.IsEncrypted, UpdatedAt = s.UpdatedAt, UpdatedBy = s.UpdatedBy
        };
    }

    private sealed class NoCache : ISettingCache
    {
        public bool TryGetValue<T>(string key, out T? value) { value = default; return false; }
        public void Set<T>(string key, T value, TimeSpan duration) { }
        public void Remove(string key) { }
    }
}
