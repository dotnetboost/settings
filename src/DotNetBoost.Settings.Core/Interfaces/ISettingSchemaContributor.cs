namespace DotNetBoost.Settings.Core.Interfaces;

/// <summary>
/// Describes the validation constraints on a settings group's properties, for the schema
/// endpoint to publish.
/// </summary>
/// <remarks>
/// <para>
/// A separate port because <see cref="ISettingValidator"/> cannot answer this: it only offers
/// <c>ValidateAsync</c> and <c>CanValidate</c>, so it can reject a value but has no way to say
/// what it would accept. Without a way to publish rules, every client ends up hardcoding them
/// — and a bound that lives only in a dashboard is not enforced anywhere.
/// </para>
/// <para>
/// Constraints are <c>property name → constraint name → value</c>. Names are conventional
/// rather than closed, so a contributor can describe a rule this library has never heard of;
/// prefer the familiar ones where they fit: <c>required</c>, <c>min</c>, <c>max</c>,
/// <c>exclusiveMin</c>, <c>exclusiveMax</c>, <c>minLength</c>, <c>maxLength</c>,
/// <c>pattern</c>.
/// </para>
/// <para>
/// <b>Several compose.</b> Every registered contributor whose <see cref="CanDescribe"/> returns
/// true is consulted, and their constraints are merged per property — constraints are additive
/// facts about a property, not a decision, so two contributors describing different aspects of
/// one group is the normal case rather than a conflict. Register a second one to publish
/// something this library cannot know about: a rule carried by an attribute from another
/// package, for instance, alongside the validation rules the first contributor reads.
/// </para>
/// <para>
/// On a collision — the same constraint name on the same property from two contributors — the
/// first registered wins and the clash is logged at <c>Warning</c>. Resolving it quietly would
/// make the published schema depend on registration order.
/// </para>
/// <para>
/// Entirely optional. With no contributor registered the schema endpoint simply reports no
/// constraints, and publishing a constraint changes nothing about enforcement — the validator
/// is still what rejects a bad write. A contributor that throws costs its own constraints and
/// not the endpoint, nor the other contributors' constraints.
/// </para>
/// </remarks>
public interface ISettingSchemaContributor
{
    /// <summary>Whether this contributor has anything to say about <paramref name="type"/>.</summary>
    bool CanDescribe(Type type);

    /// <summary>
    /// The constraints on <paramref name="type"/>'s properties, keyed by property name as it
    /// is spelled on the class.
    /// </summary>
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, object?>> Describe(Type type);
}
