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

    /// <summary>
    /// An exact length is both bounds at once. It matters that this is read as exact rather than
    /// falling through to the general length rule, which would publish only a minimum and let a
    /// client accept a value the server rejects.
    /// </summary>
    [Fact]
    public void Describe_PublishesAnExactLengthAsBothBounds()
    {
        var constraints = Describe()[nameof(FormSettings.Region)];

        Assert.Equal(2, constraints["minLength"]);
        Assert.Equal(2, constraints["maxLength"]);
    }

    /// <summary>
    /// A one-sided length rule publishes the bound it states and stays silent on the other, so a
    /// client does not infer a floor of zero as a real constraint.
    /// </summary>
    [Fact]
    public void Describe_PublishesAMaximumLengthAlone()
    {
        var constraints = Describe()[nameof(FormSettings.Label)];

        Assert.Equal(50, constraints["maxLength"]);
        Assert.DoesNotContain("minLength", constraints.Keys, StringComparer.Ordinal);
    }

    [Fact]
    public void Describe_PublishesAMinimumLengthAlone()
    {
        var constraints = Describe()[nameof(FormSettings.Slug)];

        Assert.Equal(4, constraints["minLength"]);
        Assert.DoesNotContain("maxLength", constraints.Keys, StringComparer.Ordinal);
    }

    /// <summary>
    /// The four comparisons split along inclusive/exclusive rather than collapsing into min/max:
    /// a client that renders <c>exclusiveMax</c> as <c>max</c> would allow the boundary value the
    /// server refuses.
    /// </summary>
    [Fact]
    public void Describe_DistinguishesInclusiveFromExclusiveComparisons()
    {
        var schema = Describe();

        Assert.Equal(32, schema[nameof(FormSettings.Workers)]["max"]);
        Assert.Equal(0, schema[nameof(FormSettings.Timeout)]["exclusiveMin"]);
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

    /// <summary>
    /// <c>Single()</c> is about this one registration being idempotent — one contributor covers
    /// every settings type, so <c>UseFluentValidation</c> registers it once however many
    /// validators it finds, and twice over is still once. It says nothing about how many
    /// contributors an application may have: the schema endpoint merges every applicable one.
    /// </summary>
    [Fact]
    public void UseFluentValidation_RegistersExactlyOneContributor_HoweverOftenItIsCalled()
    {
        var services = new ServiceCollection();
        var assembly = typeof(FormSettingsValidator).Assembly;

        services.AddSettings()
            .UseFluentValidation(assembly)
            .UseFluentValidation(assembly);

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
        public string Region { get; set; } = string.Empty;
        public string Label { get; set; } = string.Empty;
        public string Slug { get; set; } = string.Empty;
        public int Workers { get; set; }
        public int Timeout { get; set; }
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
            RuleFor(x => x.Region).Length(2);
            RuleFor(x => x.Label).MaximumLength(50);
            RuleFor(x => x.Slug).MinimumLength(4);
            RuleFor(x => x.Workers).LessThanOrEqualTo(32);
            RuleFor(x => x.Timeout).GreaterThan(0);
        }
    }
}
