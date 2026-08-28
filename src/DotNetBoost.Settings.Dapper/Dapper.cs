using Dapper;
using DotNetBoost.Settings.Core;
using DotNetBoost.Settings.Core.Interfaces;
using DotNetBoost.Settings.Core.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.Collections.Concurrent;
using System.Data;

namespace DotNetBoost.Settings.Dapper;

/// <summary>
/// Maps <see cref="Guid"/> to and from providers that store it as text, such as SQLite.
/// </summary>
public sealed class GuidTypeHandler : SqlMapper.TypeHandler<Guid>
{
    /// <inheritdoc/>
    public override void SetValue(IDbDataParameter parameter, Guid value) => parameter.Value = value.ToString();
    /// <inheritdoc/>
    public override Guid Parse(object value) => value switch { Guid g => g, string s => Guid.Parse(s), _ => Guid.Parse(value.ToString()!) };
}

/// <summary>Nullable counterpart to <see cref="GuidTypeHandler"/>.</summary>
public sealed class NullableGuidTypeHandler : SqlMapper.TypeHandler<Guid?>
{
    /// <inheritdoc/>
    public override void SetValue(IDbDataParameter parameter, Guid? value) => parameter.Value = value?.ToString();
    /// <inheritdoc/>
    public override Guid? Parse(object value)
    {
        if (value is null || value is DBNull) return null;
        return value switch { Guid g => g, string s => Guid.Parse(s), _ => Guid.Parse(value.ToString()!) };
    }
}

internal static class DapperSchemaInitializer
{
    public static Task InitializeAsync(
        IDbConnection conn, SettingTableOptions? tables = null, CancellationToken ct = default)
    {
        var t = tables ?? new SettingTableOptions();
        t.Validate();

        var name = conn.GetType().Name.ToLowerInvariant();

        // Order matters: "npgsqlconnection" *contains* "sqlconnection", so Npgsql has to be
        // matched before SQL Server.
        var sql = name switch
        {
            var x when x.Contains("npgsql")        => PostgreSql(t),
            var x when x.Contains("sqlite")        => Sqlite(t),
            var x when x.Contains("sqlconnection") => SqlServer(t),
            _ => throw new NotSupportedException($"Unsupported connection type: {conn.GetType().Name}")
        };
        return conn.ExecuteAsync(new CommandDefinition(sql, cancellationToken: ct));
    }

    private static string SqlServer(SettingTableOptions t) => $"""
        {(t.Schema is null ? "" : $"IF SCHEMA_ID('{t.Schema}') IS NULL EXEC('CREATE SCHEMA {t.Schema}');")}
        IF OBJECT_ID('{t.QualifiedSettingsTable}','U') IS NULL
        BEGIN
            CREATE TABLE {t.QualifiedSettingsTable} (
                Id           UNIQUEIDENTIFIER PRIMARY KEY,
                SettingGroup NVARCHAR(191)    NOT NULL,
                SettingKey   NVARCHAR(191)    NOT NULL,
                Value        NVARCHAR(MAX)    NOT NULL,
                Type         NVARCHAR(500)    NOT NULL,
                IsEncrypted  BIT              NOT NULL DEFAULT 0,
                UpdatedAt    DATETIME2        NOT NULL,
                UpdatedBy    NVARCHAR(256)    NULL,
                RowVersion   VARBINARY(16)    NULL
            );
            CREATE UNIQUE INDEX {t.SettingsIndexName} ON {t.QualifiedSettingsTable}(SettingGroup, SettingKey);
        END;
        IF OBJECT_ID('{t.QualifiedAuditTable}','U') IS NULL
        BEGIN
            CREATE TABLE {t.QualifiedAuditTable} (
                Id           UNIQUEIDENTIFIER PRIMARY KEY,
                SettingGroup NVARCHAR(191)    NOT NULL,
                SettingKey   NVARCHAR(191)    NOT NULL,
                OldValue     NVARCHAR(MAX)    NOT NULL,
                NewValue     NVARCHAR(MAX)    NOT NULL,
                ChangedBy    NVARCHAR(256)    NOT NULL,
                ChangedAt    DATETIME2        NOT NULL
            );
            CREATE INDEX {t.AuditIndexName} ON {t.QualifiedAuditTable}(SettingGroup, SettingKey);
        END;
        """;

