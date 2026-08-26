using DotNetBoost.Settings.Core.Attributes;
using DotNetBoost.Settings.Core.Interfaces;
using DotNetBoost.Settings.Core.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Reflection;

namespace DotNetBoost.Settings.Core.Services;

/// <summary>
/// Core implementation of <see cref="ISettingManager"/>.
/// </summary>
public sealed partial class SettingManager : ISettingManager
{
    private readonly ISettingStore              _store;
    private readonly ISettingCache              _cache;
    private readonly ISettingEncryptor?         _encryptor;
    private readonly ISettingAuditStore?        _auditStore;
    private readonly IServiceProvider           _sp;
    private readonly ILogger<SettingManager>    _logger;
    private readonly SettingOptions             _options;

    /// <summary>Placeholder written to the audit trail in place of an encrypted value.</summary>
    private const string EncryptedPlaceholder = "[encrypted]";

    internal TimeSpan CacheDuration => _options.CacheDuration;

    private static readonly ConcurrentDictionary<Type, TypeMap>       TypeMaps          = new();
    private static readonly ConcurrentDictionary<MemberInfo, Delegate> CompiledSelectors = new();

    /// <summary>
    /// Per-group load locks. Static because <c>ISettingManager</c> is registered scoped: an
    /// instance field would give every request its own semaphore, so concurrent requests for
    /// the same group would each miss the (singleton) cache and hit the store. Keyed by cache
    /// key, so the set is bounded by the number of settings groups in the application.
    /// </summary>
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Locks = new();

    /// <summary>
    /// Creates a manager. <paramref name="encryptor"/>, <paramref name="auditStore"/> and
    /// <paramref name="options"/> are optional: omitting them disables encryption and
    /// auditing, and applies the default options.
    /// </summary>
    public SettingManager(
        ISettingStore            store,
        ISettingCache            cache,
        IServiceProvider         sp,
        ILogger<SettingManager>  logger,
        ISettingEncryptor?       encryptor  = null,
        ISettingAuditStore?      auditStore = null,
        SettingOptions?          options    = null)
    {
        _store      = store      ?? throw new ArgumentNullException(nameof(store));
        _cache      = cache      ?? throw new ArgumentNullException(nameof(cache));
        _sp         = sp         ?? throw new ArgumentNullException(nameof(sp));
        _logger     = logger     ?? throw new ArgumentNullException(nameof(logger));
        _encryptor  = encryptor;
        _auditStore = auditStore;
        _options    = options ?? new SettingOptions();
    }

    /// <inheritdoc/>
    public ISettingAccessor<T> For<T>() where T : new()
        => new SettingAccessor<T>(this);

    internal T Get<T>(bool refreshCache, CancellationToken ct) where T : new()
        => GetAsync<T>(refreshCache, ct).ConfigureAwait(false).GetAwaiter().GetResult();

    internal TProp Get<T, TProp>(Expression<Func<T, TProp>> selector, bool refreshCache, CancellationToken ct)
        where T : new()
        => CompileSelector(selector)(Get<T>(refreshCache, ct));

    internal async Task<T> GetAsync<T>(bool refreshCache, CancellationToken ct) where T : new()
    {
        var map = GetTypeMap(typeof(T));
        var key = CacheKey(map);

        if (!refreshCache && TryFromCache<T>(key, out var fast))
            return fast!;

        var locker = Locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await locker.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!refreshCache && TryFromCache<T>(key, out var hit))
                return hit!;

            var rows  = await _store.GetGroupAsync(map.GroupName, ct).ConfigureAwait(false);
            var model = MapToModel<T>(rows);

