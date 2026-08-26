using DotNetBoost.Settings.Core.Attributes;
using DotNetBoost.Settings.Core.Interfaces;
using DotNetBoost.Settings.Core.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System.Globalization;
using System.Reflection;
using System.Text;

namespace DotNetBoost.Settings.Core;

/// <summary>Fluent builder returned by <c>services.AddSettings()</c>.</summary>
public sealed class SettingBuilder
{
    /// <summary>The service collection being configured. Providers add their registrations here.</summary>
    public IServiceCollection Services { get; }

    internal bool    ProviderConfigured { get; set; }
    internal string? ProviderName       { get; set; }

    /// <summary>
    /// The options instance handed to <c>SettingManager</c> through DI. Builder methods mutate
    /// this object rather than a private copy, so configuration applied anywhere in the chain
    /// reaches the manager.
    /// </summary>
    internal SettingOptions Options { get; }

    internal TimeSpan CacheDuration
    {
        get => Options.CacheDuration;
        set => Options.CacheDuration = value;
    }

    /// <summary>Creates a builder over <paramref name="services"/> and registers its options.</summary>
    public SettingBuilder(IServiceCollection services)
    {
        Services = services ?? throw new ArgumentNullException(nameof(services));
        Options  = new SettingOptions();
        Services.AddSingleton(Options);
    }

    /// <summary>
    /// Validates the builder configuration and returns the service collection.
    /// Call once at the end of the configuration chain.
    /// </summary>
    public IServiceCollection Build()
    {
        if (!ProviderConfigured)
        {
            throw new InvalidOperationException(
                "No settings provider configured. " +
                "Call UseEntityFrameworkCore(), UseDapper(), or UseMongoDb() first.");
        }

        SettingBuilderValidator.Validate(AppDomain.CurrentDomain.GetAssemblies());
        return Services;
    }
}

/// <summary>Guards against configuring more than one storage provider on a builder.</summary>
public static class SettingBuilderGuard
{
    /// <summary>
    /// Records <paramref name="providerName"/> as the builder's provider.
    /// </summary>
    /// <exception cref="InvalidOperationException">A provider is already configured.</exception>
    public static void EnsureProviderNotConfigured(SettingBuilder builder, string providerName)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerName);

        if (builder.ProviderConfigured)
        {
            throw new InvalidOperationException(
                $"A settings provider ('{builder.ProviderName}') is already configured. " +
                $"Cannot also configure '{providerName}'.");
        }

        builder.ProviderConfigured = true;
        builder.ProviderName       = providerName;
    }
}

/// <summary>
/// Startup validation for <c>[SettingGroup]</c> classes: blank routes, blank explicit names,
/// and duplicate group names or routes. Run by <see cref="SettingBuilder.Build"/>.
/// </summary>
public static class SettingBuilderValidator
{
    /// <summary>Validates every <c>[SettingGroup]</c> class found in <paramref name="assemblies"/>.</summary>
    /// <exception cref="InvalidOperationException">A rule was violated; the message lists offenders.</exception>
    public static void Validate(IEnumerable<Assembly> assemblies)
    {
        ArgumentNullException.ThrowIfNull(assemblies);
        var types = assemblies.SelectMany(SafeTypes)
            .Select(t => (Type: t, Attr: t.GetCustomAttribute<SettingGroupAttribute>()))
            .Where(x => x.Attr is not null).ToList();
        Run(types!);
    }

    /// <summary>Validates the <c>[SettingGroup]</c> classes among <paramref name="types"/>.</summary>
    /// <exception cref="InvalidOperationException">A rule was violated; the message lists offenders.</exception>
    public static void Validate(IEnumerable<Type> types)
    {
        ArgumentNullException.ThrowIfNull(types);
        var tagged = types
            .Select(t => (Type: t, Attr: t.GetCustomAttribute<SettingGroupAttribute>()))
            .Where(x => x.Attr is not null).ToList();
        Run(tagged!);
    }

    private static void Run(List<(Type Type, SettingGroupAttribute Attr)> list)
    {
        CheckEmptyRoutes(list);
        CheckBlankGroupNames(list);
        CheckDuplicateGroupNames(list);
        CheckDuplicateRoutes(list);
    }

    /// <summary>
    /// An explicitly supplied but blank <c>Name</c> silently falls back to the class name,
    /// which is never what the author meant. Fail loudly instead.
    /// </summary>
    private static void CheckBlankGroupNames(List<(Type Type, SettingGroupAttribute Attr)> list)
    {
        var bad = list.Where(x => x.Attr.Name is not null && string.IsNullOrWhiteSpace(x.Attr.Name)).ToList();
        if (bad.Count == 0) return;
        var sb = new StringBuilder("Blank SettingGroup Name values (omit Name to use the class name):");
        foreach (var x in bad) sb.AppendLine().Append(CultureInfo.InvariantCulture, $"  {x.Type.FullName}");
        throw new InvalidOperationException(sb.ToString());
    }

    private static void CheckEmptyRoutes(List<(Type Type, SettingGroupAttribute Attr)> list)
    {
        var bad = list.Where(x => string.IsNullOrWhiteSpace(x.Attr.Route)).ToList();
        if (bad.Count == 0) return;
        var sb = new StringBuilder("Empty SettingGroup route values:");
        foreach (var x in bad) sb.AppendLine().Append(CultureInfo.InvariantCulture, $"  {x.Type.FullName}");
        throw new InvalidOperationException(sb.ToString());
    }

    /// <summary>
    /// Two groups resolving to the same persistence key would read and write each other's
    /// rows. Compared on the resolved name, so same-named classes in different namespaces
    /// still collide unless one of them sets an explicit <c>Name</c>.
    /// </summary>
    private static void CheckDuplicateGroupNames(List<(Type Type, SettingGroupAttribute Attr)> list)
    {
        var dups = list.GroupBy(x => SettingGroupAttribute.ResolveName(x.Type), StringComparer.OrdinalIgnoreCase)
                       .Where(g => g.Count() > 1).ToList();
        if (dups.Count == 0) return;
        var sb = new StringBuilder(
            "Duplicate settings group names detected (set a distinct SettingGroup Name to separate them):");
        foreach (var g in dups) sb.AppendLine().Append(CultureInfo.InvariantCulture, $"  '{g.Key}': {string.Join(", ", g.Select(x => x.Type.FullName))}");
        throw new InvalidOperationException(sb.ToString());
    }

    private static void CheckDuplicateRoutes(List<(Type Type, SettingGroupAttribute Attr)> list)
    {
        var dups = list.GroupBy(x => x.Attr.Route, StringComparer.OrdinalIgnoreCase)
                       .Where(g => g.Count() > 1).ToList();
        if (dups.Count == 0) return;
        var sb = new StringBuilder("Duplicate SettingGroup route names detected:");
        foreach (var g in dups) sb.AppendLine().Append(CultureInfo.InvariantCulture, $"  Route '{g.Key}': {string.Join(", ", g.Select(x => x.Type.FullName))}");
        throw new InvalidOperationException(sb.ToString());
    }

    private static IEnumerable<Type> SafeTypes(Assembly a)
    {
        try { return a.GetTypes(); }
        catch (ReflectionTypeLoadException ex) { return ex.Types.Where(t => t is not null)!; }
    }
}
