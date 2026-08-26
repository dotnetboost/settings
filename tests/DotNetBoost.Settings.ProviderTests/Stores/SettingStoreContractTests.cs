using DotNetBoost.Settings.Core;
using DotNetBoost.Settings.Core.Interfaces;
using DotNetBoost.Settings.Core.Models;

namespace DotNetBoost.Settings.ProviderTests.Stores;

/// <summary>
/// Contract tests that every <see cref="ISettingStore"/> implementation must pass.
/// Inherit and implement <see cref="CreateStoreAsync"/> for a concrete provider.
/// </summary>
public abstract class SettingStoreContractTests
{
    protected abstract Task<ISettingStore> CreateStoreAsync();

    [Fact]
    public async Task Upsert_ThenGet_ReturnsRow()
    {
        var store = await CreateStoreAsync();
        await store.UpsertAsync(Setting("Mail", "Port", "587"));
        var result = await store.GetAsync("Mail", "Port");
        Assert.NotNull(result);
        Assert.Equal("587", result!.Value);
    }

    [Fact]
    public async Task Upsert_ExistingKey_UpdatesValue()
    {
        var store = await CreateStoreAsync();
        await store.UpsertAsync(Setting("Mail", "Port", "25"));
        await store.UpsertAsync(Setting("Mail", "Port", "587"));
        var result = await store.GetAsync("Mail", "Port");
        Assert.Equal("587", result!.Value);
    }

    [Fact]
    public async Task Get_NonExistentKey_ReturnsNull()
    {
        var store = await CreateStoreAsync();
        Assert.Null(await store.GetAsync("NoGroup", "NoKey"));
    }

    [Fact]
    public async Task GetGroup_ReturnsAllRowsForGroup()
    {
        var store = await CreateStoreAsync();
        await store.UpsertAsync(Setting("Mail", "Host", "smtp"));
        await store.UpsertAsync(Setting("Mail", "Port", "587"));
        var rows = await store.GetGroupAsync("Mail");
        Assert.Equal(2, rows.Count);
    }

    [Fact]
    public async Task GetGroup_EmptyGroup_ReturnsEmptyList()
    {
        var store = await CreateStoreAsync();
        Assert.Empty(await store.GetGroupAsync("NonExistent"));
    }

    [Fact]
    public async Task GetGroup_DoesNotReturnOtherGroups()
    {
        var store = await CreateStoreAsync();
        await store.UpsertAsync(Setting("Mail", "Host", "smtp"));
        await store.UpsertAsync(Setting("Sms",  "ApiKey", "abc"));
        var rows = await store.GetGroupAsync("Mail");
        Assert.Single(rows);
    }

    [Fact]
    public async Task UpsertMany_InsertsAll()
    {
        var store = await CreateStoreAsync();
        await store.UpsertManyAsync([
            Setting("Mail", "Host",   "smtp"),
            Setting("Mail", "Port",   "587"),
            Setting("Mail", "UseSsl", "true")
        ]);
        Assert.Equal(3, await store.CountAsync("Mail"));
    }

    [Fact]
    public async Task UpsertMany_EmptyList_NoOp()
    {
        var store = await CreateStoreAsync();
        var ex = await Record.ExceptionAsync(() => store.UpsertManyAsync([]));
        Assert.Null(ex);
    }

    [Fact]
    public async Task UpsertMany_UpdatesExistingRows()
    {
        var store = await CreateStoreAsync();
        await store.UpsertAsync(Setting("Mail", "Port", "25"));
        await store.UpsertManyAsync([Setting("Mail", "Port", "587")]);
        var result = await store.GetAsync("Mail", "Port");
        Assert.Equal("587", result!.Value);
    }

    [Fact]
    public async Task Delete_RemovesSingleRow()
    {
        var store = await CreateStoreAsync();
        await store.UpsertAsync(Setting("Mail", "Port", "587"));
        await store.UpsertAsync(Setting("Mail", "Host", "smtp"));
        await store.DeleteAsync("Mail", "Port");
        Assert.Null(await store.GetAsync("Mail", "Port"));
        Assert.NotNull(await store.GetAsync("Mail", "Host"));
    }

    [Fact]
    public async Task Delete_NonExistentRow_DoesNotThrow()
    {
        var store = await CreateStoreAsync();
        var ex = await Record.ExceptionAsync(() => store.DeleteAsync("Ghost", "Key"));
        Assert.Null(ex);
    }

    [Fact]
    public async Task DeleteGroup_RemovesAllRows()
    {
        var store = await CreateStoreAsync();
        await store.UpsertAsync(Setting("Mail", "Port", "587"));
        await store.UpsertAsync(Setting("Mail", "Host", "smtp"));
        await store.DeleteGroupAsync("Mail");
        Assert.Equal(0, await store.CountAsync("Mail"));
    }

    [Fact]
    public async Task DeleteGroup_DoesNotAffectOtherGroups()
    {
        var store = await CreateStoreAsync();
        await store.UpsertAsync(Setting("Mail", "Port", "587"));
        await store.UpsertAsync(Setting("Sms",  "ApiKey", "abc"));
        await store.DeleteGroupAsync("Mail");
        Assert.Equal(1, await store.CountAsync("Sms"));
    }

    [Fact]
    public async Task Count_ReturnsCorrectNumber()
    {
        var store = await CreateStoreAsync();
        await store.UpsertAsync(Setting("Mail", "Port", "587"));
        Assert.Equal(1, await store.CountAsync("Mail"));
    }

    [Fact]
    public async Task Count_EmptyGroup_ReturnsZero()
    {
        var store = await CreateStoreAsync();
        Assert.Equal(0, await store.CountAsync("Empty"));
    }

