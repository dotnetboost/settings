using DotNetBoost.Settings.Core;
using DotNetBoost.Settings.Core.Attributes;
using DotNetBoost.Settings.Core.Interfaces;
using DotNetBoost.Settings.Core.Models;
using DotNetBoost.Settings.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using System.Security.Cryptography;

namespace DotNetBoost.Settings.UnitTests;

/// <summary>
/// Regression tests for the audit trail. These use a stateful fake store rather than a mock:
/// the bug they cover (OldValue read back *after* the write) is invisible to a store whose
/// reads are not affected by its writes.
/// </summary>
public class AuditTrailTests
{
    [Fact]
    public async Task Audit_RecordsPreviousValue_AsOldValue()
    {
        var (mgr, _, audit) = Build();

        await mgr.For<AuditSettings>().SetAsync(new AuditSettings { Host = "old.example.com" });
        audit.Entries.Clear();
        await mgr.For<AuditSettings>().SetAsync(new AuditSettings { Host = "new.example.com" });

        var entry = audit.Entries.Single(e => e.Key == nameof(AuditSettings.Host));
        Assert.Equal("old.example.com", entry.OldValue);
        Assert.Equal("new.example.com", entry.NewValue);
    }

    [Fact]
    public async Task Audit_FirstWrite_RecordsEmptyOldValue()
    {
        var (mgr, _, audit) = Build();

        await mgr.For<AuditSettings>().SetAsync(new AuditSettings { Host = "first.example.com" });

        var entry = audit.Entries.Single(e => e.Key == nameof(AuditSettings.Host));
        Assert.Equal(string.Empty, entry.OldValue);
        Assert.Equal("first.example.com", entry.NewValue);
    }

