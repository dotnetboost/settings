using DotNetBoost.Settings.Core.Models;

namespace DotNetBoost.Settings.Core.Interfaces;

/// <summary>
/// Persistence contract for settings storage.
/// Implement this to add a custom backend (e.g. Redis, DynamoDB).
/// </summary>
public interface ISettingStore
{
    /// <summary>Returns every stored row for <paramref name="group"/>, or an empty list.</summary>
    Task<IReadOnlyList<Setting>> GetGroupAsync(string group, CancellationToken ct = default);

    /// <summary>Returns a single row, or <c>null</c> when the group/key pair is not stored.</summary>
    Task<Setting?> GetAsync(string group, string key, CancellationToken ct = default);

    /// <summary>
    /// Inserts or updates one row, matched on its group and key.
    /// <para>
    /// When <see cref="Setting.RowVersion"/> is non-null the write is conditional: it must only
    /// apply if the stored row still carries that token, and must throw
    /// <see cref="DotNetBoost.Settings.Core.SettingConcurrencyException"/> otherwise. A
    /// successful write stamps a fresh token via <see cref="Setting.NewRowVersion"/>. A null
    /// token means write unconditionally.
    /// </para>
    /// </summary>
    /// <exception cref="DotNetBoost.Settings.Core.SettingConcurrencyException">
    /// The stored row no longer carries the expected token.
    /// </exception>
    Task UpsertAsync(Setting setting, CancellationToken ct = default);

    /// <summary>
    /// Inserts or updates a batch of rows, atomically where the backend supports it.
    /// Implementations may assume every item belongs to the same group. Concurrency tokens are
    /// honoured per row exactly as in <see cref="UpsertAsync"/>.
    /// </summary>
    /// <exception cref="DotNetBoost.Settings.Core.SettingConcurrencyException">
    /// A stored row no longer carries the expected token.
    /// </exception>
    Task UpsertManyAsync(IEnumerable<Setting> settings, CancellationToken ct = default);

    /// <summary>Removes one row. Succeeds silently when it does not exist.</summary>
    Task DeleteAsync(string group, string key, CancellationToken ct = default);

    /// <summary>Removes every row belonging to <paramref name="group"/>.</summary>
    Task DeleteGroupAsync(string group, CancellationToken ct = default);

    /// <summary>Whether <paramref name="group"/> has at least one stored row.</summary>
    Task<bool> GroupExistsAsync(string group, CancellationToken ct = default);

    /// <summary>Number of stored rows belonging to <paramref name="group"/>.</summary>
    Task<int> CountAsync(string group, CancellationToken ct = default);
}
