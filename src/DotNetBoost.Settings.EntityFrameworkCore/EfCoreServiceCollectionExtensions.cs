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
        /// <c>ApplySettings</c> in <c>OnModelCreating</c>. It needs no interface
        /// and no <c>DbSet</c> properties: the store reads its entities through
        /// <see cref="DbContext.Set{TEntity}()"/>.
        /// </para>
        /// </summary>
        /// <remarks>
        /// Registers nothing against <see cref="DbContext"/> itself. The store is constructed
        /// from <typeparamref name="TContext"/> explicitly, so the library never claims a
        /// framework type it does not own — which in an application with two contexts would
        /// silently hand a bare <c>DbContext</c> dependency whichever one Settings was
        /// pointed at.
        /// </remarks>
        public static SettingBuilder UseEntityFrameworkCore<TContext>(this SettingBuilder builder)
            where TContext : DbContext
        {
            SettingBuilderGuard.EnsureProviderNotConfigured(builder, "EntityFrameworkCore");
            builder.Services.AddScoped<ISettingStore>(
                sp => new EfCoreSettingStore(sp.GetRequiredService<TContext>()));
            return builder;
        }
    }
}
