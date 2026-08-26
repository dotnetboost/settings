using System.ComponentModel;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace DotNetBoost.Settings.Core;

/// <summary>Internal helpers for type conversion and serialisation.</summary>
public static class Extensions
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

    /// <summary><see cref="NeedsSerialization(Type)"/> for a property's type.</summary>
    public static bool PropertyNeedsSerialization(this PropertyInfo property)
        => NeedsSerialization(property.PropertyType);

    /// <summary>
    /// Resolves a type by name, searching the loaded assemblies when it is not found in the
    /// calling context. Returns <c>null</c> when no match exists.
    /// </summary>
    public static Type? GetTypeByName(string typeName)
    {
        if (string.IsNullOrWhiteSpace(typeName)) return null;
        return Type.GetType(typeName)
               ?? AppDomain.CurrentDomain.GetAssemblies()
                   .Select(a => a.GetType(typeName))
                   .FirstOrDefault(t => t is not null);
    }

    /// <summary>Wraps a string as a readable UTF-8 stream.</summary>
    public static Stream ToStream(this string text)
        => new MemoryStream(Encoding.UTF8.GetBytes(text));
}
