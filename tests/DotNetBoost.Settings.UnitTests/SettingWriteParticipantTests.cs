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
/// The hook that runs inside a write. These use a fake store that keeps what participants
/// enlist apart until a write commits it, so "committed with the write" and "discarded with
/// it" are both observable without a database. The Entity Framework Core store's half of the
/// contract is tested against real databases in the provider and integration tests.
/// </summary>
public class SettingWriteParticipantTests
{
    [Fact]
    public async Task Participant_RunsBeforeTheStore_AndWhatItEnlistsCommitsWithTheWrite()
    {
        var store = new EnlistingStore();
        var rowsWhenCalled = -1;
        var participant = new Participant(async w =>
        {
            rowsWhenCalled = (await store.GetGroupAsync(w.GroupName)).Count;
            store.Enlist("audit");
        });
        var mgr = Build(store, participant);

        await mgr.For<ParticipantSettings>().SetAsync(new ParticipantSettings { Host = "a" });

        Assert.Equal(0, rowsWhenCalled);
        Assert.Equal(["audit"], store.Committed);
        Assert.Empty(store.Enlisted);
    }

    /// <summary>
    /// One write, one <see cref="SettingWrite"/>: an audit entry recorded by a participant and a
    /// log line written by an observer must be joinable on the correlation id.
    /// </summary>
    [Fact]
    public async Task Participant_AndObserver_ReceiveTheSameWrite()
    {
        var participant = new Participant();
        var observer    = new Observer();
        var mgr = Build(new EnlistingStore(), participant, observer);

        await mgr.For<ParticipantSettings>().SetAsync(new ParticipantSettings { Host = "a" });

        Assert.Same(Assert.Single(participant.Writes), Assert.Single(observer.Writes));
    }

    [Fact]
    public async Task Participant_ThatThrows_AbortsTheWrite_AndDiscardsWhatEarlierOnesEnlisted()
    {
        var store    = new EnlistingStore();
        var first    = new Participant(_ => { store.Enlist("audit"); return Task.CompletedTask; });
        var throwing = new Participant(_ => throw new InvalidOperationException("audit store is down"));
        var observer = new Observer();
        var handler  = new ChangedHandler();
        var mgr = Build(store, [first, throwing], observer, handler);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => mgr.For<ParticipantSettings>().SetAsync(new ParticipantSettings { Host = "a" }));

        Assert.Equal("audit store is down", ex.Message);
        Assert.Equal(0, store.Writes);
        Assert.Empty(await store.GetGroupAsync(nameof(ParticipantSettings)));
        Assert.Equal(1, store.Discards);
        Assert.Empty(store.Enlisted);
        Assert.Empty(store.Committed);

