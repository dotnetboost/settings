using System.ComponentModel;
using System.Text.Json;

namespace DotNetBoost.Settings.Core;

/// <summary>
/// Helpers for turning property values into their stored form and back. Internal: this is the
/// engine's own serialisation detail, not something a consumer should bind to.
/// </summary>
internal static class Extensions
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>Converts a stored string back to <typeparamref name="T"/>.</summary>
    public static T ConvertTo<T>(string value) => (T)ConvertTo(typeof(T), value)!;

    /// <summary>
    /// Converts a stored string back to <paramref name="type"/>, via its <c>TypeConverter</c>
    /// where one exists and JSON otherwise. Blank input yields <c>null</c> for every type but
    /// <see cref="string"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">The value could not be converted.</exception>
    public static object? ConvertTo(Type type, string value)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (type == typeof(string)) return value;
        if (string.IsNullOrWhiteSpace(value)) return null;

        var target    = Nullable.GetUnderlyingType(type) ?? type;
        var converter = TypeDescriptor.GetConverter(target);
        if (converter.CanConvertFrom(typeof(string)))
        {
            try { return converter.ConvertFromInvariantString(value); }
            catch { /* fall through */ }
        }

        try   { return JsonSerializer.Deserialize(value, target, JsonOptions); }
        catch (Exception ex)
        { throw new InvalidOperationException($"Failed to convert value to {type.Name}.", ex); }
    }

    /// <summary>Non-throwing <see cref="ConvertTo(Type, string)"/>.</summary>
    public static bool TryConvertTo(Type type, string value, out object? result)
    {
        try   { result = ConvertTo(type, value); return true; }
        catch { result = null; return false; }
    }

    /// <summary>
    /// Renders a value for storage: the invariant string form for simple types, JSON for
    /// everything else.
    /// </summary>
    public static string ConvertFrom(Type type, object value)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(value);
        if (NeedsSerialization(type)) return JsonSerializer.Serialize(value, JsonOptions);
        var c = TypeDescriptor.GetConverter(type);
        return c.CanConvertTo(typeof(string)) ? c.ConvertToInvariantString(value) ?? string.Empty : value.ToString() ?? string.Empty;
    }

    /// <summary>
    /// Renders a value for storage, as <see cref="ConvertFrom(Type, object)"/>. <c>null</c>
    /// becomes an empty string.
    /// </summary>
    public static string ConvertFrom<T>(T value)
    {
        if (value is null) return string.Empty;
        var type = typeof(T);
        if (NeedsSerialization(type)) return JsonSerializer.Serialize(value, JsonOptions);
        var c = TypeDescriptor.GetConverter(type);
        return c.CanConvertTo(typeof(string)) ? c.ConvertToInvariantString(value) ?? string.Empty : value.ToString() ?? string.Empty;
    }

    /// <summary>
    /// Whether a type is stored as JSON rather than as its invariant string form. Primitives,
    /// enums, and the common value types round-trip as strings; everything else does not.
    /// </summary>
    public static bool NeedsSerialization(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (type == typeof(string) || type.IsPrimitive || type.IsEnum) return false;
        if (type == typeof(decimal)  || type == typeof(DateTime) || type == typeof(Guid) ||
            type == typeof(DateOnly) || type == typeof(TimeOnly)  || type == typeof(TimeSpan) ||
            type == typeof(DateTimeOffset))
        {
            return false;
        }
        return true;
    }



}
