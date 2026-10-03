namespace DotNetBoost.Settings.Core.Interfaces;

/// <summary>
/// Shapes a settings group for the READ path only. Registered in DI with
/// <c>.UseProjector&lt;TSettings, TProjector&gt;()</c>; applied by <c>MapSettingsEndpoints</c>
/// on GET.
/// <para>
/// This is the hook for serving a group as something other than its stored values —
/// translating a display name into the request's language, resolving an image id to a URL,
/// masking a field for the UI — without the settings engine needing to know what any of
/// those things are.
/// </para>
/// </summary>
/// <remarks>
/// Three properties hold, and the generated endpoints are what hold them:
/// <list type="bullet">
/// <item>
/// <description>
/// <b>Read path only.</b> A projector is never consulted on POST/PUT, so it cannot influence
/// what is stored. Round-tripping a projected body back through POST is the caller's problem,
/// not something the library pretends to support.
/// </description>
/// </item>
/// <item>
/// <description>
/// <b>Never applied to the ETag.</b> <c>GetVersionAsync</c> keeps covering the stored values.
/// A tag derived from projected output would change with the request's language or with data
/// owned by another component, and conditional writes would stop protecting anything.
/// </description>
/// </item>
/// <item>
/// <description>
/// <b>No projector registered is today's behaviour, byte for byte.</b> The group is serialised
/// exactly as it was before.
/// </description>
/// </item>
/// </list>
/// <para>
/// Programmatic reads through <c>ISettingManager</c> are unaffected: <c>For&lt;T&gt;().GetAsync()</c>
/// always returns the stored group. Projection belongs to the HTTP representation.
/// </para>
/// </remarks>
/// <typeparam name="T">The settings group this projector shapes.</typeparam>
public interface ISettingProjector<T> where T : new()
{
    /// <summary>
    /// Returns what GET should serialise in place of <paramref name="group"/>. Returning
    /// <paramref name="group"/> itself is valid and means "no change".
    /// </summary>
    /// <param name="group">The settings group as stored. Treat it as read-only.</param>
    /// <param name="ct">Cancels the projection.</param>
    Task<object> ProjectAsync(T group, CancellationToken ct = default);
}
