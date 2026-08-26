using DotNetBoost.Settings.Core;
using DotNetBoost.Settings.Core.Interfaces;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using System.Reflection;

namespace DotNetBoost.Settings.FluentValidation;

/// <summary>
/// Bridges a FluentValidation <see cref="IValidator{T}"/> to the
/// <see cref="ISettingValidator"/> abstraction used by <c>SetAsync</c> and the REST API.
/// </summary>
public sealed class FluentSettingValidator<T>(IValidator<T> validator) : ISettingValidator
    where T : class
{
    /// <inheritdoc/>
    public bool CanValidate(Type type) => type == typeof(T);

    /// <inheritdoc/>
    public async Task<(bool IsValid, IDictionary<string, string[]> Errors)> ValidateAsync(object model)
    {
        if (model is not T typed)
        {
            return (false, new Dictionary<string, string[]>
            {
                { "TypeMismatch", [$"Expected {typeof(T).Name} but received {model.GetType().Name}."] }
            });
        }

        var result = await validator.ValidateAsync(typed).ConfigureAwait(false);
        var errors = result.Errors
            .GroupBy(e => e.PropertyName)
            .ToDictionary(g => g.Key, g => g.Select(e => e.ErrorMessage).ToArray());

        return (result.IsValid, errors);
    }
}
