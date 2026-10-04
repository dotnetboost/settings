using DotNetBoost.Settings.Core.Interfaces;
using Microsoft.AspNetCore.Http;
using System.Security.Claims;

namespace DotNetBoost.Settings.API;

/// <summary>
/// Reports the signed-in user of the current request as
/// <see cref="SettingWrite.Actor"/>. Register with <c>.UseHttpContextActor()</c>.
/// </summary>
/// <remarks>
/// Prefers <see cref="ClaimTypes.NameIdentifier"/> over <c>Identity.Name</c>: the subject
/// identifier is stable, while a display name can be changed by the person it names, which
/// is the wrong property for something a record of who-did-what is keyed on.
/// <para>
/// Returns <c>null</c> outside a request, for an unauthenticated one, and when neither value
/// is present — the generated endpoints are anonymous unless the settings class carries
/// <c>[Authorize]</c>, so that last case is a real one rather than a defensive branch. Null
/// means "not captured" and never stands in for a user.
/// </para>
/// </remarks>
/// <param name="httpContextAccessor">Supplies the ambient request, if there is one.</param>
public sealed class HttpContextSettingActorAccessor(IHttpContextAccessor httpContextAccessor)
    : ISettingActorAccessor
{
    /// <inheritdoc/>
    public string? GetActor()
    {
        var user = httpContextAccessor.HttpContext?.User;
        if (user?.Identity is not { IsAuthenticated: true }) return null;

        var subject = user.FindFirstValue(ClaimTypes.NameIdentifier);
        return string.IsNullOrWhiteSpace(subject) ? NullIfBlank(user.Identity.Name) : subject;
    }

    private static string? NullIfBlank(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value;
}
