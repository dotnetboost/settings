namespace DotNetBoost.Settings.Core;

/// <summary>
/// Runtime options for the settings engine. Registered as a singleton by
/// <c>services.AddSettings()</c> and configured through <see cref="SettingBuilder"/>.
/// </summary>
public sealed class SettingOptions
{
    /// <summary>
    /// How long a resolved settings group is held in <c>ISettingCache</c>. Default: 10 minutes.
    /// Set via <c>WithCacheDuration()</c>.
    /// </summary>
    public TimeSpan CacheDuration { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Whether a <c>[Sensitive]</c> value that cannot be decrypted aborts the read with a
    /// <see cref="SettingDecryptionException"/>. Default: <c>true</c>.
    /// <para>
    /// When false, the failure is logged and the property keeps its default value — which
    /// means a rotated-away key leaves the application running on default secrets. Turn it
    /// off via <c>IgnoreDecryptionFailures()</c> only when that is genuinely preferable.
    /// </para>
    /// </summary>
    public bool ThrowOnDecryptionFailure { get; set; } = true;
}
