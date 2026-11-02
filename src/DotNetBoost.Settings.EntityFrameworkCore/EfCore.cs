using DotNetBoost.Settings.Core;
using DotNetBoost.Settings.Core.Interfaces;
using DotNetBoost.Settings.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.Extensions.DependencyInjection;

namespace DotNetBoost.Settings.EntityFrameworkCore;

/// <summary>Relational engines the EF Core provider ships column mappings for.</summary>
public enum DatabaseProvider
{
    /// <summary>Microsoft SQL Server.</summary>
    SqlServer,

    /// <summary>PostgreSQL.</summary>
    PostgreSql,

    /// <summary>SQLite.</summary>
    Sqlite
}

/// <summary>
/// No longer required. The EF Core store reads its entities through
/// <see cref="DbContext.Set{TEntity}()"/>, so a context needs neither this interface nor the
/// <c>DbSet</c> properties it declares — <c>ApplySettings</c> in <c>OnModelCreating</c> is the
/// whole registration.
/// </summary>
/// <remarks>
/// Kept for one release so existing contexts keep compiling. Delete the
/// <c>: ISettingDbContext</c> and the <c>DbSet</c> property; nothing else changes.
/// </remarks>
[Obsolete("ISettingDbContext is no longer used. Remove it and the DbSet properties from your " +
          "DbContext; UseEntityFrameworkCore<TContext>() only needs a DbContext. " +
          "This interface will be removed in the next release.")]
public interface ISettingDbContext
{
    /// <summary>The persisted settings rows.</summary>
    DbSet<Setting> Settings { get; }

    /// <summary>Persists pending changes.</summary>
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Change-tracking entry for an entity. Needed to state the concurrency token a write is
    /// conditional on. A <see cref="DbContext"/> satisfies this member implicitly.
    /// </summary>
    EntityEntry<TEntity> Entry<TEntity>(TEntity entity) where TEntity : class;
}

/// <summary>Entity mapping for <see cref="Setting"/>, tuned per engine.</summary>
/// <param name="provider">Selects the value column type and concurrency-token style.</param>
public sealed class SettingConfiguration(DatabaseProvider provider)
    : IEntityTypeConfiguration<Setting>
{
    /// <inheritdoc/>
    public void Configure(EntityTypeBuilder<Setting> builder)
    {
        builder.ToTable("Settings");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.Group).HasColumnName("SettingGroup").HasMaxLength(191).IsRequired();
        builder.Property(x => x.Key).HasColumnName("SettingKey").HasMaxLength(191).IsRequired();
        builder.Property(x => x.Type).HasMaxLength(500).IsRequired();
        builder.Property(x => x.UpdatedBy).HasMaxLength(256);
        builder.Property(x => x.IsEncrypted).IsRequired();
        builder.Property(x => x.UpdatedAt).IsRequired();

        builder.Property(x => x.Value).IsRequired().HasColumnType(provider switch
        {
            DatabaseProvider.SqlServer  => "nvarchar(max)",
            DatabaseProvider.PostgreSql => "text",
            _                           => "TEXT"
        });

        builder.HasIndex(x => new { x.Group, x.Key }).IsUnique().HasDatabaseName("UX_Settings_Group_Key");

        // A store-generated token rather than SQL Server's native rowversion, so that
        // optimistic concurrency behaves identically across every provider — including the
        // Dapper and MongoDB stores, which have no server-side equivalent.
        builder.Property(x => x.RowVersion).IsConcurrencyToken().HasMaxLength(16);
    }
}

/// <summary>Model-building helpers for wiring the settings entities into a DbContext.</summary>
public static class ModelBuilderExtensions
{
    /// <summary>
    /// Applies the settings entity configuration. Call from <c>OnModelCreating</c>.
    /// </summary>
    /// <param name="mb">The model builder.</param>
    /// <param name="provider">The engine being targeted.</param>
    public static ModelBuilder ApplySettings(this ModelBuilder mb, DatabaseProvider provider)
    {
        ArgumentNullException.ThrowIfNull(mb);
        mb.ApplyConfiguration(new SettingConfiguration(provider));
        return mb;
    }

    /// <summary>
    /// Applies the settings entity configuration, taking the engine from
    /// <paramref name="context"/> rather than being told it. Call from
    /// <c>OnModelCreating</c> as <c>modelBuilder.ApplySettings(this)</c>.
    /// <para>
    /// The explicit overload stays for anyone who needs to override the inference — a
    /// provider this package has no mapping for, or a model built against one engine and
    /// migrated onto another.
    /// </para>
    /// </summary>
    /// <param name="mb">The model builder.</param>
    /// <param name="context">The context being configured; usually <c>this</c>.</param>
    /// <exception cref="InvalidOperationException">
    /// The context's EF Core provider is not one this package ships column mappings for.
    /// Use the <see cref="ApplySettings(ModelBuilder, DatabaseProvider)"/> overload and name
    /// the closest engine.
    /// </exception>
    public static ModelBuilder ApplySettings(this ModelBuilder mb, DbContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return mb.ApplySettings(ResolveProvider(context.Database.ProviderName));
    }