    private static string PostgreSql(SettingTableOptions t) => $"""
        {(t.Schema is null ? "" : $"CREATE SCHEMA IF NOT EXISTS {t.Schema};")}
        CREATE TABLE IF NOT EXISTS {t.QualifiedSettingsTable} (
            Id           uuid          PRIMARY KEY,
            SettingGroup varchar(191)  NOT NULL,
            SettingKey   varchar(191)  NOT NULL,
            Value        text          NOT NULL,
            Type         varchar(500)  NOT NULL,
            IsEncrypted  boolean       NOT NULL DEFAULT false,
            UpdatedAt    timestamp     NOT NULL,
            UpdatedBy    varchar(256),
            RowVersion   bytea
        );
        CREATE UNIQUE INDEX IF NOT EXISTS {t.SettingsIndexName} ON {t.QualifiedSettingsTable}(SettingGroup,SettingKey);
        CREATE TABLE IF NOT EXISTS {t.QualifiedAuditTable} (
            Id           uuid          PRIMARY KEY,
            SettingGroup varchar(191)  NOT NULL,
            SettingKey   varchar(191)  NOT NULL,
            OldValue     text          NOT NULL,
            NewValue     text          NOT NULL,
            ChangedBy    varchar(256)  NOT NULL,
            ChangedAt    timestamp     NOT NULL
        );
        """;

    private static string Sqlite(SettingTableOptions t)
    {
        if (t.Schema is not null)
        {
            throw new NotSupportedException(
                "SQLite has no schemas; leave SettingTableOptions.Schema null for SQLite.");
        }

        return $"""
        CREATE TABLE IF NOT EXISTS {t.SettingsTable} (
            Id           TEXT    PRIMARY KEY,
            SettingGroup TEXT    NOT NULL,
            SettingKey   TEXT    NOT NULL,
            Value        TEXT    NOT NULL,
            Type         TEXT    NOT NULL,
            IsEncrypted  INTEGER NOT NULL DEFAULT 0,
            UpdatedAt    TEXT    NOT NULL,
            UpdatedBy    TEXT,
            RowVersion   BLOB
        );
        CREATE UNIQUE INDEX IF NOT EXISTS {t.SettingsIndexName} ON {t.SettingsTable}(SettingGroup, SettingKey);
        CREATE TABLE IF NOT EXISTS {t.AuditTable} (
            Id           TEXT NOT NULL PRIMARY KEY,
            SettingGroup TEXT NOT NULL,
            SettingKey   TEXT NOT NULL,
            OldValue     TEXT NOT NULL,
            NewValue     TEXT NOT NULL,
            ChangedBy    TEXT NOT NULL,
            ChangedAt    TEXT NOT NULL
        );
        """;
    }
}

/// <summary>
/// Row shape for the Settings table. Its property names mirror the column names, which keeps
/// the SELECT free of quoted aliases — <c>Group</c> and <c>Key</c> are reserved words, and the
/// quoting style for them differs per engine ([] on SQL Server, "" on PostgreSQL).
/// </summary>
internal sealed class SettingRow
{
    public Guid     Id           { get; set; }
    public string   SettingGroup { get; set; } = default!;
    public string   SettingKey   { get; set; } = default!;
    public string   Value        { get; set; } = default!;
    public string   Type         { get; set; } = default!;
    public bool     IsEncrypted  { get; set; }
    public DateTime UpdatedAt    { get; set; }
    public string?  UpdatedBy    { get; set; }
    public byte[]?  RowVersion   { get; set; }

    public Setting ToSetting() => new()
    {
        Id          = Id,
        Group       = SettingGroup,
        Key         = SettingKey,
        Value       = Value,
        Type        = Type,
        IsEncrypted = IsEncrypted,
        UpdatedAt   = UpdatedAt,
        UpdatedBy   = UpdatedBy,
        RowVersion  = RowVersion
    };
}

