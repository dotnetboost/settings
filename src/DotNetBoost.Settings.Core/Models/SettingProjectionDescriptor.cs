namespace DotNetBoost.Settings.Core.Models;

/// <summary>
/// The type a settings group's <c>GET</c> returns — the projection when a typed projector is
/// registered, otherwise the group itself.
/// </summary>
/// <remarks>
/// Registered as a keyed singleton under the group type by <c>UseProjector</c>, and read once
/// per group by <c>MapSettingsEndpoints</c> to declare the endpoint's 200 response. A keyed
/// entry means there is one per group by construction, with no ordering rule to define: the
/// last registration to write the key wins, exactly as the projector registration it
/// accompanies does.
/// <para>
/// A declaration about the response, nothing more. Nothing resolves a projector from it, and
/// it is read at map time rather than per request.
/// </para>
/// </remarks>
/// <param name="Projection">The CLR type <c>GET</c> serialises.</param>
public sealed record SettingProjectionDescriptor(Type Projection);