            _cache.Set(key, model, CacheDuration);
            return model;
        }
        finally
        {
            locker.Release();
        }
    }

    internal async Task<TProp> GetAsync<T, TProp>(
        Expression<Func<T, TProp>> selector, bool refreshCache, CancellationToken ct) where T : new()
    {
        var model = await GetAsync<T>(refreshCache, ct).ConfigureAwait(false);
        return CompileSelector(selector)(model);
    }

    internal async Task<string> GetVersionAsync<T>(CancellationToken ct) where T : new()
    {
        var map  = GetTypeMap(typeof(T));
        var rows = await _store.GetGroupAsync(map.GroupName, ct).ConfigureAwait(false);
        return SettingVersion.Compute(rows);
    }

    internal async Task SetAsync<T>(T model, string? expectedVersion, CancellationToken ct) where T : new()
    {
        ArgumentNullException.ThrowIfNull(model);

        await ValidateOrThrowAsync(model!, ct).ConfigureAwait(false);

        var map = GetTypeMap(typeof(T));

        // One authoritative read of the stored state, serving four purposes: deciding what
        // actually changed, supplying the concurrency token each write is conditional on,
        // giving the audit trail its before-values, and the model handed to change handlers.
        // It deliberately bypasses the cache — a stale token would fail every write.
        var prevRows = await _store.GetGroupAsync(map.GroupName, ct).ConfigureAwait(false);
        var prevMap  = prevRows.ToDictionary(r => r.Key, StringComparer.OrdinalIgnoreCase);

        // Checked against the same snapshot the writes are built from. Anything that slips in
        // between is still caught by the per-row tokens below, so there is no window here.
        if (expectedVersion is not null)
        {
            var actual = SettingVersion.Compute(prevRows);
            if (!string.Equals(actual, expectedVersion, StringComparison.Ordinal))
                throw new SettingConcurrencyException(map.GroupName, "*");
        }

        T previous;
        try   { previous = MapToModel<T>(prevRows); }
        catch { previous = new T(); }

        var rows = new List<Setting>(map.Properties.Count);

        foreach (var prop in map.Properties)
        {
            var raw = prop.Getter(model!);
            if (raw is null) continue;

            var plaintext = Extensions.ConvertFrom(prop.PropertyType, raw);
            var strValue  = plaintext;

            var isEncrypted = false;
            if (prop.IsSensitive && _encryptor is not null)
            {
                strValue    = _encryptor.Encrypt(strValue);
                isEncrypted = true;
            }

            prevMap.TryGetValue(prop.Name, out var prev);

            // Writing only what changed is what keeps concurrent edits to *different*
            // properties of the same group from overwriting one another.
            if (IsUnchanged(prev, isEncrypted, plaintext, map.GroupName, prop.Name)) continue;

            rows.Add(new Setting
            {
                Id          = prev?.Id ?? Guid.NewGuid(),
                Group       = map.GroupName,
                Key         = prop.Name,
                Type        = prop.TypeName,
                Value       = strValue,
                IsEncrypted = isEncrypted,
                UpdatedAt   = DateTime.UtcNow,

                // The token this write is conditional on. Null for a property that has never
                // been stored, which is a plain insert.
                RowVersion  = prev?.RowVersion
            });
        }

        if (rows.Count == 0)
        {
            LogGroupUnchanged(_logger, map.GroupName);
            return;
        }

        await _store.UpsertManyAsync(rows, ct).ConfigureAwait(false);

        _cache.Remove(CacheKey<T>());

        if (_auditStore is not null)
        {
            // rows already holds only what changed, so every one of them earns an entry.
            foreach (var row in rows)
            {
                prevMap.TryGetValue(row.Key, out var prev);
                await _auditStore.RecordAsync(new SettingAuditEntry
                {
                    Group     = row.Group,
                    Key       = row.Key,
                    OldValue  = prev is null ? string.Empty
                              : prev.IsEncrypted ? EncryptedPlaceholder : prev.Value,
                    NewValue  = row.IsEncrypted ? EncryptedPlaceholder : row.Value,
                    ChangedBy = "system"
                }, ct).ConfigureAwait(false);
            }
        }

        await FireChangedHandlersAsync(previous, model, map.GroupName, ct).ConfigureAwait(false);

        LogGroupUpdated(_logger, map.GroupName, rows.Count);
    }

    internal async Task SetAsync<T, TProp>(
        Expression<Func<T, TProp>> selector, TProp value, CancellationToken ct) where T : new()
    {
        var model = await GetAsync<T>(false, ct).ConfigureAwait(false);
        GetPropertyInfo(selector).SetValue(model, value);
        await SetAsync(model, null, ct).ConfigureAwait(false);
    }

    internal async Task<bool> ExistsAsync<T>(bool allProperties, CancellationToken ct) where T : new()
    {
        var map   = GetTypeMap(typeof(T));
        var count = await _store.CountAsync(map.GroupName, ct).ConfigureAwait(false);

        if (count == 0)         return false;
        if (!allProperties)     return true;
        return count == map.Properties.Count;
    }

    internal async Task ClearAsync<T>(CancellationToken ct) where T : new()
    {
        var map = GetTypeMap(typeof(T));
        await _store.DeleteGroupAsync(map.GroupName, ct).ConfigureAwait(false);
        _cache.Remove(CacheKey<T>());
        LogGroupCleared(_logger, map.GroupName);
    }

    /// <summary>
    /// Decides whether a written row is identical to what was already stored, so the audit
    /// trail can skip it. Encrypted values are compared as plaintext: AES-GCM draws a fresh
    /// nonce per call, so re-encrypting an unchanged secret always produces different
    /// ciphertext and would otherwise look like a change on every single save.
    /// Anything that cannot be compared with confidence is reported as changed.
    /// </summary>
    private bool IsUnchanged(Setting? previous, bool willEncrypt, string plaintext, string group, string key)
    {
        if (previous is null) return false;

        // A value that gained or lost encryption at rest is a change worth recording,
        // even when the plaintext is identical.
        if (previous.IsEncrypted != willEncrypt) return false;

        if (!previous.IsEncrypted)
            return string.Equals(previous.Value, plaintext, StringComparison.Ordinal);

        if (_encryptor is null) return false;

        try
        {
            return string.Equals(_encryptor.Decrypt(previous.Value), plaintext, StringComparison.Ordinal);
        }
        catch (Exception ex)
        {
            LogAuditCompareFailed(_logger, group, key, ex);
            return false;
        }
    }

    private async Task ValidateOrThrowAsync<T>(T model, CancellationToken ct)
    {
        var validators = _sp.GetServices<ISettingValidator>();
        var validator  = validators.FirstOrDefault(v => v.CanValidate(typeof(T)));

        if (validator is null) return;

        var (isValid, errors) = await validator.ValidateAsync(model!).ConfigureAwait(false);
        if (!isValid)
        {
            var messages = string.Join("; ", errors.SelectMany(e => e.Value));
            throw new SettingValidationException(typeof(T), errors, messages);
        }
    }

    private async Task FireChangedHandlersAsync<T>(T previous, T current, string groupName, CancellationToken ct)
        where T : new()
    {
        var handlers = _sp.GetServices<ISettingChangedHandler<T>>();
        foreach (var handler in handlers)
        {
            try
            {
                await handler.OnChangedAsync(previous, current, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LogChangeHandlerFailed(_logger, handler.GetType().Name, groupName, ex);
            }
        }
    }

    private T MapToModel<T>(IReadOnlyList<Setting> rows) where T : new()
    {
        var map   = GetTypeMap(typeof(T));
        var model = new T();

        foreach (var prop in map.Properties.Where(p => p.DefaultValue is not null))
        {
            if (Extensions.TryConvertTo(prop.PropertyType, prop.DefaultValue!.ToString()!, out var def))
                prop.Setter(model!, def);
        }

        if (rows.Count == 0) return model;

        var lookup = new Dictionary<string, Setting>(rows.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var r in rows)
            lookup[r.Key] = r;

        foreach (var prop in map.Properties)
        {
            if (!lookup.TryGetValue(prop.Name, out var row)) continue;

            var raw = row.Value;

            if (row.IsEncrypted && _encryptor is not null)
            {
                try   { raw = _encryptor.Decrypt(raw); }
                catch (Exception ex)
                {
                    // Falling through here would leave the property on its default value, so a
                    // rotated-away key would surface as an app running on default credentials
                    // rather than as an error. Fatal unless explicitly opted out of.
                    if (_options.ThrowOnDecryptionFailure)
                        throw new SettingDecryptionException(row.Group, row.Key, ex);

                    LogDecryptFailed(_logger, row.Group, row.Key, ex);
                    continue;
                }
            }

            if (!Extensions.TryConvertTo(prop.PropertyType, raw, out var converted))
            {
                LogConversionFailed(_logger, row.Group, row.Key, prop.PropertyType.Name);
                continue;
            }

            prop.Setter(model!, converted);
        }

        return model;
    }

    private static TypeMap GetTypeMap(Type type)
        => TypeMaps.GetOrAdd(type, t =>
        {
            var props = t
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.CanRead && p.CanWrite)
                .Select(BuildPropertyMap)
                .ToList();

            return new TypeMap(SettingGroupAttribute.ResolveName(t), props);
        });

    private static PropertyMap BuildPropertyMap(PropertyInfo p)
    {
        var sensitive = p.GetCustomAttribute<SensitiveAttribute>() is not null;
        var def       = p.GetCustomAttribute<SettingDefaultAttribute>()?.Value;

        return new PropertyMap(
            Name:         p.Name,
            PropertyType: p.PropertyType,
            TypeName:     p.PropertyType.FullName ?? p.PropertyType.Name,
            IsSensitive:  sensitive,
            DefaultValue: def,
            Getter:       BuildGetter(p),
            Setter:       BuildSetter(p));
    }

    private static Func<object, object?> BuildGetter(PropertyInfo p)
    {
        var inst   = Expression.Parameter(typeof(object), "i");
        var body   = Expression.Convert(Expression.Property(Expression.Convert(inst, p.DeclaringType!), p), typeof(object));
        return Expression.Lambda<Func<object, object?>>(body, inst).Compile();
    }

    private static Action<object, object?> BuildSetter(PropertyInfo p)
    {
        var inst = Expression.Parameter(typeof(object), "i");
        var val  = Expression.Parameter(typeof(object), "v");
        var body = Expression.Assign(
            Expression.Property(Expression.Convert(inst, p.DeclaringType!), p),
            Expression.Convert(val, p.PropertyType));
        return Expression.Lambda<Action<object, object?>>(body, inst, val).Compile();
    }

    private static Func<T, TProp> CompileSelector<T, TProp>(Expression<Func<T, TProp>> selector)
    {
        var info = GetPropertyInfo(selector);
        var del  = CompiledSelectors.GetOrAdd(info, _ => selector.Compile());
        return (Func<T, TProp>)del;
    }

    private static PropertyInfo GetPropertyInfo<T, TProp>(Expression<Func<T, TProp>> selector)
    {
        if (selector.Body is UnaryExpression { Operand: MemberExpression um } && um.Member is PropertyInfo up) return up;
        if (selector.Body is MemberExpression m && m.Member is PropertyInfo mp) return mp;
        throw new InvalidOperationException($"Selector must point directly at a property on {typeof(T).Name}.");
    }

    private bool TryFromCache<T>(string key, out T? value)
        => _cache.TryGetValue(key, out value) && value is not null;

    private static string CacheKey<T>() => CacheKey(GetTypeMap(typeof(T)));

    private static string CacheKey(TypeMap map) => $"dnb:setting:{map.GroupName}";

    [LoggerMessage(EventId = 1000, Level = LogLevel.Information,
        Message = "Settings group '{group}' updated ({count} properties).")]
    private static partial void LogGroupUpdated(ILogger logger, string group, int count);

    [LoggerMessage(EventId = 1001, Level = LogLevel.Information,
        Message = "Settings group '{group}' cleared.")]
    private static partial void LogGroupCleared(ILogger logger, string group);

    [LoggerMessage(EventId = 1002, Level = LogLevel.Error,
        Message = "Change handler {handler} threw for settings group '{group}'.")]
    private static partial void LogChangeHandlerFailed(ILogger logger, string handler, string group, Exception ex);

    [LoggerMessage(EventId = 1003, Level = LogLevel.Error,
        Message = "Failed to decrypt setting '{group}.{key}'; using default.")]
    private static partial void LogDecryptFailed(ILogger logger, string group, string key, Exception ex);

    [LoggerMessage(EventId = 1004, Level = LogLevel.Warning,
        Message = "Cannot convert setting '{group}.{key}' to {type}; skipping.")]
    private static partial void LogConversionFailed(ILogger logger, string group, string key, string type);

    [LoggerMessage(EventId = 1006, Level = LogLevel.Debug,
        Message = "Settings group '{group}' written with no changes; nothing persisted.")]
    private static partial void LogGroupUnchanged(ILogger logger, string group);

    [LoggerMessage(EventId = 1005, Level = LogLevel.Warning,
        Message = "Cannot decrypt the stored value of '{group}.{key}' to compare it; recording an audit entry anyway.")]
    private static partial void LogAuditCompareFailed(ILogger logger, string group, string key, Exception ex);

    private sealed record TypeMap(string GroupName, IReadOnlyList<PropertyMap> Properties);

    private sealed record PropertyMap(
        string Name,
        Type   PropertyType,
        string TypeName,
        bool   IsSensitive,
        object? DefaultValue,
        Func<object, object?>  Getter,
        Action<object, object?> Setter);
}