/// <summary>
/// Dapper-backed <see cref="ISettingStore"/> for SQL Server, PostgreSQL and SQLite.
/// </summary>
public sealed class DapperSettingStore : ISettingStore
{
    private static int _typeHandlersRegistered;

    // Table names are composed into the statement text, so the finished SQL is cached per
    // table rather than rebuilt for every store instance — the store is resolved per request.
    private static readonly ConcurrentDictionary<string, StoreSql> SqlCache = new(StringComparer.Ordinal);

    private readonly IDbConnection _connection;
    private readonly StoreSql      _sql;

    static DapperSettingStore() => RegisterTypeHandlers();

    /// <param name="connection">
    /// The connection to issue commands on. Its runtime type selects the SQL dialect used by
    /// <c>migrateSchema</c>.
    /// </param>
    /// <param name="tables">Object names to read and write. Defaults to <c>Settings</c>/<c>SettingAudits</c>.</param>
    public DapperSettingStore(IDbConnection connection, SettingTableOptions? tables = null)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var options = tables ?? new SettingTableOptions();
        options.Validate();

        _connection = connection;
        _sql = SqlCache.GetOrAdd(options.QualifiedSettingsTable, StoreSql.For);
    }

    /// <summary>
    /// Registers the Guid type handlers this store depends on. Providers such as SQLite
    /// return <c>Id</c> as TEXT, which Dapper cannot map to <see cref="Guid"/> unaided.
    /// Runs automatically before the first store instance is created; idempotent.
    /// </summary>
    public static void RegisterTypeHandlers()
    {
        if (Interlocked.Exchange(ref _typeHandlersRegistered, 1) != 0) return;

        SqlMapper.AddTypeHandler(new NullableGuidTypeHandler());
        SqlMapper.AddTypeHandler(new GuidTypeHandler());
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<Setting>> GetGroupAsync(string group, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(group);
        var rows = await _connection.QueryAsync<SettingRow>(new CommandDefinition(_sql.GetGroup, new { Group = group }, cancellationToken: ct)).ConfigureAwait(false);
        return rows.Select(static r => r.ToSetting()).ToList();
    }

    /// <inheritdoc/>
    public async Task<Setting?> GetAsync(string group, string key, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(group);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var row = await _connection.QueryFirstOrDefaultAsync<SettingRow>(new CommandDefinition(_sql.Get, new { Group = group, Key = key }, cancellationToken: ct)).ConfigureAwait(false);
        return row?.ToSetting();
    }

    /// <inheritdoc/>
    public Task UpsertAsync(Setting setting, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(setting);
        return UpsertInternalAsync(setting, null, ct);
    }

    /// <inheritdoc/>
    public async Task UpsertManyAsync(IEnumerable<Setting> settings, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var list = settings as IList<Setting> ?? settings.ToList();
        if (list.Count == 0) return;

        var shouldClose = _connection.State != ConnectionState.Open;
        if (shouldClose) _connection.Open();
        using var tx = _connection.BeginTransaction();
        try
        {
            foreach (var item in list)
                await UpsertInternalAsync(item, tx, ct).ConfigureAwait(false);
            tx.Commit();
        }
        catch { tx.Rollback(); throw; }
        finally { if (shouldClose) _connection.Close(); }
    }

    /// <inheritdoc/>
    public async Task DeleteAsync(string group, string key, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(group);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        await _connection.ExecuteAsync(new CommandDefinition(_sql.Delete, new { Group = group, Key = key }, cancellationToken: ct)).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task DeleteGroupAsync(string group, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(group);
        await _connection.ExecuteAsync(new CommandDefinition(_sql.DeleteGroup, new { Group = group }, cancellationToken: ct)).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public Task<bool> GroupExistsAsync(string group, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(group);
        return _connection.ExecuteScalarAsync<bool>(new CommandDefinition(_sql.Exists, new { Group = group }, cancellationToken: ct));
    }

    /// <inheritdoc/>
    public Task<int> CountAsync(string group, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(group);
        return _connection.ExecuteScalarAsync<int>(new CommandDefinition(_sql.Count, new { Group = group }, cancellationToken: ct));
    }

    private async Task UpsertInternalAsync(Setting s, IDbTransaction? tx, CancellationToken ct)
    {
        var parameters = new
        {
            s.Id,
            SettingGroup = s.Group,
            SettingKey   = s.Key,
            s.Value,
            s.Type,
            s.IsEncrypted,
            s.UpdatedAt,
            s.UpdatedBy,
            ExpectedRowVersion = s.RowVersion,
            NewRowVersion      = Setting.NewRowVersion()
        };

        var updated = await _connection.ExecuteAsync(
            new CommandDefinition(_sql.Update, parameters, transaction: tx, cancellationToken: ct)).ConfigureAwait(false);
        if (updated > 0) return;

        var inserted = await _connection.ExecuteAsync(
            new CommandDefinition(_sql.InsertIfAbsent, parameters, transaction: tx, cancellationToken: ct)).ConfigureAwait(false);
        if (inserted > 0) return;

        // Nothing updated and nothing inserted: the row is there, but not with the token the
        // caller expected.
        throw new SettingConcurrencyException(s.Group, s.Key);
    }

    /// <summary>The statements for one settings table, composed once.</summary>
    private sealed record StoreSql(
        string GetGroup, string Get, string Delete, string DeleteGroup,
        string Exists, string Count, string Update, string InsertIfAbsent)
    {
        private const string Columns =
            "Id, SettingGroup, SettingKey, Value, Type, IsEncrypted, UpdatedAt, UpdatedBy, RowVersion";

        public static StoreSql For(string table) => new(
            GetGroup:    $"SELECT {Columns} FROM {table} WHERE SettingGroup = @Group",
            Get:         $"SELECT {Columns} FROM {table} WHERE SettingGroup = @Group AND SettingKey = @Key",
            Delete:      $"DELETE FROM {table} WHERE SettingGroup = @Group AND SettingKey = @Key",
            DeleteGroup: $"DELETE FROM {table} WHERE SettingGroup = @Group",
            Exists:      $"SELECT COUNT(1) FROM {table} WHERE SettingGroup = @Group",
            Count:       $"SELECT COUNT(*) FROM {table} WHERE SettingGroup = @Group",

            // Deliberately two statements rather than one batch. The affected-row counts are
            // what separate the three outcomes — updated, inserted, or beaten by a concurrent
            // writer — and a multi-statement batch reports those inconsistently across providers.
            Update: $"""
                UPDATE {table} SET Value=@Value, Type=@Type, IsEncrypted=@IsEncrypted,
                    UpdatedAt=@UpdatedAt, UpdatedBy=@UpdatedBy, RowVersion=@NewRowVersion
                WHERE SettingGroup=@SettingGroup AND SettingKey=@SettingKey
                  AND (@ExpectedRowVersion IS NULL OR RowVersion = @ExpectedRowVersion)
                """,
            InsertIfAbsent: $"""
                INSERT INTO {table} (Id,SettingGroup,SettingKey,Value,Type,IsEncrypted,UpdatedAt,UpdatedBy,RowVersion)
                SELECT @Id,@SettingGroup,@SettingKey,@Value,@Type,@IsEncrypted,@UpdatedAt,@UpdatedBy,@NewRowVersion
                WHERE NOT EXISTS (SELECT 1 FROM {table} WHERE SettingGroup=@SettingGroup AND SettingKey=@SettingKey)
                """);
    }
}

internal sealed class DapperMigrationHostedService(
    IServiceProvider services, SettingTableOptions tables) : IHostedService
{
    public async Task StartAsync(CancellationToken ct)
    {
        using var scope = services.CreateScope();
        var conn = scope.ServiceProvider.GetRequiredService<IDbConnection>();
        await DapperSchemaInitializer.InitializeAsync(conn, tables, ct).ConfigureAwait(false);
    }
    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
