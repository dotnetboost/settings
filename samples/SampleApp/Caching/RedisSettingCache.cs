using DotNetBoost.Settings.Core.Interfaces;
using StackExchange.Redis;
using System.Text.Json;

namespace SampleApp.Caching;

/// <summary>
/// A distributed <see cref="ISettingCache"/> backed by the Redis container the AppHost
/// starts. Registered with <c>.UseCustomCache&lt;RedisSettingCache&gt;()</c>, which replaces
/// the default <c>IMemoryCache</c> implementation.
/// </summary>
/// <remarks>
/// Why bother: with the in-memory cache every API instance keeps its own copy, so a write
/// on one instance leaves the others serving stale settings until their entry expires.
/// A shared Redis instance makes an update visible to the whole fleet immediately.
/// <para>
/// <see cref="ISettingCache"/> is synchronous, so this uses StackExchange.Redis' sync API
/// rather than blocking on the async one.
/// </para>
/// </remarks>
public sealed class RedisSettingCache : ISettingCache
{
    // Namespaced so settings never collide with anything else sharing the Redis instance.
    private const string KeyPrefix = "dotnetboost:settings:";

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly IDatabase                 _db;
    private readonly ILogger<RedisSettingCache> _logger;

    public RedisSettingCache(IConnectionMultiplexer redis, ILogger<RedisSettingCache> logger)
    {
        ArgumentNullException.ThrowIfNull(redis);
        _db     = redis.GetDatabase();
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public bool TryGetValue<T>(string key, out T? value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        RedisValue cached;
        try
        {
            cached = _db.StringGet(KeyPrefix + key);
        }
        catch (RedisException ex)
        {
            // A cache miss is always safe: the caller falls back to the store.
            LogCacheUnavailable(_logger, key, ex);
            value = default;
            return false;
        }

        if (cached.IsNullOrEmpty)
        {
            value = default;
            return false;
        }

        try
        {
            value = JsonSerializer.Deserialize<T>((string)cached!, SerializerOptions);
            return value is not null;
        }
        catch (JsonException ex)
        {
            // Stale shape left by an older deployment — drop it and re-read from the store.
            LogCorruptEntry(_logger, key, ex);
            Remove(key);
            value = default;
            return false;
        }
    }

    public void Set<T>(string key, T value, TimeSpan duration)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(value);

        try
        {
            _db.StringSet(KeyPrefix + key, JsonSerializer.Serialize(value, SerializerOptions), duration);
        }
        catch (RedisException ex)
        {
            // Failing to cache must never fail the request that produced the value.
            LogCacheUnavailable(_logger, key, ex);
        }
    }

    public void Remove(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        try
        {
            _db.KeyDelete(KeyPrefix + key);
        }
        catch (RedisException ex)
        {
            LogCacheUnavailable(_logger, key, ex);
        }
    }

    private static readonly Action<ILogger, string, Exception?> LogCacheUnavailable =
        LoggerMessage.Define<string>(
            LogLevel.Warning, new EventId(3000, nameof(LogCacheUnavailable)),
            "Redis cache unavailable for setting key {Key}; falling through to the store.");

    private static readonly Action<ILogger, string, Exception?> LogCorruptEntry =
        LoggerMessage.Define<string>(
            LogLevel.Warning, new EventId(3001, nameof(LogCorruptEntry)),
            "Cached value for setting key {Key} could not be deserialized; evicting it.");
}
