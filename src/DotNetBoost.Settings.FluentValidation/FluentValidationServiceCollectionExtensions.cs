using DotNetBoost.Settings.Core.Interfaces;
using DotNetBoost.Settings.Core;
using DotNetBoost.Settings.FluentValidation;
using FluentValidation;
using System.Reflection;

namespace Microsoft.Extensions.DependencyInjection
{
    /// <summary>Builder methods for FluentValidation integration.</summary>
    public static class ValidationBuilderExtensions
    {
        /// <summary>
        /// Scans <paramref name="assembly"/> for all <c>AbstractValidator&lt;T&gt;</c>
        /// implementations and registers them as <see cref="ISettingValidator"/>.
        /// </summary>
        public static SettingBuilder UseFluentValidation(this SettingBuilder builder, Assembly assembly)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentNullException.ThrowIfNull(assembly);
            RegisterValidators(builder.Services, assembly);
            return builder;
        }

        /// <summary>Standalone registration helper (without the builder pattern).</summary>
        public static IServiceCollection AddFluentValidationSettings(this IServiceCollection services, Assembly assembly)
        {
            ArgumentNullException.ThrowIfNull(services);
            ArgumentNullException.ThrowIfNull(assembly);
            RegisterValidators(services, assembly);
            return services;
        }

        private static void RegisterValidators(IServiceCollection services, Assembly assembly)
        {
            var pairs = assembly.GetTypes()
                .Where(t => !t.IsAbstract && !t.IsInterface)
                .Select(t => new
                {
                    ValidatorType = t,
                    ServiceType   = t.GetInterfaces()
                        .FirstOrDefault(i => i.IsGenericType &&
                                             i.GetGenericTypeDefinition() == typeof(IValidator<>))
                })
                .Where(x => x.ServiceType is not null)
                .ToList();

            foreach (var item in pairs)
            {
                var settingsType = item.ServiceType!.GetGenericArguments()[0];
                var bridgeType   = typeof(FluentSettingValidator<>).MakeGenericType(settingsType);

                if (!services.Any(d => d.ServiceType == item.ServiceType))
                    services.AddTransient(item.ServiceType, item.ValidatorType);

                services.AddTransient(typeof(ISettingValidator), bridgeType);
            }
        }
    }
}
