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
/// <c>DbSet</c> properties it declares — <c>ApplySettingsConfiguration</c> in
/// <c>OnModelCreating</c> is the whole registration.
/// </summary>
/// <remarks>
/// Kept for one release so existing contexts keep compiling. Delete the
/// <c>: ISettingDbContext</c> and the two <c>DbSet</c> properties; nothing else changes.
/// </remarks>
[Obsolete("ISettingDbContext is no longer used. Remove it and the DbSet properties from your " +
          "DbContext; UseEntityFrameworkCore<TContext>() only needs a DbContext. " +
          "This interface will be removed in the next release.")]
public interface ISettingDbContext
{
    /// <summary>The persisted settings rows.</summary>
    DbSet<Setting>           Settings      { get; }

    /// <summary>The change-history rows.</summary>
    DbSet<SettingAuditEntry> SettingAudits { get; }

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

/// <summary>Entity mapping for <see cref="SettingAuditEntry"/>.</summary>
public sealed class SettingAuditConfiguration : IEntityTypeConfiguration<SettingAuditEntry>
{
    /// <inheritdoc/>
    public void Configure(EntityTypeBuilder<SettingAuditEntry> builder)
    {
        builder.ToTable("SettingAudits");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.Group).HasColumnName("SettingGroup").HasMaxLength(191).IsRequired();
        builder.Property(x => x.Key).HasColumnName("SettingKey").HasMaxLength(191).IsRequired();
        builder.Property(x => x.ChangedBy).HasMaxLength(256).IsRequired();
        builder.Property(x => x.ChangedAt).IsRequired();
        builder.HasIndex(x => new { x.Group, x.Key });
    }
}

/// <summary>Model-building helpers for wiring the settings entities into a DbContext.</summary>
public static class ModelBuilderExtensions
{
    /// <summary>
    /// Applies both settings entity configurations. Call from <c>OnModelCreating</c>.
    /// </summary>
    /// <param name="mb">The model builder.</param>
    /// <param name="provider">The engine being targeted.</param>
    public static ModelBuilder ApplySettingsConfiguration(this ModelBuilder mb, DatabaseProvider provider)
    {
        ArgumentNullException.ThrowIfNull(mb);
        mb.ApplyConfiguration(new SettingConfiguration(provider));
        mb.ApplyConfiguration(new SettingAuditConfiguration());
        return mb;
    }

    /// <summary>
    /// Applies both settings entity configurations, taking the engine from
    /// <paramref name="context"/> rather than being told it. Call from
    /// <c>OnModelCreating</c> as <c>modelBuilder.ApplySettingsConfiguration(this)</c>.
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
    /// Use the <see cref="ApplySettingsConfiguration(ModelBuilder, DatabaseProvider)"/>
    /// overload and name the closest engine.
    /// </exception>
    public static ModelBuilder ApplySettingsConfiguration(this ModelBuilder mb, DbContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return mb.ApplySettingsConfiguration(ResolveProvider(context.Database.ProviderName));
    }

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
            "ApplySettingsConfiguration(DatabaseProvider) with the engine to target instead.")
    };
}

/// <summary>EF Core-backed <see cref="ISettingStore"/>.</summary>
/// <remarks>
/// Takes a plain <see cref="DbContext"/> and reaches its entities through
/// <see cref="DbContext.Set{TEntity}()"/>, so a consuming context needs no interface and no
/// <c>DbSet</c> properties — only <c>ApplySettingsConfiguration</c> in <c>OnModelCreating</c>.
/// </remarks>
/// <param name="db">The context the settings entities are mapped on.</param>
public sealed class EfCoreSettingStore(DbContext db) : ISettingStore
{
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
            }
        }

        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            var clash = ex.Entries.Select(e => e.Entity).OfType<Setting>().FirstOrDefault();
            throw new SettingConcurrencyException(clash?.Group ?? group, clash?.Key ?? "?", ex);
        }
    }

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
    public Task DeleteGroupAsync(string group, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(group);
        return Settings.Where(x => x.Group == group).ExecuteDeleteAsync(ct);
    }


    /// <inheritdoc/>
    public Task<int> CountAsync(string group, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(group);
        return Settings.CountAsync(x => x.Group == group, ct);
    }
}

/// <summary>EF Core-backed audit store. Registered via <c>.UseAuditStore&lt;EfCoreAuditStore&gt;()</c>.</summary>
/// <param name="db">The context the audit entity is mapped on.</param>
public sealed class EfCoreAuditStore(DbContext db) : ISettingAuditStore
{
    private DbSet<SettingAuditEntry> SettingAudits => db.Set<SettingAuditEntry>();

    /// <inheritdoc/>
    public async Task RecordAsync(SettingAuditEntry entry, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        await SettingAudits.AddAsync(entry, ct).ConfigureAwait(false);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<SettingAuditEntry>> GetHistoryAsync(string group, string? key = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(group);
        var q = SettingAudits.AsNoTracking().Where(x => x.Group == group);
        if (key is not null) q = q.Where(x => x.Key == key);
        return await q.OrderByDescending(x => x.ChangedAt).ToListAsync(ct).ConfigureAwait(false);
    }
}
