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
/// Implement this on your <see cref="DbContext"/> to let the EF Core store use it.
/// </summary>
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
/// <param name="tables">Schema and table names. Defaults to <c>Settings</c> with no schema.</param>
public sealed class SettingConfiguration(DatabaseProvider provider, SettingTableOptions? tables = null)
    : IEntityTypeConfiguration<Setting>
{
    private readonly SettingTableOptions _tables = Validated(tables);

    /// <inheritdoc/>
    public void Configure(EntityTypeBuilder<Setting> builder)
    {
        builder.ToTable(_tables.SettingsTable, _tables.Schema);
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

        builder.HasIndex(x => new { x.Group, x.Key }).IsUnique().HasDatabaseName(_tables.SettingsIndexName);

        // A store-generated token rather than SQL Server's native rowversion, so that
        // optimistic concurrency behaves identically across every provider — including the
        // Dapper and MongoDB stores, which have no server-side equivalent.
        builder.Property(x => x.RowVersion).IsConcurrencyToken().HasMaxLength(16);
    }

    private static SettingTableOptions Validated(SettingTableOptions? tables)
    {
        var t = tables ?? new SettingTableOptions();
        t.Validate();
        return t;
    }
}

/// <summary>Entity mapping for <see cref="SettingAuditEntry"/>.</summary>
/// <param name="tables">Schema and table names. Defaults to <c>SettingAudits</c> with no schema.</param>
public sealed class SettingAuditConfiguration(SettingTableOptions? tables = null)
    : IEntityTypeConfiguration<SettingAuditEntry>
{
    private readonly SettingTableOptions _tables = Validated(tables);

    /// <inheritdoc/>
    public void Configure(EntityTypeBuilder<SettingAuditEntry> builder)
    {
        builder.ToTable(_tables.AuditTable, _tables.Schema);
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.Property(x => x.Group).HasColumnName("SettingGroup").HasMaxLength(191).IsRequired();
        builder.Property(x => x.Key).HasColumnName("SettingKey").HasMaxLength(191).IsRequired();
        builder.Property(x => x.ChangedBy).HasMaxLength(256).IsRequired();
        builder.Property(x => x.ChangedAt).IsRequired();
        builder.HasIndex(x => new { x.Group, x.Key }).HasDatabaseName(_tables.AuditIndexName);
    }

    private static SettingTableOptions Validated(SettingTableOptions? tables)
    {
        var t = tables ?? new SettingTableOptions();
        t.Validate();
        return t;
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
    /// <param name="configureTables">
    /// Overrides the schema and table names. Must match whatever is passed to
    /// <c>UseEntityFrameworkCore</c>-adjacent tooling such as migrations.
    /// </param>
    public static ModelBuilder ApplySettingsConfiguration(
        this ModelBuilder mb, DatabaseProvider provider, Action<SettingTableOptions>? configureTables = null)
    {
        ArgumentNullException.ThrowIfNull(mb);

        var tables = new SettingTableOptions();
        configureTables?.Invoke(tables);

        mb.ApplyConfiguration(new SettingConfiguration(provider, tables));
        mb.ApplyConfiguration(new SettingAuditConfiguration(tables));
        return mb;
    }
}

/// <summary>EF Core-backed <see cref="ISettingStore"/>.</summary>
/// <param name="db">The context holding the settings entities.</param>
public sealed class EfCoreSettingStore(ISettingDbContext db) : ISettingStore
{
    /// <inheritdoc/>
    public async Task<IReadOnlyList<Setting>> GetGroupAsync(string group, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(group);
        return await db.Settings.AsNoTracking().Where(x => x.Group == group).ToListAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public Task<Setting?> GetAsync(string group, string key, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(group);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return db.Settings.AsNoTracking().FirstOrDefaultAsync(x => x.Group == group && x.Key == key, ct);
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
        var existing = await db.Settings.Where(x => x.Group == group && keys.Contains(x.Key)).ToListAsync(ct).ConfigureAwait(false);
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
                await db.Settings.AddAsync(item, ct).ConfigureAwait(false);
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
        var row = await db.Settings.FirstOrDefaultAsync(x => x.Group == group && x.Key == key, ct).ConfigureAwait(false);
        if (row is null) return;
        db.Settings.Remove(row);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public Task DeleteGroupAsync(string group, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(group);
        return db.Settings.Where(x => x.Group == group).ExecuteDeleteAsync(ct);
    }

    /// <inheritdoc/>
    public Task<bool> GroupExistsAsync(string group, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(group);
        return db.Settings.AnyAsync(x => x.Group == group, ct);
    }

    /// <inheritdoc/>
    public Task<int> CountAsync(string group, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(group);
        return db.Settings.CountAsync(x => x.Group == group, ct);
    }
}

/// <summary>EF Core-backed audit store. Registered via <c>.UseAuditStore&lt;EfCoreAuditStore&gt;()</c>.</summary>
public sealed class EfCoreAuditStore(ISettingDbContext db) : ISettingAuditStore
{
    /// <inheritdoc/>
    public async Task RecordAsync(SettingAuditEntry entry, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        await db.SettingAudits.AddAsync(entry, ct).ConfigureAwait(false);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<SettingAuditEntry>> GetHistoryAsync(string group, string? key = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(group);
        var q = db.SettingAudits.AsNoTracking().Where(x => x.Group == group);
        if (key is not null) q = q.Where(x => x.Key == key);
        return await q.OrderByDescending(x => x.ChangedAt).ToListAsync(ct).ConfigureAwait(false);
    }
}
