using DotNetBoost.Settings.Core;
using DotNetBoost.Settings.Core.Attributes;
using DotNetBoost.Settings.Core.Interfaces;
using DotNetBoost.Settings.Core.Models;
using DotNetBoost.Settings.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DotNetBoost.Settings.ProviderTests.Stores;

/// <summary>A context mapping the settings beside an application entity, as a real one does.</summary>
public sealed class ParticipantDbContext(DbContextOptions<ParticipantDbContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplySettings(this);
        modelBuilder.Entity<AuditRow>(b =>
        {
            b.ToTable("AuditRows");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Group).HasMaxLength(191);
            b.Property(x => x.Kind).HasMaxLength(32);
            b.Property(x => x.CorrelationId).HasMaxLength(191);
        });
    }
}

/// <summary>What an auditing participant records: one row per write.</summary>
public sealed class AuditRow
{
    public Guid   Id            { get; set; } = Guid.NewGuid();
    public string Group         { get; set; } = "";
    public string Kind          { get; set; } = "";
    public string CorrelationId { get; set; } = "";
    public int    ChangeCount   { get; set; }
}

/// <summary>An entity no context here maps.</summary>
public sealed class UnmappedRow
{
    public Guid Id { get; set; }
}

[SettingGroup("ef-participant")]
public sealed class EfParticipantSettings
{
    public string? Host { get; set; }
    public string? Mode { get; set; }
}

/// <summary>
/// The Entity Framework Core store's half of the participant contract, against a real
/// database: what a participant enlists commits with the write or not at all, and a write
/// that does not commit leaves nothing tracked for the application's next save to pick up.
/// Each engine runs it through a subclass; SQLite here, PostgreSQL and SQL Server in the
/// integration tests.
/// </summary>
public abstract class EfCoreWriteParticipantTests : IAsyncLifetime
{
    private readonly List<ServiceProvider> _providers = [];

    /// <summary>A new, empty database, as the options it is reached through.</summary>
    protected abstract Task<Action<DbContextOptionsBuilder>> NewDatabaseAsync();

    [Fact]
    public async Task Set_CommitsTheEnlistedRow_WithTheSettings()
    {
        var (sp, _) = await BuildAsync();

        await WithManagerAsync(sp, m => m.For<EfParticipantSettings>().SetAsync(new EfParticipantSettings { Host = "a" }));

        Assert.Equal("a", (await ReadSettingsAsync(sp)).Host);
        var audit = Assert.Single(await ReadAuditAsync(sp));
        Assert.Equal(nameof(EfParticipantSettings), audit.Group);
        Assert.Equal(nameof(SettingWriteKind.Updated), audit.Kind);
    }

    [Fact]
    public async Task SetProperty_CommitsTheEnlistedRow_WithTheSetting()
    {
        var (sp, _) = await BuildAsync();

        await WithManagerAsync(sp, m => m.For<EfParticipantSettings>().SetAsync(x => x.Mode, "strict"));

        Assert.Equal("strict", (await ReadSettingsAsync(sp)).Mode);
        Assert.Single(await ReadAuditAsync(sp));
    }

    [Fact]
    public async Task Set_ParticipantThrows_CommitsNothing_AndLeavesNothingTracked()
    {
        var (sp, hooks) = await BuildAsync();
        hooks.ThrowAfterEnlisting = true;

        using var scope = sp.CreateScope();
        var mgr = scope.ServiceProvider.GetRequiredService<ISettingManager>();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => mgr.For<EfParticipantSettings>().SetAsync(new EfParticipantSettings { Host = "a" }));

