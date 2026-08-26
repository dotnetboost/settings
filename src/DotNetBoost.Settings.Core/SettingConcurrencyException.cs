namespace DotNetBoost.Settings.Core;

/// <summary>
/// Thrown when a write would overwrite a value that changed after it was read.
/// <para>
/// Settings are written per property with an optimistic concurrency token, so this surfaces
/// only when two writers change the <em>same</em> property concurrently — editing different
/// properties of the same group is safe and does not conflict. The caller should re-read the
/// group, re-apply its change, and retry.
/// </para>
/// </summary>
public sealed class SettingConcurrencyException : Exception
{
    /// <summary>Persistence key of the settings group that could not be written.</summary>
    public string Group { get; }

    /// <summary>Property whose stored value had already moved on.</summary>
    public string Key { get; }

    /// <summary>Creates the exception for a group/key pair that lost the race.</summary>
    public SettingConcurrencyException(string group, string key)
        : base($"Setting '{group}.{key}' was modified by someone else after it was read. " +
               "Re-read the group, re-apply the change, and write again.")
    {
        Group = group;
        Key   = key;
    }

    /// <summary>Creates the exception, preserving the provider's own concurrency failure.</summary>
    public SettingConcurrencyException(string group, string key, Exception innerException)
        : base($"Setting '{group}.{key}' was modified by someone else after it was read. " +
               "Re-read the group, re-apply the change, and write again.", innerException)
    {
        Group = group;
        Key   = key;
    }
}
