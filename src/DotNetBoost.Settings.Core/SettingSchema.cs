using DotNetBoost.Settings.Core.Attributes;
using System.Collections.Concurrent;
using System.Reflection;

namespace DotNetBoost.Settings.Core;

/// <summary>One property of a settings group, as the engine sees it.</summary>
/// <param name="Name">The property name on the settings class.</param>
/// <param name="ClrType">
/// The property's type name — the underlying type for a <see cref="Nullable{T}"/>.
/// </param>
/// <param name="IsNullable">Whether the property is a <see cref="Nullable{T}"/>.</param>
/// <param name="DefaultValue">
/// The value used when the store holds no row: the <c>[SettingDefault]</c> value when there is
/// one, otherwise whatever a fresh instance of the class initialises the property to. This is
/// a compile-time fact about the class, never anything read from the store.
/// </param>
/// <param name="IsSensitive">Whether the property carries <c>[Sensitive]</c>.</param>
public sealed record SettingPropertyInfo(
    string Name,
    string ClrType,
    bool IsNullable,
    object? DefaultValue,
    bool IsSensitive);

/// <summary>
/// Describes the shape of a settings group — the properties the engine will store, their
/// types and their defaults.
/// </summary>
/// <remarks>
/// This is also where the rule for <em>which</em> properties count lives, so the description
/// and the engine cannot drift apart: <c>SettingManager</c> builds its own map from the same
/// <see cref="StorableProperties"/>. A description that listed a property the engine ignores
/// would be worse than no description.
/// </remarks>
public static class SettingSchema
{
    private static readonly ConcurrentDictionary<Type, IReadOnlyList<SettingPropertyInfo>> Descriptions = new();

    /// <summary>
    /// The properties of <paramref name="groupType"/> that the engine reads and writes: public
    /// instance properties with both a getter and a setter.
    /// </summary>
    public static IEnumerable<PropertyInfo> StorableProperties(Type groupType)
    {
        ArgumentNullException.ThrowIfNull(groupType);
        return groupType
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.CanWrite);
    }

    /// <summary>
    /// Describes every storable property of <paramref name="groupType"/>. Cached per type —
    /// it is a fact about the class, not about what is stored.
    /// </summary>
    public static IReadOnlyList<SettingPropertyInfo> Describe(Type groupType)
    {
        ArgumentNullException.ThrowIfNull(groupType);
        return Descriptions.GetOrAdd(groupType, BuildDescription);
    }

    private static IReadOnlyList<SettingPropertyInfo> BuildDescription(Type groupType)
    {
        // One throwaway instance, so a property with no [SettingDefault] can report whatever
        // the class initialises it to — which is the value a read actually falls back to.
        object? blank = null;
        try   { blank = Activator.CreateInstance(groupType); }
        catch (MissingMethodException) { /* no parameterless ctor: initialiser defaults are unknowable */ }

        return
        [
            .. StorableProperties(groupType).Select(p =>
            {
                var underlying = Nullable.GetUnderlyingType(p.PropertyType);

                return new SettingPropertyInfo(
                    Name:         p.Name,
                    ClrType:      (underlying ?? p.PropertyType).Name,
                    IsNullable:   underlying is not null,
                    DefaultValue: ResolveDefault(p, blank),
                    IsSensitive:  p.GetCustomAttribute<SensitiveAttribute>() is not null);
            })
        ];
    }

    private static object? ResolveDefault(PropertyInfo property, object? blank)
    {
        if (property.GetCustomAttribute<SettingDefaultAttribute>() is { } attribute)
            return attribute.Value;

        if (blank is null) return null;

        try   { return property.GetValue(blank); }
        catch (TargetInvocationException) { return null; }
    }
}
