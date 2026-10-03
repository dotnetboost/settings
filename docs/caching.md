# Caching

Reads go through `ISettingCache` (default: `IMemoryCache`, 10-minute absolute expiration). A per-group `SemaphoreSlim` prevents cache stampedes under concurrent load.

**What is cached is the stored rows, not the settings object.** Every `GetAsync` materialises a
fresh instance from them, so two callers never share one: assigning to a property of what you
read changes nothing for the next reader, and nothing for `GET /api/settings/{route}`. The cache
is there to skip the round trip to the store; the mapping is reflection over a handful of
properties and is cheap to repeat.

Two consequences worth knowing:

- A `[Sensitive]` value is decrypted per read rather than once per cache entry. For a
  distributed cache this is the better trade anyway — what is written to Redis is the stored
  ciphertext, not the decrypted model.
- A custom `ISettingCache` is handed `Setting[]`. Implementations that serialise, like the Redis
  one below, need nothing special for this; the type is public and plain.

```csharp
builder.Services.AddSettings()
    .UseEntityFrameworkCore<AppDbContext>()
    .WithCacheDuration(TimeSpan.FromMinutes(5))
    .UseCustomCache<RedisSettingCache>()   // swap in a distributed cache
    .Build();
```

> **Multi-node note:** the default cache is per-instance in-memory. In a multi-node deployment, use `UseCustomCache<T>()` with a distributed cache (Redis, etc.) so a write on one node invalidates the cache on all nodes.

```csharp
public class RedisSettingCache(IConnectionMultiplexer redis) : ISettingCache
{
    private readonly IDatabase _db = redis.GetDatabase();

    public bool TryGetValue<T>(string key, out T? value)
    {
        var raw = _db.StringGet(key);
        if (!raw.HasValue) { value = default; return false; }
        value = JsonSerializer.Deserialize<T>(raw!);
        return value is not null;
    }

    public void Set<T>(string key, T value, TimeSpan duration)
        => _db.StringSet(key, JsonSerializer.Serialize(value), duration);

    public void Remove(string key) => _db.KeyDelete(key);
}
```