        // After-commit hooks hear only about writes that committed.
        Assert.Empty(observer.Writes);
        Assert.False(handler.WasCalled);
    }

    /// <summary>
    /// A participant's failure must leave the cache as it was. Evicting would be harmless here,
    /// but evicting before the store is called is the order that, under a real failure between
    /// the two, serves a value that was never written.
    /// </summary>
    [Fact]
    public async Task Participant_ThatThrows_LeavesTheCachedEntry()
    {
        var store = new EnlistingStore();
        var fail  = false;
        var participant = new Participant(_ => fail ? throw new InvalidOperationException() : Task.CompletedTask);
        var mgr = Build(store, [participant], cache: new SettingCache(new MemoryCache(new MemoryCacheOptions())));
        var settings = mgr.For<ParticipantSettings>();

        await settings.SetAsync(new ParticipantSettings { Host = "a" });
        Assert.Equal("a", (await settings.GetAsync()).Host);

        fail = true;
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => settings.SetAsync(new ParticipantSettings { Host = "b" }));

        var readsBefore = store.GroupReads;
        Assert.Equal("a", (await settings.GetAsync()).Host);
        Assert.Equal(readsBefore, store.GroupReads);
    }

    /// <summary>
    /// The store can fail after every participant succeeded — a concurrency conflict is the
    /// usual case. What they enlisted must not survive it.
    /// </summary>
    [Fact]
    public async Task StoreThatFails_DiscardsWhatParticipantsEnlisted()
    {
        var store = new EnlistingStore { FailWrites = true };
        var participant = new Participant(_ => { store.Enlist("audit"); return Task.CompletedTask; });
        var observer = new Observer();
        var mgr = Build(store, participant, observer);

        await Assert.ThrowsAsync<SettingConcurrencyException>(
            () => mgr.For<ParticipantSettings>().SetAsync(new ParticipantSettings { Host = "a" }));

        Assert.Equal(1, store.Discards);
        Assert.Empty(store.Enlisted);
        Assert.Empty(store.Committed);
        Assert.Empty(observer.Writes);
    }

    /// <summary>
    /// Running a participant against a store that cannot commit what it enlists would look
    /// like an audit trail and not be one. Refused at run time too, because <c>Build()</c>
    /// does not see registrations made outside the builder.
    /// </summary>
    [Fact]
    public async Task Participant_WithAStoreThatCannotEnlist_RefusesTheWrite()
    {
        var store = new PlainStore();
        var participant = new Participant();
        var services = new ServiceCollection();
        services.AddSingleton<ISettingWriteParticipant>(participant);
        var mgr = new SettingManager(store, new NoCache(), services.BuildServiceProvider(),
            NullLogger<SettingManager>.Instance);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => mgr.For<ParticipantSettings>().SetAsync(new ParticipantSettings { Host = "a" }));

        Assert.Contains(nameof(PlainStore), ex.Message, StringComparison.Ordinal);
        Assert.Empty(participant.Writes);
        Assert.Equal(0, store.Writes);
    }

    [Fact]
    public async Task Participant_RunsOnEveryWritePath()
    {
        var participant = new Participant();
        var mgr = Build(new EnlistingStore(), participant);
        var settings = mgr.For<ParticipantSettings>();

        await settings.SetAsync(new ParticipantSettings { Host = "a" });
        await settings.SetAsync(x => x.Port, 587);
        await settings.ClearAsync();

        Assert.Equal(
            [SettingWriteKind.Updated, SettingWriteKind.Updated, SettingWriteKind.Cleared],
            participant.Writes.Select(w => w.Kind));
    }

    [Fact]
    public async Task Write_ThatChangesNothing_CallsNoParticipant()
    {
        var participant = new Participant();
        var mgr = Build(new EnlistingStore(), participant);

        await mgr.For<ParticipantSettings>().SetAsync(new ParticipantSettings { Host = "a" });
        await mgr.For<ParticipantSettings>().SetAsync(new ParticipantSettings { Host = "a" });

        Assert.Single(participant.Writes);
    }

    /// <summary>
    /// A participant on a clear runs while the rows still exist and is told what they held,
    /// which is what an audit of a clear records.
    /// </summary>
    [Fact]
    public async Task Clear_Participant_SeesTheValuesAboutToBeDeleted()
    {
        var store = new EnlistingStore();
        var participant = new Participant();
        var mgr = Build(store, participant);

        await mgr.For<ParticipantSettings>().SetAsync(new ParticipantSettings { Host = "a" });
        await mgr.For<ParticipantSettings>().ClearAsync();

        var cleared = participant.Writes[^1];
        Assert.Equal(SettingWriteKind.Cleared, cleared.Kind);
        Assert.Equal(
            [
                new SettingChange(nameof(ParticipantSettings.Host), "a", null, IsRedacted: false),
                new SettingChange(nameof(ParticipantSettings.Port), "0", null, IsRedacted: false)
            ],
            cleared.Changes);
    }

    [Fact]
    public async Task Clear_Participant_ThatThrows_LeavesTheRows()
    {
        var store = new EnlistingStore();
        var fail  = false;
        var participant = new Participant(_ => fail ? throw new InvalidOperationException() : Task.CompletedTask);
        var mgr = Build(store, participant);

        await mgr.For<ParticipantSettings>().SetAsync(new ParticipantSettings { Host = "a" });
        var stored = (await store.GetGroupAsync(nameof(ParticipantSettings))).Count;
        fail = true;

        await Assert.ThrowsAsync<InvalidOperationException>(() => mgr.For<ParticipantSettings>().ClearAsync());

        Assert.NotEqual(0, stored);
        Assert.Equal(stored, (await store.GetGroupAsync(nameof(ParticipantSettings))).Count);
        Assert.Equal(1, store.Discards);
    }

    [Fact]
    public void Validator_RefusesAParticipant_ForAProviderThatCannotEnlist()
    {
        var services = new ServiceCollection();
        services.AddScoped<ISettingWriteParticipant, Participant>();

        var ex = Assert.Throws<InvalidOperationException>(
            () => SettingBuilderValidator.ValidateWriteParticipants(services, "Dapper", providerSupportsParticipants: false));

        Assert.Contains("'Dapper'", ex.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(Participant), ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validator_AcceptsAParticipant_ForAProviderThatCanEnlist()
    {
        var services = new ServiceCollection();
        services.AddScoped<ISettingWriteParticipant, Participant>();

        SettingBuilderValidator.ValidateWriteParticipants(services, "EntityFrameworkCore", providerSupportsParticipants: true);
    }

    [Fact]
    public void Validator_AcceptsAnyProvider_WhenNoParticipantIsRegistered()
        => SettingBuilderValidator.ValidateWriteParticipants(new ServiceCollection(), "Dapper", providerSupportsParticipants: false);

    [Fact]
    public void UseWriteParticipant_Registers_AndTheGuardRecordsSupport()
    {
        var builder = new ServiceCollection().AddSettings();
        SettingBuilderGuard.EnsureProviderNotConfigured(builder, "Fake", supportsWriteParticipants: true);

        builder.UseWriteParticipant<Participant>();

        Assert.Contains(builder.Services, d =>
            d.ServiceType == typeof(ISettingWriteParticipant) && d.ImplementationType == typeof(Participant));
        Assert.True(builder.SupportsWriteParticipants);
    }

    [Fact]
    public void Guard_WithoutTheFlag_RecordsNoSupport()
    {
        var builder = new ServiceCollection().AddSettings();
        SettingBuilderGuard.EnsureProviderNotConfigured(builder, "Fake");

        Assert.False(builder.SupportsWriteParticipants);
    }

    private static SettingManager Build(
        ISettingStore store, Participant participant, Observer? observer = null)
        => Build(store, [participant], observer);

    private static SettingManager Build(
        ISettingStore store, Participant[] participants, Observer? observer = null,
        ChangedHandler? handler = null, ISettingCache? cache = null)
    {
        var services = new ServiceCollection();
        foreach (var p in participants)
            services.AddSingleton<ISettingWriteParticipant>(p);
        if (observer is not null)
            services.AddSingleton<ISettingWriteObserver>(observer);
        if (handler is not null)
            services.AddSingleton<ISettingChangedHandler<ParticipantSettings>>(handler);

        return new SettingManager(store, cache ?? new NoCache(), services.BuildServiceProvider(),
            NullLogger<SettingManager>.Instance);
    }

    [SettingGroup("participant")]
    public class ParticipantSettings
    {
        public string? Host { get; set; }
        public int Port { get; set; }
    }

    private sealed class Participant(Func<SettingWrite, Task>? onWriting = null) : ISettingWriteParticipant
    {
        public Participant() : this(null) { }

        public List<SettingWrite> Writes { get; } = [];

        public Task OnWritingAsync(SettingWrite write, CancellationToken ct = default)
        {
            Writes.Add(write);
            return onWriting?.Invoke(write) ?? Task.CompletedTask;
        }
    }

    private sealed class Observer : ISettingWriteObserver
    {
        public List<SettingWrite> Writes { get; } = [];

        public Task OnWrittenAsync(SettingWrite write, CancellationToken ct = default)
        {
            Writes.Add(write);
            return Task.CompletedTask;
        }
    }

    private sealed class ChangedHandler : ISettingChangedHandler<ParticipantSettings>
    {
        public bool WasCalled { get; private set; }

        public Task OnChangedAsync(ParticipantSettings previous, ParticipantSettings current, CancellationToken ct = default)
        {
            WasCalled = true;
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Holds what participants enlist apart from what it has stored, and commits it only when
    /// one of its writes succeeds — the contract an enlisting store has to keep.
    /// </summary>
    private sealed class EnlistingStore : PlainStore, IEnlistingSettingStore
    {
        public List<string> Enlisted  { get; } = [];
        public List<string> Committed { get; } = [];
        public int          Discards  { get; private set; }
        public bool         FailWrites { get; init; }

        public void Enlist(string item) => Enlisted.Add(item);

        public void DiscardEnlisted()
        {
            Discards++;
            Enlisted.Clear();
        }

        protected override void BeforeWrite()
        {
            if (FailWrites) throw new SettingConcurrencyException(nameof(ParticipantSettings), "Host");
        }

        protected override void AfterWrite()
        {
            Committed.AddRange(Enlisted);
            Enlisted.Clear();
        }
    }

    /// <summary>A store whose reads reflect its writes, and which cannot enlist anything.</summary>
    private class PlainStore : ISettingStore
    {
        private readonly Dictionary<string, Setting> _data = new(StringComparer.Ordinal);
        private static string K(string g, string k) => g + "|" + k;

        public int Writes     { get; private set; }
        public int GroupReads { get; private set; }

        protected virtual void BeforeWrite() { }
        protected virtual void AfterWrite()  { }

        public Task<IReadOnlyList<Setting>> GetGroupAsync(string group, CancellationToken ct = default)
        {
            GroupReads++;
            return Task.FromResult<IReadOnlyList<Setting>>(_data.Values.Where(x => x.Group == group).ToList());
        }

        public Task<Setting?> GetAsync(string group, string key, CancellationToken ct = default)
            => Task.FromResult(_data.GetValueOrDefault(K(group, key)));

        public Task UpsertAsync(Setting setting, CancellationToken ct = default)
            => UpsertManyAsync([setting], ct);

        public Task UpsertManyAsync(IEnumerable<Setting> settings, CancellationToken ct = default)
        {
            BeforeWrite();
            foreach (var s in settings)
            {
                _data[K(s.Group, s.Key)] = new Setting
                {
                    Id = s.Id, Group = s.Group, Key = s.Key, Value = s.Value, Type = s.Type,
                    IsEncrypted = s.IsEncrypted, UpdatedAt = s.UpdatedAt
                };
            }
            Writes++;
            AfterWrite();
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string group, string key, CancellationToken ct = default)
        {
            _data.Remove(K(group, key));
            return Task.CompletedTask;
        }

        public Task DeleteGroupAsync(string group, CancellationToken ct = default)
        {
            BeforeWrite();
            foreach (var k in _data.Where(x => x.Value.Group == group).Select(x => x.Key).ToList())
                _data.Remove(k);
            Writes++;
            AfterWrite();
            return Task.CompletedTask;
        }

        public Task<int> CountAsync(string group, CancellationToken ct = default)
            => Task.FromResult(_data.Values.Count(x => x.Group == group));
    }

    private sealed class NoCache : ISettingCache
    {
        public bool TryGetValue<T>(string key, out T? value) { value = default; return false; }
        public void Set<T>(string key, T value, TimeSpan duration) { }
        public void Remove(string key) { }
    }
}
