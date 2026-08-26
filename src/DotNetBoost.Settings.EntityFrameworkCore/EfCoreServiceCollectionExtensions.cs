using DotNetBoost.Settings.Core;
using DotNetBoost.Settings.Core.Interfaces;
using DotNetBoost.Settings.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Microsoft.Extensions.DependencyInjection
{
    /// <summary>Builder methods for the EF Core storage provider.</summary>
    public static class EntityFrameworkBuilderExtensions
    {
        /// <summary>Configures Entity Framework Core as the settings provider.</summary>
        public static SettingBuilder UseEntityFrameworkCore<TContext>(this SettingBuilder builder)
            where TContext : DbContext, ISettingDbContext
        {
            SettingBuilderGuard.EnsureProviderNotConfigured(builder, "EntityFrameworkCore");
            builder.Services.AddScoped<ISettingDbContext>(sp => sp.GetRequiredService<TContext>());
            builder.Services.AddScoped<ISettingStore, EfCoreSettingStore>();
            return builder;
        }
    }
}
