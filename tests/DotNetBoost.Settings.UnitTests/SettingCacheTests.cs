using DotNetBoost.Settings.Core.Services;
using Microsoft.Extensions.Caching.Memory;

namespace DotNetBoost.Settings.UnitTests;

public class SettingCacheTests
{
    private static SettingCache CreateCache() => new(new MemoryCache(new MemoryCacheOptions()));

    [Fact]
    public void Set_ThenGet_ReturnsStoredValue()
    {
        var cache = CreateCache();
        cache.Set("key", 587, TimeSpan.FromMinutes(5));
        var found = cache.TryGetValue<int>("key", out var result);
        Assert.True(found);
        Assert.Equal(587, result);
    }

    [Fact]
    public void TryGetValue_MissingKey_ReturnsFalse()
    {
        var cache = CreateCache();
        Assert.False(cache.TryGetValue<int>("missing", out var r));
        Assert.Equal(0, r);
    }

    [Fact]
    public void TryGetValue_WrongType_ReturnsFalse()
    {
        var cache = CreateCache();
        cache.Set("key", 587, TimeSpan.FromMinutes(5));
        Assert.False(cache.TryGetValue<string>("key", out _));
    }

    [Fact]
    public void Set_OverwritesExistingKey()
    {
        var cache = CreateCache();
        cache.Set("key", "first",  TimeSpan.FromMinutes(5));
        cache.Set("key", "second", TimeSpan.FromMinutes(5));
        cache.TryGetValue<string>("key", out var r);
        Assert.Equal("second", r);
    }

    [Fact]
    public void Remove_DeletesExistingKey()
    {
        var cache = CreateCache();
        cache.Set("key", 1, TimeSpan.FromMinutes(5));
        cache.Remove("key");
        Assert.False(cache.TryGetValue<int>("key", out _));
    }

    [Fact]
    public void Remove_NonExistentKey_DoesNotThrow()
    {
        var cache = CreateCache();
        var ex = Record.Exception(() => cache.Remove("not-there"));
        Assert.Null(ex);
    }

    [Fact]
    public async Task Set_Expired_EntryNotFound()
    {
        var cache = CreateCache();
        cache.Set("key", 99, TimeSpan.FromMilliseconds(30));
        await Task.Delay(80);
        Assert.False(cache.TryGetValue<int>("key", out _));
    }

    [Fact]
    public void Set_NullValue_Throws()
        => Assert.ThrowsAny<Exception>(() => CreateCache().Set<string?>("key", null, TimeSpan.FromMinutes(1)));

    [Fact]
    public void Constructor_NullMemoryCache_Throws()
        => Assert.Throws<ArgumentNullException>(() => new SettingCache(null!));
}
