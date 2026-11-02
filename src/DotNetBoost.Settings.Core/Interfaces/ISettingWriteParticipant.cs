namespace DotNetBoost.Settings.Core.Interfaces;

/// <summary>
/// Takes part in every write to every settings group, before the store saves it. This is the
/// hook for work that must commit together with the change, or not at all: an audit entry is
/// the reason it exists.
/// </summary>
/// <remarks>
/// <para>
/// <b>Failure policy: an exception aborts the write.</b> The store is not called, the cache
/// keeps its entry, and no <see cref="ISettingWriteObserver"/> or
/// <see cref="ISettingChangedHandler{T}"/> runs. The exception reaches the caller unchanged.
/// This is the opposite of an observer's policy, and the reason both hooks exist: an observer
/// runs after the commit and cannot fail it, a participant runs before it and can.
/// </para>
/// <para>
/// <b>Enlist, never save.</b> A participant adds what it wants committed to the store's own
/// unit of work, and the store saves it with the rows. For Entity Framework Core that is
/// <c>EfCoreSettingWriteScope.Add</c>. Calling <c>SaveChangesAsync</c> yourself, or doing
/// anything with an effect outside that unit of work — sending a message, calling a service —
/// defeats the point: the write can still fail after the participant has run, on a
/// concurrency conflict for example, and only what was enlisted is undone.
/// </para>
/// <para>
/// Only a store that implements <see cref="IEnlistingSettingStore"/> accepts participants.
/// <c>Build()</c> refuses a participant for a provider that has not declared support, and a
/// write refuses one at run time if the store cannot enlist. Of the shipped providers, Entity
/// Framework Core does; Dapper and MongoDB do not.
/// </para>
/// <para>
/// Several may be registered; all run, in registration order, and the first to throw stops
/// the rest. A write that changes nothing calls none of them.
/// </para>
/// </remarks>
public interface ISettingWriteParticipant
{
    /// <summary>
    /// Called once per write, before the store saves it. Throwing aborts the write.
    /// </summary>
    /// <param name="write">
    /// The write about to be committed. The same instance is handed to every
    /// <see cref="ISettingWriteObserver"/> once it has been, so the two share a
    /// <see cref="SettingWrite.CorrelationId"/>.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    Task OnWritingAsync(SettingWrite write, CancellationToken ct = default);
}

/// <summary>
/// A store that commits what <see cref="ISettingWriteParticipant"/>s enlist together with its
/// own write. Implement it only when that is true: the manager refuses participants for a store
/// that does not implement it, rather than run them with no atomicity.
/// </summary>
/// <remarks>
/// How a participant enlists is the store's own API, since only the store knows its unit of
/// work. What the manager needs from every such store is the one call here.
/// </remarks>
public interface IEnlistingSettingStore : ISettingStore
{
    /// <summary>
    /// Drops everything participants enlisted for the current write, without saving it. Called
    /// when the write will not be committed: a participant threw, or the store's write did.
    /// Must be safe to call when nothing was enlisted.
    /// </summary>
    void DiscardEnlisted();
}
