namespace DotNetBoost.Settings.Core.Interfaces;

/// <summary>
/// Shapes a settings group for the READ path only. Registered in DI with
/// <c>.UseProjector&lt;TSettings, TProjector&gt;()</c>; applied by <c>MapSettingsEndpoints</c>
/// on GET.
/// <para>
/// Implement <see cref="ISettingProjector{T, TProjection}"/> instead to name the type you
/// return, and GET's OpenAPI response will describe the projection rather than the stored
/// group. This interface cannot: the projector is resolved per request, long after the route
/// was mapped, so at map time nothing knows what a projection looks like.
/// </para>
/// <para>
/// This is the hook for serving a group as something other than its stored values —
/// translating a display name into the request's language, resolving an image id to a URL,
/// masking a field for the UI — without the settings engine needing to know what any of
/// those things are.
/// </para>
/// </summary>
/// <remarks>
/// Four properties hold, and the generated endpoints are what hold them:
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
/// <item>
/// <description>
/// <b>A projector that throws fails the request.</b> This is deliberately unlike
/// <see cref="ISettingChangedHandler{T}"/> and <see cref="ISettingWriteObserver"/>, whose
/// exceptions are logged and swallowed: a failed notification must not break a write that has
/// already committed, whereas a failed projection means the response would be wrong, and a
/// wrong response must not be hidden.
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

/// <summary>
/// A projector that names the type it returns, so the generated <c>GET</c> can advertise it.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ISettingProjector{T}"/> returns <c>object</c>, which leaves the endpoint
/// declaring the stored group as its 200 response — wrong for every projected group, and not
/// something the library could fix on its own: a projector is resolved from the request scope,
/// long after the route was mapped. Declaring <typeparamref name="TProjection"/> here puts
/// the answer somewhere the compiler checks it, which a third generic argument on
/// <c>UseProjector</c> would not.
/// </para>
/// <para>
/// Everything in <see cref="ISettingProjector{T}"/> still holds — read path only, never
/// applied to the ETag, and a projector that throws fails the request. This changes the
/// documentation of the response, not the response: the body is byte for byte what the
/// untyped interface produces, and <c>POST</c> still accepts the stored group, because
/// advertising the projection as a request body would promise a write the API refuses.
/// </para>
/// </remarks>
/// <typeparam name="T">The settings group this projector shapes.</typeparam>
/// <typeparam name="TProjection">What <c>GET</c> returns in its place.</typeparam>
public interface ISettingProjector<T, TProjection> : ISettingProjector<T>
    where T : new()
    where TProjection : class
{
    /// <summary>
    /// Returns what GET should serialise in place of <paramref name="group"/>.
    /// </summary>
    /// <param name="group">The settings group as stored. Treat it as read-only.</param>
    /// <param name="ct">Cancels the projection.</param>
    new Task<TProjection> ProjectAsync(T group, CancellationToken ct = default);

    /// <summary>
    /// Adapts to the object-returning member the generated endpoint calls, so implementing
    /// the typed interface is all an author has to do.
    /// </summary>
    async Task<object> ISettingProjector<T>.ProjectAsync(T group, CancellationToken ct)
        => await ProjectAsync(group, ct).ConfigureAwait(false);
}
