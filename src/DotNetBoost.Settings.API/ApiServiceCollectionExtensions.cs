using DotNetBoost.Settings.API;
using DotNetBoost.Settings.Core;
using DotNetBoost.Settings.Core.Interfaces;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Builder methods that need ASP.NET Core.</summary>
public static class SettingsApiBuilderExtensions
{
    /// <summary>
    /// Captures the signed-in user of the current request as <see cref="SettingWrite.Actor"/>,
    /// so a write observer can report who made a change instead of leaving it unknown.
    /// Also registers <c>IHttpContextAccessor</c>, which it needs.
    /// </summary>
    public static SettingBuilder UseHttpContextActor(this SettingBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.AddHttpContextAccessor();
        return builder.UseActorAccessor<HttpContextSettingActorAccessor>();
    }
}