        await AssertNothingPendingAsync(scope);
        Assert.Null((await ReadSettingsAsync(sp)).Host);
        Assert.Empty(await ReadAuditAsync(sp));
    }

    /// <summary>
    /// The case participants can do nothing about: they all succeeded, then the conditional
    /// update lost to another writer. Neither the audit row nor the modified setting may stay
    /// tracked, or the application's next save would write both — an audit of a write that
    /// never happened, and the losing value over the winner's.
    /// </summary>
    [Fact]
    public async Task Set_ConcurrencyConflict_CommitsNothing_AndLeavesNothingTracked()
    {
        var (sp, hooks) = await BuildAsync();
        await WithManagerAsync(sp, m => m.For<EfParticipantSettings>().SetAsync(new EfParticipantSettings { Host = "a" }));

        // Another writer changes the row after this write read it and before it saves.
        hooks.BeforeEnlisting = () => ChangeHostElsewhereAsync(sp, "theirs");

        using var scope = sp.CreateScope();
        var mgr = scope.ServiceProvider.GetRequiredService<ISettingManager>();

        await Assert.ThrowsAsync<SettingConcurrencyException>(
            () => mgr.For<EfParticipantSettings>().SetAsync(new EfParticipantSettings { Host = "mine" }));

        await AssertNothingPendingAsync(scope);
        Assert.Equal("theirs", (await ReadSettingsAsync(sp)).Host);
        Assert.Single(await ReadAuditAsync(sp));
    }

    [Fact]
    public async Task Clear_CommitsTheDelete_AndTheEnlistedRow_Together()
    {
        var (sp, _) = await BuildAsync();
        await WithManagerAsync(sp, m => m.For<EfParticipantSettings>().SetAsync(new EfParticipantSettings { Host = "a" }));

        await WithManagerAsync(sp, m => m.For<EfParticipantSettings>().ClearAsync());

        Assert.Null((await ReadSettingsAsync(sp)).Host);
        var audits = await ReadAuditAsync(sp);
        var cleared = Assert.Single(audits, a => a.Kind == nameof(SettingWriteKind.Cleared));
        Assert.Equal(1, cleared.ChangeCount);
    }

    [Fact]
    public async Task Clear_ParticipantThrows_LeavesTheRows_AndNothingTracked()
    {
        var (sp, hooks) = await BuildAsync();
        await WithManagerAsync(sp, m => m.For<EfParticipantSettings>().SetAsync(new EfParticipantSettings { Host = "a" }));
        hooks.ThrowAfterEnlisting = true;

        using var scope = sp.CreateScope();
        var mgr = scope.ServiceProvider.GetRequiredService<ISettingManager>();

        await Assert.ThrowsAsync<InvalidOperationException>(() => mgr.For<EfParticipantSettings>().ClearAsync());

        await AssertNothingPendingAsync(scope);
        Assert.Equal("a", (await ReadSettingsAsync(sp)).Host);
        Assert.Single(await ReadAuditAsync(sp));
    }

    /// <summary>
    /// A clear inside the application's own transaction joins it, rather than committing on its
    /// own, so the application's rollback takes back the delete and the enlisted row together.
    /// Run through the execution strategy, as an application on a retrying one has to.
    /// </summary>
    [Fact]
    public async Task Clear_InsideTheCallersTransaction_IsUndoneByItsRollback()
    {
        var (sp, _) = await BuildAsync();
        await WithManagerAsync(sp, m => m.For<EfParticipantSettings>().SetAsync(new EfParticipantSettings { Host = "a" }));

        using (var scope = sp.CreateScope())
        {
            var db  = scope.ServiceProvider.GetRequiredService<ParticipantDbContext>();
            var mgr = scope.ServiceProvider.GetRequiredService<ISettingManager>();

            await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
            {
                await using var tx = await db.Database.BeginTransactionAsync();
                await mgr.For<EfParticipantSettings>().ClearAsync();
                await tx.RollbackAsync();
            });
        }

        Assert.Equal("a", (await ReadSettingsAsync(sp)).Host);
        Assert.Single(await ReadAuditAsync(sp));
    }

    [Fact]
    public async Task Clear_InsideTheCallersTransaction_CommitsWithIt()
    {
        var (sp, _) = await BuildAsync();
        await WithManagerAsync(sp, m => m.For<EfParticipantSettings>().SetAsync(new EfParticipantSettings { Host = "a" }));

        using (var scope = sp.CreateScope())
        {
            var db  = scope.ServiceProvider.GetRequiredService<ParticipantDbContext>();
            var mgr = scope.ServiceProvider.GetRequiredService<ISettingManager>();

            await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
            {
                await using var tx = await db.Database.BeginTransactionAsync();
                await mgr.For<EfParticipantSettings>().ClearAsync();
                await tx.CommitAsync();
            });
        }

        Assert.Null((await ReadSettingsAsync(sp)).Host);
        Assert.Equal(2, (await ReadAuditAsync(sp)).Count);
    }

    /// <summary>
    /// An entity the settings context does not map could never be saved with the write. It
    /// fails the write on the spot rather than vanishing.
    /// </summary>
    [Fact]
    public async Task Participant_EnlistingAnUnmappedEntity_FailsTheWrite()
    {
        var (sp, hooks) = await BuildAsync();
        hooks.EnlistUnmapped = true;

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => WithManagerAsync(sp, m => m.For<EfParticipantSettings>().SetAsync(new EfParticipantSettings { Host = "a" })));

        Assert.Null((await ReadSettingsAsync(sp)).Host);
    }

    private async Task<(ServiceProvider, ParticipantHooks)> BuildAsync()
    {
        var configure = await NewDatabaseAsync();
        var hooks     = new ParticipantHooks();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<ParticipantDbContext>(configure);
        services.AddSingleton(hooks);
        services.AddSettings()
            .UseEntityFrameworkCore<ParticipantDbContext>()
            .UseWriteParticipant<AuditParticipant>();

        var sp = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        _providers.Add(sp);

        using var scope = sp.CreateScope();
        await scope.ServiceProvider.GetRequiredService<ParticipantDbContext>().Database.EnsureCreatedAsync();

        return (sp, hooks);
    }

    private static async Task WithManagerAsync(IServiceProvider sp, Func<ISettingManager, Task> act)
    {
        using var scope = sp.CreateScope();
        await act(scope.ServiceProvider.GetRequiredService<ISettingManager>());
    }

    private static async Task<EfParticipantSettings> ReadSettingsAsync(IServiceProvider sp)
    {
        using var scope = sp.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ISettingManager>()
            .For<EfParticipantSettings>().GetAsync(refreshCache: true);
    }

    private static async Task<List<AuditRow>> ReadAuditAsync(IServiceProvider sp)
    {
        using var scope = sp.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ParticipantDbContext>()
            .Set<AuditRow>().AsNoTracking().ToListAsync();
    }

    private static async Task ChangeHostElsewhereAsync(IServiceProvider sp, string value)
    {
        using var scope = sp.CreateScope();
        var db  = scope.ServiceProvider.GetRequiredService<ParticipantDbContext>();
        var row = await db.Set<Setting>().SingleAsync(x => x.Group == nameof(EfParticipantSettings) && x.Key == "Host");
        row.Value      = value;
        row.RowVersion = Setting.NewRowVersion();
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Nothing left for the application's next save on the same context to write: no entry
    /// added, modified or deleted, and a save that writes nothing.
    /// </summary>
    private static async Task AssertNothingPendingAsync(IServiceScope scope)
    {
        var db = scope.ServiceProvider.GetRequiredService<ParticipantDbContext>();

        Assert.DoesNotContain(db.ChangeTracker.Entries(), e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted);
        Assert.Equal(0, await db.SaveChangesAsync());
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public virtual async Task DisposeAsync()
    {
        foreach (var sp in _providers) await sp.DisposeAsync();
    }

    public sealed class ParticipantHooks
    {
        /// <summary>Runs once, on the next write, before the participant enlists.</summary>
        public Func<Task>? BeforeEnlisting     { get; set; }
        public bool        ThrowAfterEnlisting { get; set; }
        public bool        EnlistUnmapped      { get; set; }
    }

    /// <summary>An auditing participant, written the way the docs say to write one.</summary>
    public sealed class AuditParticipant(EfCoreSettingWriteScope scope, ParticipantHooks hooks) : ISettingWriteParticipant
    {
        public async Task OnWritingAsync(SettingWrite write, CancellationToken ct = default)
        {
            if (hooks.BeforeEnlisting is { } before)
            {
                hooks.BeforeEnlisting = null;
                await before();
            }

            scope.Add(hooks.EnlistUnmapped
                ? new UnmappedRow { Id = Guid.NewGuid() }
                : new AuditRow
                {
                    Group         = write.GroupName,
                    Kind          = write.Kind.ToString(),
                    CorrelationId = write.CorrelationId,
                    ChangeCount   = write.Changes.Count
                });

            if (hooks.ThrowAfterEnlisting)
                throw new InvalidOperationException("participant failed after enlisting");
        }
    }
}

public sealed class EfCoreSqliteWriteParticipantTests : EfCoreWriteParticipantTests
{
    private readonly List<SqliteConnection> _connections = [];

    protected override async Task<Action<DbContextOptionsBuilder>> NewDatabaseAsync()
    {
        // An in-memory database lives as long as its connection, so every context in a test
        // shares this one, held open until the test ends.
        var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        _connections.Add(connection);
        return o => o.UseSqlite(connection);
    }

    public override async Task DisposeAsync()
    {
        await base.DisposeAsync();
        foreach (var c in _connections) await c.DisposeAsync();
    }
}

/// <summary>Registration and wiring checks that need no database round trip.</summary>
public class EfCoreWriteParticipantRegistrationTests
{
    [Fact]
    public void Store_RefusesAWriteScope_OverAnotherContext()
    {
        var options = new DbContextOptionsBuilder<ParticipantDbContext>().UseSqlite("DataSource=:memory:").Options;
        using var one   = new ParticipantDbContext(options);
        using var other = new ParticipantDbContext(options);

        Assert.Throws<ArgumentException>(() => new EfCoreSettingStore(one, new EfCoreSettingWriteScope(other)));
    }

    [Fact]
    public void UseEntityFrameworkCore_AcceptsAParticipant_InBuild()
    {
        var services = new ServiceCollection();
        services.AddDbContext<ParticipantDbContext>(o => o.UseSqlite("DataSource=:memory:"));

        services.AddSettings()
            .UseEntityFrameworkCore<ParticipantDbContext>()
            .UseWriteParticipant<EfCoreWriteParticipantTests.AuditParticipant>()
            .Build();
    }

    [Fact]
    public void UseDapper_RefusesAParticipant_InBuild()
    {
        var services = new ServiceCollection();

        var builder = services.AddSettings()
            .UseDapper(_ => new SqliteConnection("DataSource=:memory:"))
            .UseWriteParticipant<EfCoreWriteParticipantTests.AuditParticipant>();

        var ex = Assert.Throws<InvalidOperationException>(() => builder.Build());
        Assert.Contains("'Dapper'", ex.Message, StringComparison.Ordinal);
    }
}
