using DotNetBoost.Settings.Core.Models;
using DotNetBoost.Settings.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace DotNetBoost.Settings.ProviderTests.Stores;

public sealed class TestDbContext(DbContextOptions<TestDbContext> options)
    : DbContext(options), ISettingDbContext
{
    public DbSet<Setting>           Settings      => Set<Setting>();
    public DbSet<SettingAuditEntry> SettingAudits => Set<SettingAuditEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
        => modelBuilder.ApplySettingsConfiguration(DatabaseProvider.Sqlite);
}

public sealed class EfCoreSettingStoreTests : SettingStoreContractTests, IDisposable
{
    private readonly List<TestDbContext> _contexts = [];

    protected override async Task<Core.Interfaces.ISettingStore> CreateStoreAsync()
    {
        var connection = new Microsoft.Data.Sqlite.SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<TestDbContext>()
            .UseSqlite(connection)
            .Options;

        var ctx = new TestDbContext(options);
        await ctx.Database.EnsureCreatedAsync();
        _contexts.Add(ctx);

        return new EfCoreSettingStore(ctx);
    }

    public void Dispose()
    {
        foreach (var ctx in _contexts) ctx.Dispose();
    }
}
