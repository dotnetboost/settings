using DotNetBoost.Settings.Core.Interfaces;
using FluentValidation;
using FluentValidation.Validators;

namespace DotNetBoost.Settings.FluentValidation;

/// <summary>
/// Publishes the rules of a registered FluentValidation validator through
/// <see cref="ISettingSchemaContributor"/>, so a generated form can enforce the same bounds
/// the server does instead of hardcoding its own.
/// </summary>
/// <remarks>
/// Reads FluentValidation's own <c>IValidatorDescriptor</c>. Rules it does not recognise are
/// skipped rather than guessed at: a wrong published bound is worse than a missing one,
/// because a client would believe it. A rule expressed as <c>Must(...)</c> is opaque by
/// nature and will never appear here.
/// </remarks>
/// <param name="serviceProvider">Resolves the <c>IValidator&lt;T&gt;</c> for a settings type.</param>
public sealed class FluentValidationSchemaContributor(IServiceProvider serviceProvider)
    : ISettingSchemaContributor
{
    /// <inheritdoc/>
    public bool CanDescribe(Type type) => Resolve(type) is not null;

    /// <inheritdoc/>
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, object?>> Describe(Type type)
    {
        var validator = Resolve(type);
        if (validator is null) return new Dictionary<string, IReadOnlyDictionary<string, object?>>();

        var result = new Dictionary<string, IReadOnlyDictionary<string, object?>>(StringComparer.Ordinal);

        foreach (var member in validator.CreateDescriptor().GetMembersWithValidators())
        {
            var constraints = new Dictionary<string, object?>(StringComparer.Ordinal);

            foreach (var (propertyValidator, _) in member)
                Collect(propertyValidator, constraints);

            if (constraints.Count > 0) result[member.Key] = constraints;
        }

        return result;
    }

    private IValidator? Resolve(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return serviceProvider.GetService(typeof(IValidator<>).MakeGenericType(type)) as IValidator;
    }

    private static void Collect(IPropertyValidator validator, Dictionary<string, object?> into)
    {
        switch (validator)
        {
            case IBetweenValidator between:
                into["min"] = between.From;
                into["max"] = between.To;
                break;

            // Order matters: the exact/minimum/maximum validators all implement
            // ILengthValidator, and an exact length is both bounds at once.
            case IExactLengthValidator exact:
                into["minLength"] = exact.Min;
                into["maxLength"] = exact.Max;
                break;

            case IMaximumLengthValidator maximum:
                into["maxLength"] = maximum.Max;
                break;

            case IMinimumLengthValidator minimum:
                into["minLength"] = minimum.Min;
                break;

            case ILengthValidator length:
                into["minLength"] = length.Min;
                if (length.Max > 0) into["maxLength"] = length.Max;
                break;

            case IRegularExpressionValidator regex:
                into["pattern"] = regex.Expression;
                break;

            case INotEmptyValidator or INotNullValidator:
                into["required"] = true;
                break;

            case IComparisonValidator comparison:
                CollectComparison(comparison, into);
                break;

            default:
                // Deliberately silent. A Must(...) or a custom validator cannot be described,
                // and inventing a bound for it would publish a lie.
                break;
        }
    }

    private static void CollectComparison(IComparisonValidator comparison, Dictionary<string, object?> into)
    {
        // Only a literal comparison can be published: ValueToCompare is null when the rule
        // compares against another property or a lambda, and there is nothing to report then.
        if (comparison.ValueToCompare is not { } value) return;

        switch (comparison.Comparison)
        {
            case Comparison.GreaterThanOrEqual: into["min"] = value; break;
            case Comparison.LessThanOrEqual:    into["max"] = value; break;
            case Comparison.GreaterThan:        into["exclusiveMin"] = value; break;
            case Comparison.LessThan:           into["exclusiveMax"] = value; break;
            default: break;   // Equal / NotEqual are not bounds.
        }
    }
}
