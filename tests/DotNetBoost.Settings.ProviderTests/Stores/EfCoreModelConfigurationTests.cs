using DotNetBoost.Settings.Core.Models;
using DotNetBoost.Settings.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace DotNetBoost.Settings.ProviderTests.Stores;

/// <summary>
/// <c>ApplySettings</c> picks the Value column type and the concurrency-token
/// style per engine, and getting it wrong is the kind of mistake that only shows up as
/// truncated settings long after the migration ran. These tests read the type back off the
/// built model, so the mapping is checked without needing any of these servers to exist.
/// </summary>
public class EfCoreModelConfigurationTests
{
    [Fact]
    public void InferringOverload_ReadsTheEngineOffTheContext()
    {
        // TestDbContext is on SQLite and calls ApplySettings(this).
        Assert.Equal("TEXT", ValueColumnTypeOf(new InferredSqliteContext()));
    }

    [Theory]
    [InlineData(DatabaseProvider.SqlServer,  "nvarchar(max)")]
    [InlineData(DatabaseProvider.PostgreSql, "text")]
    [InlineData(DatabaseProvider.Sqlite,     "TEXT")]
    public void ExplicitOverload_WinsOverWhateverTheContextIsConnectedTo(
        DatabaseProvider provider, string expectedColumnType)
    {
        // Every context here runs on SQLite. Naming the engine explicitly is the documented
        // escape hatch for a model built against one engine and migrated onto another, so it
        // has to override the inference rather than be overridden by it.
        Assert.Equal(expectedColumnType, ValueColumnTypeOf(ContextFor(provider)));
    }

    [Fact]
    public void InferringOverload_OnAnUnmappedProvider_SaysWhatToDoInstead()
    {
        using var ctx = new InMemoryContext();

        var ex = Assert.ThrowsAny<InvalidOperationException>(() => ctx.Model);

        Assert.Contains("ApplySettings(DatabaseProvider)", Flatten(ex), StringComparison.Ordinal);
        Assert.Contains("Microsoft.EntityFrameworkCore.InMemory", Flatten(ex), StringComparison.Ordinal);
    }

    /// <summary>
    /// The whole purpose of the obsolete alias: a context written against the old name keeps
    /// compiling and keeps building the same model. Deprecation warning, not a break.
    /// </summary>
    [Theory]
    [InlineData(DatabaseProvider.SqlServer,  "nvarchar(max)")]
    [InlineData(DatabaseProvider.PostgreSql, "text")]
    [InlineData(DatabaseProvider.Sqlite,     "TEXT")]
    public void TheObsoleteName_StillBuildsTheSameModel(DatabaseProvider provider, string expectedColumnType)
    {
        Assert.Equal(expectedColumnType, ValueColumnTypeOf(ObsoleteContextFor(provider)));

        // And the same as the new name produces, rather than merely something plausible.
        Assert.Equal(ValueColumnTypeOf(ContextFor(provider)), ValueColumnTypeOf(ObsoleteContextFor(provider)));
    }

    /// <summary>EF wraps a throw from OnModelCreating, so assert over the whole chain.</summary>
    private static string Flatten(Exception ex)
    {
        var text = ex.Message;
        for (var inner = ex.InnerException; inner is not null; inner = inner.InnerException)
            text += " | " + inner.Message;
        return text;
    }

    private static string? ValueColumnTypeOf(DbContext ctx)
    {
        using (ctx)
        {
            return ctx.Model
                .FindEntityType(typeof(Setting))!
                .FindProperty(nameof(Setting.Value))!
                .GetColumnType();
        }
    }

    // One context type per provider: EF caches the built model per context type, so a single
    // parameterised type would hand every case back the first model it built.
    private static DbContext ContextFor(DatabaseProvider provider) => provider switch
    {
        DatabaseProvider.SqlServer  => new ExplicitSqlServerContext(),
        DatabaseProvider.PostgreSql => new ExplicitPostgresContext(),
        _                           => new ExplicitSqliteContext()
    };

    private abstract class SqliteBackedContext : DbContext
    {
        protected override void OnConfiguring(DbContextOptionsBuilder options)
            => options.UseSqlite("DataSource=:memory:");
    }

    private sealed class InferredSqliteContext : SqliteBackedContext
    {
        protected override void OnModelCreating(ModelBuilder mb) => mb.ApplySettings(this);
    }

    private sealed class ExplicitSqlServerContext : SqliteBackedContext
    {
        protected override void OnModelCreating(ModelBuilder mb)
            => mb.ApplySettings(DatabaseProvider.SqlServer);
    }

    private sealed class ExplicitPostgresContext : SqliteBackedContext
    {
        protected override void OnModelCreating(ModelBuilder mb)
            => mb.ApplySettings(DatabaseProvider.PostgreSql);
    }

    private sealed class ExplicitSqliteContext : SqliteBackedContext
    {
        protected override void OnModelCreating(ModelBuilder mb)
            => mb.ApplySettings(DatabaseProvider.Sqlite);
    }

    // One context type per provider, as above, each calling the obsolete alias. CS0618 is
    // suppressed here and nowhere else: these exist precisely to exercise the deprecated name.
    private static DbContext ObsoleteContextFor(DatabaseProvider provider) => provider switch
    {
        DatabaseProvider.SqlServer  => new ObsoleteSqlServerContext(),
        DatabaseProvider.PostgreSql => new ObsoletePostgresContext(),
        _                           => new ObsoleteSqliteContext()
    };

#pragma warning disable CS0618 // Deliberately calling the obsolete alias.
    private sealed class ObsoleteSqlServerContext : SqliteBackedContext
    {
        protected override void OnModelCreating(ModelBuilder mb)
            => mb.ApplySettingsConfiguration(DatabaseProvider.SqlServer);
    }

    private sealed class ObsoletePostgresContext : SqliteBackedContext
    {
        protected override void OnModelCreating(ModelBuilder mb)
            => mb.ApplySettingsConfiguration(DatabaseProvider.PostgreSql);
    }

    private sealed class ObsoleteSqliteContext : SqliteBackedContext
    {
        protected override void OnModelCreating(ModelBuilder mb)
            => mb.ApplySettingsConfiguration(DatabaseProvider.Sqlite);
    }
#pragma warning restore CS0618

    private sealed class InMemoryContext : DbContext
    {
        protected override void OnConfiguring(DbContextOptionsBuilder options)
            => options.UseInMemoryDatabase(nameof(InMemoryContext));

        protected override void OnModelCreating(ModelBuilder mb) => mb.ApplySettings(this);
    }
}
