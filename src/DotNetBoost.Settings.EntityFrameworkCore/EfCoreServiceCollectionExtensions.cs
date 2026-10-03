using DotNetBoost.Settings.Core;
using DotNetBoost.Settings.Core.Interfaces;
using DotNetBoost.Settings.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Microsoft.Extensions.DependencyInjection
{
    /// <summary>Builder methods for the EF Core storage provider.</summary>
    public static class EntityFrameworkBuilderExtensions
    {
        /// <summary>
        /// Configures Entity Framework Core as the settings provider.
        /// <para>
        /// <typeparamref name="TContext"/> only has to be a <see cref="DbContext"/> that calls
        /// <c>ApplySettingsConfiguration</c> in <c>OnModelCreating</c>. It needs no interface
        /// and no <c>DbSet</c> properties: the store reads its entities through
        /// <see cref="DbContext.Set{TEntity}()"/>.
        /// </para>
        /// </summary>
        /// <remarks>
        /// Also registers <typeparamref name="TContext"/> as the scoped <see cref="DbContext"/>,
        /// which is what lets <c>.UseAuditStore&lt;EfCoreAuditStore&gt;()</c> resolve. In an
        /// application with several contexts this makes <typeparamref name="TContext"/> the one
        /// a bare <c>DbContext</c> dependency gets; the settings store itself always uses
        /// <typeparamref name="TContext"/> regardless.
        /// </remarks>
        public static SettingBuilder UseEntityFrameworkCore<TContext>(this SettingBuilder builder)
            where TContext : DbContext
        {
            SettingBuilderGuard.EnsureProviderNotConfigured(builder, "EntityFrameworkCore");
            builder.Services.AddScoped<DbContext>(sp => sp.GetRequiredService<TContext>());
            builder.Services.AddScoped<ISettingStore>(
                sp => new EfCoreSettingStore(sp.GetRequiredService<TContext>()));
            return builder;
        }
    }
}
