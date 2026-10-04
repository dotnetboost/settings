namespace DotNetBoost.Settings.API;

/// <summary>One registered settings group, as <c>GET /api/settings</c> lists it.</summary>
/// <param name="Route">The route segment its endpoints live under.</param>
/// <param name="Name">The persistence key its rows are stored under.</param>
/// <param name="RequiresAuthorization">
/// Whether the group's class carries <c>[Authorize]</c>. <c>false</c> means its endpoints are
/// anonymous, which is the default for a settings class.
/// </param>
public sealed record SettingGroupDescriptor(string Route, string Name, bool RequiresAuthorization);

/// <summary>One property, as <c>GET /api/settings/{route}/schema</c> describes it.</summary>
/// <param name="Name">
/// The property name as the <c>GET</c> body spells it, so a client can match the two up
/// without guessing at a casing convention.
/// </param>
/// <param name="Type">The CLR type name — the underlying type for a nullable.</param>
/// <param name="Nullable">Whether the property is a nullable value type.</param>
/// <param name="Default">
/// The value a read falls back to when nothing is stored. Omitted for a <c>[Sensitive]</c>
/// property: a compile-time default on a secret is exactly the kind of thing this endpoint
/// must not hand out, given it is meant to be safe to expose more widely than values are.
/// </param>
/// <param name="Sensitive">Whether the property carries <c>[Sensitive]</c>.</param>
/// <param name="Constraints">
/// Validation rules from the registered <c>ISettingSchemaContributor</c>, or <c>null</c> when
/// none is registered or it has nothing to say about this property.
/// </param>
public sealed record SettingPropertyDescriptor(
    string Name,
    string Type,
    bool Nullable,
    object? Default,
    bool Sensitive,
    IReadOnlyDictionary<string, object?>? Constraints);

/// <summary>The shape of one settings group. Never its values.</summary>
/// <param name="Route">The route segment the group's endpoints live under.</param>
/// <param name="Name">The persistence key its rows are stored under.</param>
/// <param name="Properties">The properties the engine reads and writes.</param>
public sealed record SettingGroupSchema(
    string Route,
    string Name,
    IReadOnlyList<SettingPropertyDescriptor> Properties);