    [Fact]
    public async Task Audit_RedactsEncryptedValues_OnBothSides()
    {
        var (mgr, _, audit) = Build(new ReversibleEncryptor());

        await mgr.For<AuditSettings>().SetAsync(new AuditSettings { Host = "h", Secret = "s3cret" });
        audit.Entries.Clear();
        await mgr.For<AuditSettings>().SetAsync(new AuditSettings { Host = "h", Secret = "n3wer" });

        var entry = audit.Entries.Single(e => e.Key == nameof(AuditSettings.Secret));
        Assert.Equal("[encrypted]", entry.OldValue);
        Assert.Equal("[encrypted]", entry.NewValue);
        Assert.DoesNotContain("s3cret", entry.OldValue, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Audit_SkipsUnchangedProperties()
    {
        var (mgr, _, audit) = Build();

        await mgr.For<AuditSettings>().SetAsync(new AuditSettings { Host = "same.example.com" });
        audit.Entries.Clear();
        await mgr.For<AuditSettings>().SetAsync(new AuditSettings { Host = "same.example.com" });

        Assert.Empty(audit.Entries);
    }

    [Fact]
    public async Task Audit_RecordsOnlyTheChangedProperty()
    {
        var (mgr, _, audit) = Build();

        await mgr.For<MultiSettings>().SetAsync(new MultiSettings { Host = "a", Port = 25 });
        audit.Entries.Clear();
        await mgr.For<MultiSettings>().SetAsync(new MultiSettings { Host = "a", Port = 587 });

        var entry = Assert.Single(audit.Entries);
        Assert.Equal(nameof(MultiSettings.Port), entry.Key);
        Assert.Equal("25", entry.OldValue);
        Assert.Equal("587", entry.NewValue);
    }

    [Fact]
    public async Task Audit_SkipsUnchangedSensitiveProperty_DespiteFreshCiphertext()
    {
        // The real AES-GCM encryptor, not a stub: it draws a new nonce per call, so an
        // unchanged secret serialises to different ciphertext on every save. Comparing
        // ciphertext would report a change here; comparing plaintext must not.
        var key       = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var encryptor = new AesSettingEncryptor(key);

        // Guards the premise: re-encrypting the same plaintext really does produce different
        // bytes, so comparing stored ciphertext would report a change on every save.
        Assert.NotEqual(encryptor.Encrypt("s3cret"), encryptor.Encrypt("s3cret"));

        var (mgr, store, audit) = Build(encryptor);

        await mgr.For<AuditSettings>().SetAsync(new AuditSettings { Host = "h", Secret = "s3cret" });
        var firstCiphertext = (await store.GetAsync("AuditSettings", nameof(AuditSettings.Secret)))!.Value;
        audit.Entries.Clear();

        await mgr.For<AuditSettings>().SetAsync(new AuditSettings { Host = "h", Secret = "s3cret" });

        Assert.Empty(audit.Entries);
        // Unchanged properties are not rewritten at all, so the stored bytes stay put.
        Assert.Equal(firstCiphertext, (await store.GetAsync("AuditSettings", nameof(AuditSettings.Secret)))!.Value);
    }

    [Fact]
    public async Task Audit_RecordsChangedSensitiveProperty_Redacted()
    {
        var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var (mgr, _, audit) = Build(new AesSettingEncryptor(key));

        await mgr.For<AuditSettings>().SetAsync(new AuditSettings { Host = "h", Secret = "s3cret" });
        audit.Entries.Clear();
        await mgr.For<AuditSettings>().SetAsync(new AuditSettings { Host = "h", Secret = "n3wer" });

        var entry = Assert.Single(audit.Entries);
        Assert.Equal(nameof(AuditSettings.Secret), entry.Key);
        Assert.Equal("[encrypted]", entry.OldValue);
        Assert.Equal("[encrypted]", entry.NewValue);
    }

    [Fact]
    public async Task Audit_NotQueried_WhenNoAuditStoreRegistered()
    {
        var store = new Mock<ISettingStore>();
        store.Setup(x => x.GetGroupAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
             .ReturnsAsync([]);
        var mgr = new SettingManager(store.Object, new NoCache(),
            new ServiceCollection().BuildServiceProvider(), NullLogger<SettingManager>.Instance);

        await mgr.For<AuditSettings>().SetAsync(new AuditSettings { Host = "h" });

        // Exactly one read. Change detection, concurrency tokens, the audit before-values and
        // the change-handler model all come from that single snapshot.
        store.Verify(x => x.GetGroupAsync("AuditSettings", It.IsAny<CancellationToken>()), Times.Once);
    }

    private static (ISettingManager, FakeStore, FakeAuditStore) Build(ISettingEncryptor? encryptor = null)
    {
        var store = new FakeStore();
        var audit = new FakeAuditStore();
        var mgr = new SettingManager(store, new NoCache(),
            new ServiceCollection().BuildServiceProvider(),
            NullLogger<SettingManager>.Instance, encryptor, audit);
        return (mgr, store, audit);
    }

    public class AuditSettings
    {
        public string? Host { get; set; }
        [Sensitive] public string? Secret { get; set; }
    }

    public class MultiSettings
    {
        public string? Host { get; set; }
        public int Port { get; set; }
    }

    private sealed class ReversibleEncryptor : ISettingEncryptor
    {
        public string Encrypt(string plaintext)  => "enc:" + plaintext;
        public string Decrypt(string ciphertext) => ciphertext["enc:".Length..];
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

    private sealed class FakeAuditStore : ISettingAuditStore
    {
        public List<SettingAuditEntry> Entries { get; } = [];

        public Task RecordAsync(SettingAuditEntry entry, CancellationToken ct = default)
        {
            Entries.Add(entry);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<SettingAuditEntry>> GetHistoryAsync(
            string group, string? key = null, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<SettingAuditEntry>>(Entries);
    }

    private sealed class NoCache : ISettingCache
    {
        public bool TryGetValue<T>(string key, out T? value) { value = default; return false; }
        public void Set<T>(string key, T value, TimeSpan duration) { }
        public void Remove(string key) { }
    }
}

/// <summary>Regression tests for <c>WithCacheDuration()</c> reaching the manager.</summary>
public class SettingOptionsTests
{
    [Fact]
    public async Task WithCacheDuration_IsAppliedToCachedEntries()
    {
        var (mgr, cache) = BuildWith(b => b.WithCacheDuration(TimeSpan.FromMinutes(5)));

        await mgr.For<OptionsSettings>().GetAsync();

        cache.Verify(x => x.Set("dnb:setting:OptionsSettings",
            It.IsAny<OptionsSettings>(), TimeSpan.FromMinutes(5)), Times.Once);
    }

    [Fact]
    public async Task CacheDuration_DefaultsToTenMinutes()
    {
        var (mgr, cache) = BuildWith(_ => { });

        await mgr.For<OptionsSettings>().GetAsync();

        cache.Verify(x => x.Set("dnb:setting:OptionsSettings",
            It.IsAny<OptionsSettings>(), TimeSpan.FromMinutes(10)), Times.Once);
    }

    [Fact]
    public void WithCacheDuration_RejectsNonPositiveDuration()
    {
        var builder = new ServiceCollection().AddSettings();
        Assert.Throws<ArgumentOutOfRangeException>(() => builder.WithCacheDuration(TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => builder.WithCacheDuration(TimeSpan.FromSeconds(-1)));
    }

    [Fact]
    public void AddSettings_RegistersOptions()
    {
        var services = new ServiceCollection();
        services.AddSettings().WithCacheDuration(TimeSpan.FromHours(2));

        var options = services.BuildServiceProvider().GetRequiredService<SettingOptions>();

        Assert.Equal(TimeSpan.FromHours(2), options.CacheDuration);
    }

    // Resolves the manager through DI so the test covers the real registration path,
    // which is where the builder-to-manager wiring was previously lost.
    private static (ISettingManager, Mock<ISettingCache>) BuildWith(Action<SettingBuilder> configure)
    {
        var store = new Mock<ISettingStore>();
        store.Setup(x => x.GetGroupAsync("OptionsSettings", It.IsAny<CancellationToken>()))
             .ReturnsAsync([]);

        var cache = new Mock<ISettingCache>();
        OptionsSettings? miss = null;
        cache.Setup(x => x.TryGetValue<OptionsSettings>(It.IsAny<string>(), out miss)).Returns(false);

        var services = new ServiceCollection();
        services.AddSingleton(store.Object);
        services.AddSingleton(cache.Object);   // registered first so AddSettings' TryAdd defers
        services.AddLogging();

        configure(services.AddSettings());

        return (services.BuildServiceProvider().GetRequiredService<ISettingManager>(), cache);
    }

    public class OptionsSettings { public int Port { get; set; } }
}
