using System.Reflection;

namespace DotNetBoost.Settings.Core.Attributes;

/// <summary>
/// Marks a POCO class as a named settings group and controls its API route segment.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class SettingGroupAttribute(string route) : Attribute
{
    /// <summary>URL-friendly route segment used for the auto-generated settings API endpoint.</summary>
    public string Route { get; } = route;

    /// <summary>
    /// The stable key this group's rows are stored under. Set this to decouple persistence
    /// from the CLR type name, so the class can later be renamed or moved to another
    /// namespace without orphaning the rows already in the store.
    /// <para>
    /// Defaults to the class name when unset, which is the historical behaviour — adding the
    /// attribute alone never moves existing data. Changing it on a class that already has
    /// rows *does* require renaming those rows; see the README's "Group names" section.
    /// </para>
    /// </summary>
    public string? Name { get; set; }

    /// <summary>
    /// Resolves the persistence key for <paramref name="type"/>: the explicit
    /// <see cref="Name"/> when one is set, otherwise the class name.
    /// </summary>
    public static string ResolveName(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        var name = type.GetCustomAttribute<SettingGroupAttribute>()?.Name;
        return string.IsNullOrWhiteSpace(name) ? type.Name : name;
    }
}

/// <summary>
/// Marks a property as sensitive — its value will be encrypted at rest using
/// the registered <see cref="DotNetBoost.Settings.Core.Interfaces.ISettingEncryptor"/>.
/// The value is always decrypted transparently on read.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class SensitiveAttribute : Attribute { }

/// <summary>
/// Provides a compile-time default value that is used when a setting row does
/// not exist in the store, instead of falling back to the CLR default.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class SettingDefaultAttribute(object value) : Attribute
{
    /// <summary>The fallback value applied when the store holds no row for the property.</summary>
    public object Value { get; } = value;
}