    /// <summary>
    /// Renamed to <see cref="ApplySettings(ModelBuilder, DatabaseProvider)"/>.
    /// </summary>
    /// <remarks>
    /// Kept for one release so existing contexts keep compiling, the same way
    /// <see cref="ISettingDbContext"/> is. The old name described the mechanism rather than
    /// the library, which read as meaningful next to the <c>ApplyMedia()</c> and
    /// <c>ApplyMultiLingual()</c> calls it usually sits beside. It was not.
    /// </remarks>
    [Obsolete(RenamedMessage)]
    public static ModelBuilder ApplySettingsConfiguration(this ModelBuilder mb, DatabaseProvider provider)
        => mb.ApplySettings(provider);

    private const string RenamedMessage =
        "Renamed to ApplySettings, to match ApplyMedia and ApplyMultiLingual. " +
        "This alias will be removed in the next release.";

    /// <summary>
    /// Maps an EF Core provider assembly name onto the engine whose column types and
    /// concurrency-token style the settings tables need.
    /// </summary>
    private static DatabaseProvider ResolveProvider(string? providerName) => providerName switch
    {
        "Microsoft.EntityFrameworkCore.SqlServer"  => DatabaseProvider.SqlServer,
        "Npgsql.EntityFrameworkCore.PostgreSQL"    => DatabaseProvider.PostgreSql,
        "Microsoft.EntityFrameworkCore.Sqlite"     => DatabaseProvider.Sqlite,

        // Named deliberately rather than guessed at: picking the wrong one silently gives the
        // Value column a type the engine cannot hold, which shows up as truncated settings
        // long after the migration ran.
        _ => throw new InvalidOperationException(
            $"Cannot infer a settings DatabaseProvider from EF Core provider '{providerName ?? "(none)"}'. " +
            "Supported: SQL Server, PostgreSQL and SQLite. Call " +
            "ApplySettings(DatabaseProvider) with the engine to target instead.")
    };
}

/// <summary>EF Core-backed <see cref="ISettingStore"/>.</summary>
/// <remarks>
/// <para>
/// Takes a plain <see cref="DbContext"/> and reaches its entities through
/// <see cref="DbContext.Set{TEntity}()"/>, so a consuming context needs no interface and no
/// <c>DbSet</c> properties — only <c>ApplySettings</c> in <c>OnModelCreating</c>.
/// </para>
/// <para>
/// <b>The context is shared.</b> A write calls <c>SaveChangesAsync</c> on it, which saves
/// everything tracked on it, the application's own pending changes included. That is what lets
/// an <see cref="ISettingWriteParticipant"/> commit with the write: it enlists through
/// <see cref="EfCoreSettingWriteScope"/>, and the store's save carries it. A clear saves only
/// when something was enlisted, as part of the same transaction as the delete.
/// </para>
/// </remarks>
public sealed class EfCoreSettingStore : IEnlistingSettingStore
{
    private readonly DbContext                 db;
    private readonly EfCoreSettingWriteScope?  writeScope;

    /// <summary>A store with no write scope, so no participant can enlist in its writes.</summary>
    /// <param name="db">The context the settings entities are mapped on.</param>
    public EfCoreSettingStore(DbContext db) : this(db, null) { }

    /// <summary>A store whose writes commit what participants enlist in <paramref name="writeScope"/>.</summary>
    /// <param name="db">The context the settings entities are mapped on.</param>
    /// <param name="writeScope">Where participants enlist. Must wrap <paramref name="db"/>.</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="writeScope"/> wraps a different context, whose entities this store's save
    /// would never reach.
    /// </exception>
    public EfCoreSettingStore(DbContext db, EfCoreSettingWriteScope? writeScope)
    {
        this.db = db ?? throw new ArgumentNullException(nameof(db));

        if (writeScope is not null && !ReferenceEquals(writeScope.Context, db))
        {
            throw new ArgumentException(
                "The write scope wraps a different DbContext instance from the store's, so nothing " +
                "enlisted in it would be saved with the settings.", nameof(writeScope));
        }

        this.writeScope = writeScope;
    }

    private DbSet<Setting> Settings => db.Set<Setting>();

