using DotNetBoost.Settings.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace DotNetBoost.Settings.ProviderTests.Stores;

/// <summary>
/// The whole registration a consuming context needs: no interface, no DbSet properties, and
/// the engine taken from the context rather than restated.
/// </summary>
public sealed class TestDbContext(DbContextOptions<TestDbContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
        => modelBuilder.ApplySettingsConfiguration(this);
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
