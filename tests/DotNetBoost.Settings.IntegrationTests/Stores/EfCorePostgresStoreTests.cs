using DotNetBoost.Settings.Core.Interfaces;
using DotNetBoost.Settings.Core.Models;
using DotNetBoost.Settings.EntityFrameworkCore;
using DotNetBoost.Settings.ProviderTests.Stores;
using Microsoft.EntityFrameworkCore;

namespace DotNetBoost.Settings.IntegrationTests.Stores;

public sealed class PostgresDbContext(DbContextOptions<PostgresDbContext> options)
    : DbContext(options), ISettingDbContext
{
    public DbSet<Setting>           Settings      => Set<Setting>();
    public DbSet<SettingAuditEntry> SettingAudits => Set<SettingAuditEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
        => modelBuilder.ApplySettingsConfiguration(DatabaseProvider.PostgreSql);
}

/// <summary>
/// Runs the full <see cref="SettingStoreContractTests"/> suite against the EF Core store
/// on a real PostgreSQL server, exercising the PostgreSql branch of the model configuration
/// (text columns, the unique index, and the concurrency token rather than a rowversion).
/// </summary>
[Collection(PostgreSqlCollection.Name)]
[Trait("Category", "Integration")]
public sealed class EfCorePostgresStoreTests(PostgreSqlFixture fixture)
    : SettingStoreContractTests, IAsyncLifetime
{
    private readonly List<PostgresDbContext> _contexts = [];

    protected override async Task<ISettingStore> CreateStoreAsync()
        => new EfCoreSettingStore(await NewContextAsync());

    private async Task<PostgresDbContext> NewContextAsync()
    {
        var options = new DbContextOptionsBuilder<PostgresDbContext>()
            .UseNpgsql(await fixture.CreateDatabaseAsync())
            .Options;

        var ctx = new PostgresDbContext(options);
        await ctx.Database.EnsureCreatedAsync();
        _contexts.Add(ctx);
        return ctx;
    }

    [Fact]
    public async Task Data_Survives_A_New_DbContext_On_The_Same_Database()
    {
        var ctx = await NewContextAsync();
        var connectionString = ctx.Database.GetConnectionString()!;
        await new EfCoreSettingStore(ctx).UpsertAsync(
            new Setting { Group = "Mail", Key = "Host", Value = "smtp.live", Type = "System.String" });

        // A brand-new context over the same database proves the write actually hit disk,
        // which an in-memory or SQLite-backed store cannot demonstrate.
        await using var fresh = new PostgresDbContext(
            new DbContextOptionsBuilder<PostgresDbContext>().UseNpgsql(connectionString).Options);

        var reread = await new EfCoreSettingStore(fresh).GetAsync("Mail", "Host");
        Assert.Equal("smtp.live", reread!.Value);
    }

    [Fact]
    public async Task UniqueIndex_RejectsDuplicateGroupKeyPair()
    {
        var ctx = await NewContextAsync();

        ctx.Settings.Add(new Setting { Group = "Mail", Key = "Host", Value = "a", Type = "System.String" });
        ctx.Settings.Add(new Setting { Group = "Mail", Key = "Host", Value = "b", Type = "System.String" });

        // UX_Settings_Group_Key must be enforced by the database, not just by application code.
        await Assert.ThrowsAsync<DbUpdateException>(() => ctx.SaveChangesAsync());
    }

    [Fact]
    public async Task AuditStore_RecordsAndReadsBackHistory()
    {
        var ctx   = await NewContextAsync();
        var audit = new EfCoreAuditStore(ctx);

        await audit.RecordAsync(new SettingAuditEntry
        {
            Group = "Mail", Key = "Port", OldValue = "25", NewValue = "587", ChangedBy = "tester"
        });

        var history = await audit.GetHistoryAsync("Mail", "Port");
        Assert.Single(history);
        Assert.Equal("587", history[0].NewValue);
        Assert.Equal("tester", history[0].ChangedBy);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var ctx in _contexts) await ctx.DisposeAsync();
    }
}
