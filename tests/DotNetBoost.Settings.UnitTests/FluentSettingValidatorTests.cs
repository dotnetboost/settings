using DotNetBoost.Settings.Core.Interfaces;
using DotNetBoost.Settings.FluentValidation;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;

namespace DotNetBoost.Settings.UnitTests;

/// <summary>
/// <see cref="FluentSettingValidator{T}"/> is the whole reason a FluentValidation rule reaches
/// <c>SetAsync</c> and the REST API: Core knows only <see cref="ISettingValidator"/>, which is
/// untyped, and this adapts one to the other. The shape it produces — per-property keys, every
/// message for a property in one array — is what the API serialises into a 400 body, so it is
/// part of the contract rather than an implementation detail.
/// </summary>
public class FluentSettingValidatorTests
{
    [Fact]
    public void CanValidate_IsTrue_OnlyForTheValidatedType()
    {
        var validator = Bridge();

        Assert.True(validator.CanValidate(typeof(BridgeSettings)));
        Assert.False(validator.CanValidate(typeof(OtherSettings)));
    }

    [Fact]
    public async Task ValidateAsync_ReportsAValidModelWithNoErrors()
    {
        var (isValid, errors) = await Bridge().ValidateAsync(new BridgeSettings { Host = "localhost", Port = 25 });

        Assert.True(isValid);
        Assert.Empty(errors);
    }

    /// <summary>
    /// Keys are property names, so a client can attach each message to the field it came from.
    /// </summary>
    [Fact]
    public async Task ValidateAsync_KeysErrorsByPropertyName()
    {
        var (isValid, errors) = await Bridge().ValidateAsync(new BridgeSettings { Host = "localhost", Port = 0 });

        Assert.False(isValid);
        Assert.Equal(nameof(BridgeSettings.Port), Assert.Single(errors).Key);
    }

    /// <summary>
    /// Two rules failing on one property is one key carrying two messages, not the first message
    /// or two entries fighting over the same key — the grouping is what makes that true.
    /// </summary>
    [Fact]
    public async Task ValidateAsync_GroupsEveryMessageForOnePropertyIntoOneEntry()
    {
        var (isValid, errors) = await Bridge().ValidateAsync(new BridgeSettings { Host = string.Empty, Port = 25 });

        Assert.False(isValid);
        Assert.Equal(2, errors[nameof(BridgeSettings.Host)].Length);
    }

    /// <summary>
    /// The untyped interface makes this reachable: Core picks a validator by
    /// <see cref="ISettingValidator.CanValidate"/> and then hands it an <c>object</c>. Casting
    /// blindly would throw from inside FluentValidation with nothing naming the real problem, so
    /// the mismatch is reported as a validation failure that says which type was expected.
    /// </summary>
    [Fact]
    public async Task ValidateAsync_ReportsATypeMismatchInsteadOfThrowing()
    {
        var (isValid, errors) = await Bridge().ValidateAsync(new OtherSettings());

        Assert.False(isValid);
        var message = Assert.Single(errors["TypeMismatch"]);
        Assert.Contains(nameof(BridgeSettings), message, StringComparison.Ordinal);
        Assert.Contains(nameof(OtherSettings), message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The standalone counterpart to <c>UseFluentValidation</c>, for an application wiring
    /// services up without the builder. It has to register three things to be useful: the
    /// validator itself, the bridge that exposes it to Core, and the schema contributor.
    /// </summary>
    [Fact]
    public void AddFluentValidationSettings_RegistersTheValidatorTheBridgeAndTheContributor()
    {
        var services = new ServiceCollection();

        services.AddFluentValidationSettings(typeof(BridgeSettingsValidator).Assembly);

        var provider = services.BuildServiceProvider();

        Assert.NotNull(provider.GetService<IValidator<BridgeSettings>>());
        Assert.Contains(provider.GetServices<ISettingValidator>(), v => v.CanValidate(typeof(BridgeSettings)));
        Assert.IsType<FluentValidationSchemaContributor>(
            Assert.Single(provider.GetServices<ISettingSchemaContributor>()));
    }

    [Fact]
    public void AddFluentValidationSettings_RejectsNullArguments()
    {
        var assembly = typeof(BridgeSettingsValidator).Assembly;

        Assert.Throws<ArgumentNullException>(() => ((IServiceCollection)null!).AddFluentValidationSettings(assembly));
        Assert.Throws<ArgumentNullException>(() => new ServiceCollection().AddFluentValidationSettings(null!));
    }

    private static FluentSettingValidator<BridgeSettings> Bridge() => new(new BridgeSettingsValidator());

    public class BridgeSettings
    {
        public string Host { get; set; } = string.Empty;
        public int Port { get; set; }
    }

    public class OtherSettings;

    public class BridgeSettingsValidator : AbstractValidator<BridgeSettings>
    {
        public BridgeSettingsValidator()
        {
            RuleFor(x => x.Host).NotEmpty().MinimumLength(3);
            RuleFor(x => x.Port).InclusiveBetween(1, 65_535);
        }
    }
}