    [Fact]
    public async Task GroupExists_ReturnsTrue_WhenRowsExist()
    {
        var store = await CreateStoreAsync();
        await store.UpsertAsync(Setting("Mail", "Port", "587"));
        Assert.True(await store.GroupExistsAsync("Mail"));
    }

    [Fact]
    public async Task GroupExists_ReturnsFalse_WhenNoRows()
    {
        var store = await CreateStoreAsync();
        Assert.False(await store.GroupExistsAsync("Ghost"));
    }

    // ---- optimistic concurrency ----------------------------------------------------------
    // Every store must honour Setting.RowVersion identically, so these live in the shared
    // contract rather than in any one provider's suite.

    [Fact]
    public async Task Upsert_StampsAConcurrencyToken()
    {
        var store = await CreateStoreAsync();

        await store.UpsertAsync(Setting("Mail", "Host", "smtp"));

        var stored = await store.GetAsync("Mail", "Host");
        Assert.NotNull(stored!.RowVersion);
        Assert.NotEmpty(stored.RowVersion!);
    }

    [Fact]
    public async Task Upsert_ChangesTheTokenOnEveryWrite()
    {
        var store = await CreateStoreAsync();
        await store.UpsertAsync(Setting("Mail", "Host", "first"));
        var first = (await store.GetAsync("Mail", "Host"))!;

        await store.UpsertAsync(WithVersion(Setting("Mail", "Host", "second"), first.RowVersion));

        var second = (await store.GetAsync("Mail", "Host"))!;
        Assert.NotEqual(first.RowVersion, second.RowVersion);
    }

    [Fact]
    public async Task Upsert_WithCurrentToken_Succeeds()
    {
        var store = await CreateStoreAsync();
        await store.UpsertAsync(Setting("Mail", "Host", "first"));
        var stored = (await store.GetAsync("Mail", "Host"))!;

        await store.UpsertAsync(WithVersion(Setting("Mail", "Host", "second"), stored.RowVersion));

        Assert.Equal("second", (await store.GetAsync("Mail", "Host"))!.Value);
    }

    [Fact]
    public async Task Upsert_WithStaleToken_ThrowsAndLeavesTheStoredValueIntact()
    {
        var store = await CreateStoreAsync();
        await store.UpsertAsync(Setting("Mail", "Host", "original"));
        var stale = (await store.GetAsync("Mail", "Host"))!.RowVersion;

        // Someone else writes first, which moves the token on.
        await store.UpsertAsync(WithVersion(Setting("Mail", "Host", "theirs"), stale));

        var ex = await Assert.ThrowsAsync<SettingConcurrencyException>(
            () => store.UpsertAsync(WithVersion(Setting("Mail", "Host", "mine"), stale)));

        Assert.Equal("Mail", ex.Group);
        Assert.Equal("Host", ex.Key);
        Assert.Equal("theirs", (await store.GetAsync("Mail", "Host"))!.Value);
    }

    [Fact]
    public async Task Upsert_WithNullToken_WritesUnconditionally()
    {
        // Null means "no expectation" — what a first insert and a pre-versioning row need.
        var store = await CreateStoreAsync();
        await store.UpsertAsync(Setting("Mail", "Host", "first"));

        await store.UpsertAsync(Setting("Mail", "Host", "blind"));

        Assert.Equal("blind", (await store.GetAsync("Mail", "Host"))!.Value);
    }

    [Fact]
    public async Task UpsertMany_WithStaleTokenOnOneRow_Throws()
    {
        var store = await CreateStoreAsync();
        await store.UpsertManyAsync([Setting("Mail", "Host", "h1"), Setting("Mail", "Port", "25")]);
        var host = (await store.GetAsync("Mail", "Host"))!;
        var port = (await store.GetAsync("Mail", "Port"))!;

        await store.UpsertAsync(WithVersion(Setting("Mail", "Port", "587"), port.RowVersion));

        await Assert.ThrowsAsync<SettingConcurrencyException>(() => store.UpsertManyAsync(
        [
            WithVersion(Setting("Mail", "Host", "h2"), host.RowVersion),
            WithVersion(Setting("Mail", "Port", "999"), port.RowVersion),   // stale
        ]));
    }

    /// <summary>
    /// The scenario the whole mechanism exists for: two writers who each read the group, then
    /// change different properties. Neither should lose the other's edit.
    /// </summary>
    [Fact]
    public async Task ConcurrentWritesToDifferentProperties_BothSurvive()
    {
        var store = await CreateStoreAsync();
        await store.UpsertManyAsync([Setting("Mail", "Host", "original"), Setting("Mail", "Port", "25")]);

        var hostAsReadByA = (await store.GetAsync("Mail", "Host"))!.RowVersion;
        var portAsReadByB = (await store.GetAsync("Mail", "Port"))!.RowVersion;

        await store.UpsertAsync(WithVersion(Setting("Mail", "Host", "changed-by-A"), hostAsReadByA));
        await store.UpsertAsync(WithVersion(Setting("Mail", "Port", "587"), portAsReadByB));

        Assert.Equal("changed-by-A", (await store.GetAsync("Mail", "Host"))!.Value);
        Assert.Equal("587",          (await store.GetAsync("Mail", "Port"))!.Value);
    }

    private static Setting Setting(string group, string key, string value)
        => new() { Group = group, Key = key, Value = value, Type = "System.String" };

    private static Setting WithVersion(Setting setting, byte[]? rowVersion)
    {
        setting.RowVersion = rowVersion;
        return setting;
    }
}
