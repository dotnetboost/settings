using DotNetBoost.Settings.Core.Interfaces;
using DotNetBoost.Settings.FluentValidation;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;

namespace DotNetBoost.Settings.UnitTests;

/// <summary>
/// The constraints a generated form needs are the valuable half of a schema, and
/// <see cref="ISettingValidator"/> cannot supply them — it answers only "is this valid",
/// never "what would be". This reads them out of FluentValidation's own descriptor.
/// </summary>
public class FluentValidationSchemaContributorTests
{
    [Fact]
    public void Describe_PublishesInclusiveBetweenAsMinAndMax()
    {
        var constraints = Describe()[nameof(FormSettings.Port)];

        Assert.Equal(1, constraints["min"]);
        Assert.Equal(65_535, constraints["max"]);
    }

    [Fact]
    public void Describe_PublishesLengthBounds()
    {
        var constraints = Describe()[nameof(FormSettings.Host)];

        Assert.Equal(3, constraints["minLength"]);
        Assert.Equal(255, constraints["maxLength"]);
    }

    [Fact]
    public void Describe_PublishesNotEmptyAsRequired()
        => Assert.Equal(true, Describe()[nameof(FormSettings.Host)]["required"]);

    [Fact]
    public void Describe_PublishesComparisonsAgainstALiteral()
    {
        var retries = Describe()[nameof(FormSettings.Retries)];

        Assert.Equal(0, retries["min"]);
        Assert.Equal(10, retries["exclusiveMax"]);
    }

    [Fact]
    public void Describe_PublishesARegularExpression()
        => Assert.Equal("^[a-z]+$", Describe()[nameof(FormSettings.Code)]["pattern"]);

    /// <summary>
    /// A <c>Must(...)</c> is opaque by nature. Inventing a bound for it would publish a lie
    /// that a client would then believe, so it is skipped and the property simply carries no
    /// constraints.
    /// </summary>
    [Fact]
    public void Describe_SkipsRulesItCannotRead()
        => Assert.DoesNotContain(nameof(FormSettings.Opaque), Describe().Keys, StringComparer.Ordinal);

    [Fact]
    public void CanDescribe_IsFalse_ForATypeWithNoValidator()
    {
        var contributor = new FluentValidationSchemaContributor(Provider());

        Assert.False(contributor.CanDescribe(typeof(UnvalidatedSettings)));
        Assert.Empty(contributor.Describe(typeof(UnvalidatedSettings)));
    }

    [Fact]
    public void UseFluentValidation_RegistersTheContributor()
    {
        var services = new ServiceCollection();
        services.AddSettings().UseFluentValidation(typeof(FormSettingsValidator).Assembly);

        var contributor = services.BuildServiceProvider().GetServices<ISettingSchemaContributor>().Single();

        Assert.IsType<FluentValidationSchemaContributor>(contributor);
    }

    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, object?>> Describe()
        => new FluentValidationSchemaContributor(Provider()).Describe(typeof(FormSettings));

    private static ServiceProvider Provider()
    {
        var services = new ServiceCollection();
        services.AddTransient<IValidator<FormSettings>, FormSettingsValidator>();
        return services.BuildServiceProvider();
    }

    public class FormSettings
    {
        public string Host { get; set; } = string.Empty;
        public int Port { get; set; }
        public int Retries { get; set; }
        public string Code { get; set; } = string.Empty;
        public string Opaque { get; set; } = string.Empty;
    }

    public class UnvalidatedSettings
    {
        public string Host { get; set; } = string.Empty;
    }

    public class FormSettingsValidator : AbstractValidator<FormSettings>
    {
        public FormSettingsValidator()
        {
            RuleFor(x => x.Host).NotEmpty().Length(3, 255);
            RuleFor(x => x.Port).InclusiveBetween(1, 65_535);
            RuleFor(x => x.Retries).GreaterThanOrEqualTo(0).LessThan(10);
            RuleFor(x => x.Code).Matches("^[a-z]+$");
            RuleFor(x => x.Opaque).Must(v => v.Length % 2 == 0);
        }
    }
}
