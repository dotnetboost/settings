namespace DotNetBoost.Settings.Core.Interfaces;

/// <summary>What a <see cref="SettingWrite"/> did to the group.</summary>
public enum SettingWriteKind
{
    /// <summary>Properties were written. <see cref="SettingWrite.Changes"/> lists them.</summary>
    Updated,

    /// <summary>
    /// Every row in the group was deleted by <c>ClearAsync</c>.
    /// <see cref="SettingWrite.Changes"/> lists one entry per deleted row, with the value it
    /// held as <see cref="SettingChange.OldValue"/> and a <c>null</c>
    /// <see cref="SettingChange.NewValue"/>. Empty when the group had nothing stored.
    /// </summary>
    Cleared
}

/// <summary>One property's change within a write.</summary>
/// <param name="PropertyName">The property that changed, as named on the settings class.</param>
/// <param name="OldValue">
/// The value before the write, or <c>null</c> when the property had never been stored.
/// Always <c>null</c> when <paramref name="IsRedacted"/> is set.
/// </param>
/// <param name="NewValue">
/// The value as written. Always <c>null</c> when <paramref name="IsRedacted"/> is set, and for
/// a <see cref="SettingWriteKind.Cleared"/> write, which writes nothing.
/// </param>
/// <param name="IsRedacted">
/// Whether the property is <c>[Sensitive]</c>. Its values are withheld rather than reported:
/// the library decides what is sensitive, and an observer only learns that it changed. An
/// observer cannot opt out of this, which is the point — a recording or forwarding observer
/// should not be the place a secret escapes.
/// </param>
public sealed record SettingChange(
    string PropertyName,
    string? OldValue,
    string? NewValue,
    bool IsRedacted);

/// <summary>
/// One write to one settings group. An <see cref="ISettingWriteParticipant"/> is handed it
/// before the store commits, and every <see cref="ISettingWriteObserver"/> the same instance
/// after.
/// </summary>
/// <param name="GroupName">
/// The persistence key the group's rows are stored under — the <c>Name</c> on
/// <c>[SettingGroup]</c>, or the class name when none is set.
/// </param>
/// <param name="Route">
/// The group's API route segment, from <c>[SettingGroup]</c>. Falls back to
/// <paramref name="GroupName"/> for a settings class that carries no attribute.
/// </param>
/// <param name="Kind">Whether properties were written or the whole group was cleared.</param>
/// <param name="Changes">
/// One entry per property that actually changed, or, when <paramref name="Kind"/> is
/// <see cref="SettingWriteKind.Cleared"/>, one per row deleted. A set that changed nothing
/// raises no <see cref="SettingWrite"/> at all.
/// </param>
/// <param name="Actor">
/// Who made the change, from the registered <see cref="ISettingActorAccessor"/>. <c>null</c>
/// means not captured — read it as "unknown", never as a claim about who acted.
/// </param>
/// <param name="CorrelationId">
/// One value per write, so the several <see cref="Changes"/> of a single save are
/// identifiable as one edit. The ambient <c>Activity</c> id when there is one, otherwise a
/// fresh value.
/// </param>
/// <param name="OccurredAt">
/// UTC timestamp of the write, taken just before the store is called.
/// </param>
public sealed record SettingWrite(
    string GroupName,
    string Route,
    SettingWriteKind Kind,
    IReadOnlyList<SettingChange> Changes,
    string? Actor,
    string CorrelationId,
    DateTime OccurredAt)
{
    /// <summary>
    /// The settings class the group is defined by, so an observer can read its attributes
    /// without rebuilding the name-to-type map by scanning assemblies.
    /// </summary>
    /// <remarks>
    /// Always set on a write the library raises. <c>null</c> only on a
    /// <see cref="SettingWrite"/> constructed elsewhere, such as in a test.
    /// <para>
    /// Identify the group by <see cref="GroupName"/>, not by this type's name: moving the class
    /// to another namespace changes <see cref="Type.FullName"/> but not the rows it reads, so a
    /// record keyed on the type would split one group's history in two.
    /// </para>
    /// </remarks>
    public Type? GroupType { get; init; }
}

/// <summary>
/// Observes every completed write to every settings group. This is the hook a webhook
/// publisher, a search indexer or a distributed cache signal implements.
/// </summary>
/// <remarks>
/// <para>
/// <b>Failure policy: an exception is logged and swallowed.</b> An observer runs after the
/// store write has committed and the cache entry has been evicted, so failing the call would
/// report a write that in fact succeeded. Each observer is wrapped individually, so one
/// failure does not skip the rest. The same policy applies to
/// <see cref="ISettingChangedHandler{T}"/>, and deliberately not to
/// <see cref="ISettingProjector{T}"/>, which can still fail a request because a failed
/// projection would otherwise return a wrong response.
/// </para>
/// <para>
/// In-process only. Several observers may be registered; all are invoked in registration
/// order.
/// </para>
/// <para>
/// For work that must commit together with the write, or not at all — an audit entry —
/// implement <see cref="ISettingWriteParticipant"/> instead.
/// </para>
/// <para>
/// This is the untyped half of the notification pair. Use
/// <see cref="ISettingChangedHandler{T}"/> instead when you want the typed before/after models
/// of one group — "if <c>MailSettings.Host</c> changed, reconnect" — rather than a
/// per-property diff of every group.
/// </para>
/// </remarks>
public interface ISettingWriteObserver
{
    /// <summary>Called once per completed write. Exceptions are logged and swallowed.</summary>
    Task OnWrittenAsync(SettingWrite write, CancellationToken ct = default);
}

/// <summary>
/// Supplies who is making the current change, for <see cref="SettingWrite.Actor"/>.
/// </summary>
/// <remarks>
/// The default registered by <c>AddSettings()</c> returns <c>null</c>. ASP.NET applications
/// can register the <c>HttpContext</c>-backed one from <c>DotNetBoost.Settings.API</c> with
/// <c>.UseHttpContextActor()</c>.
/// </remarks>
public interface ISettingActorAccessor
{
    /// <summary>
    /// The current actor, or <c>null</c> when it cannot be determined. Returning a
    /// placeholder instead of <c>null</c> would turn "nobody captured this" into a claim
    /// about who acted, so do not.
    /// </summary>
    string? GetActor();
}
