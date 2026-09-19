# Caching

Reads go through `ISettingCache` (default: `IMemoryCache`, 10-minute absolute expiration). A per-group `SemaphoreSlim` prevents cache stampedes under concurrent load.

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