    /// <inheritdoc/>
    public async Task<IReadOnlyList<Setting>> GetGroupAsync(string group, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(group);
        return await Settings.AsNoTracking().Where(x => x.Group == group).ToListAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public Task<Setting?> GetAsync(string group, string key, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(group);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return Settings.AsNoTracking().FirstOrDefaultAsync(x => x.Group == group && x.Key == key, ct);
    }

    /// <inheritdoc/>
    public Task UpsertAsync(Setting setting, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(setting);
        return UpsertManyAsync([setting], ct);
    }

    /// <inheritdoc/>
    public async Task UpsertManyAsync(IEnumerable<Setting> settings, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var list = settings as IList<Setting> ?? settings.ToList();
        if (list.Count == 0) return;

        var group = list[0].Group;
        var keys  = list.Select(x => x.Key).ToHashSet(StringComparer.Ordinal);
        var existing = await Settings.Where(x => x.Group == group && keys.Contains(x.Key)).ToListAsync(ct).ConfigureAwait(false);
        var byKey = existing.ToDictionary(x => x.Key, StringComparer.Ordinal);

        // Every entity this write leaves tracked, so a failed write can take them all back.
        var touched = new List<Setting>(existing);

        try
        {
            foreach (var item in list)
            {
                if (byKey.TryGetValue(item.Key, out var e))
                {
                    // OriginalValue is what EF puts in the UPDATE's WHERE clause. Setting it to the
                    // token the caller read — rather than the one just loaded — is what makes the
                    // write conditional on nothing having changed in between.
                    if (item.RowVersion is not null)
                        db.Entry(e).Property(x => x.RowVersion).OriginalValue = item.RowVersion;

                    e.Value       = item.Value;
                    e.Type        = item.Type;
                    e.IsEncrypted = item.IsEncrypted;
                    e.UpdatedAt   = item.UpdatedAt;
                    e.UpdatedBy   = item.UpdatedBy;
                    e.RowVersion  = Setting.NewRowVersion();
                }
                else
                {
                    item.RowVersion = Setting.NewRowVersion();
                    await Settings.AddAsync(item, ct).ConfigureAwait(false);
                    touched.Add(item);
                }
            }

            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            Detach(touched);
            var clash = ex.Entries.Select(e => e.Entity).OfType<Setting>().FirstOrDefault();
            throw new SettingConcurrencyException(clash?.Group ?? group, clash?.Key ?? "?", ex);
        }
        catch
        {
            Detach(touched);
            throw;
        }

        writeScope?.Committed();
    }

    /// <summary>
    /// Takes back what a failed write left tracked. The context is scoped and usually the
    /// application's own, so a row left modified or added would otherwise be saved by its next
    /// unrelated <c>SaveChangesAsync</c> — writing the very value whose write just failed.
    /// </summary>
    private void Detach(List<Setting> touched)
    {
        foreach (var entity in touched)
            db.Entry(entity).State = EntityState.Detached;
    }

    /// <inheritdoc/>
    public void DiscardEnlisted() => writeScope?.Discard();

    /// <inheritdoc/>
    public async Task DeleteAsync(string group, string key, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(group);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var row = await Settings.FirstOrDefaultAsync(x => x.Group == group && x.Key == key, ct).ConfigureAwait(false);
        if (row is null) return;
        Settings.Remove(row);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// <para>
    /// With nothing enlisted this is one <c>ExecuteDeleteAsync</c>, which bypasses the change
    /// tracker and saves nothing else.
    /// </para>
    /// <para>
    /// With something enlisted, the delete and a <c>SaveChangesAsync</c> for it run in one
    /// transaction. The caller's transaction when there is one, which the caller then commits
    /// or rolls back. Otherwise one of its own, inside the context's execution strategy: under
    /// a retrying strategy (<c>EnableRetryOnFailure</c>) a transaction opened outside it throws.
    /// The save does not accept its changes until the transaction commits, so a retry saves
    /// them again rather than skipping them.
    /// </para>
    /// </remarks>
    public async Task DeleteGroupAsync(string group, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(group);

        if (writeScope is not { HasEnlisted: true })
        {
            await Settings.Where(x => x.Group == group).ExecuteDeleteAsync(ct).ConfigureAwait(false);
            return;
        }

        if (db.Database.CurrentTransaction is not null)
        {
            await Settings.Where(x => x.Group == group).ExecuteDeleteAsync(ct).ConfigureAwait(false);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        else
        {
            var strategy = db.Database.CreateExecutionStrategy();
            await strategy.ExecuteAsync(async () =>
            {
                await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
                await Settings.Where(x => x.Group == group).ExecuteDeleteAsync(ct).ConfigureAwait(false);
                await db.SaveChangesAsync(acceptAllChangesOnSuccess: false, ct).ConfigureAwait(false);
                await tx.CommitAsync(ct).ConfigureAwait(false);
            }).ConfigureAwait(false);

            db.ChangeTracker.AcceptAllChanges();
        }

        writeScope.Committed();
    }


    /// <inheritdoc/>
    public Task<int> CountAsync(string group, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(group);
        return Settings.CountAsync(x => x.Group == group, ct);
    }
}
