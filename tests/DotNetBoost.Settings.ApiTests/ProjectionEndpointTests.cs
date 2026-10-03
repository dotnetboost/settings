using System.Net.Http.Json;
using System.Text.Json;
using DotNetBoost.Settings.Core.Attributes;
using DotNetBoost.Settings.Core.Interfaces;
using Microsoft.Extensions.DependencyInjection;

namespace DotNetBoost.Settings.ApiTests;

/// <summary>
/// <see cref="ISettingProjector{T}"/> exists so a consumer can serve a group as something
/// other than its stored values — in the request's language, with an id resolved to a URL —
/// without the settings engine learning what any of that is. The constraints are what make
/// it safe to have: read path only, never near the entity tag, and inert when unregistered.
/// </summary>
public class ProjectionEndpointTests
{
    private const string Url = "/api/settings/api-projected";

    [Fact]
    public async Task Get_WithNoProjectorRegistered_ReturnsTheStoredGroup()
    {
        await using var app = await TestApp.StartAsync();

        var body = await app.Client.GetFromJsonAsync<JsonElement>(Url);

        Assert.Equal("stored", body.GetProperty("displayName").GetString());
        Assert.False(body.TryGetProperty("language", out _));
    }

    [Fact]
    public async Task Get_WithAProjectorRegistered_ReturnsTheProjection()
    {
        await using var app = await TestApp.StartAsync(
            s => s.AddScoped<ISettingProjector<ProjectedSettings>, UppercasingProjector>());

        var body = await app.Client.GetFromJsonAsync<JsonElement>(Url);

        Assert.Equal("STORED", body.GetProperty("displayName").GetString());
        Assert.Equal("xx-test", body.GetProperty("language").GetString());
    }

    /// <summary>
    /// The easy way to get this feature wrong. A tag covering projected output would move
    /// with whatever the projector depends on, and <c>If-Match</c> would stop describing the
    /// stored revision it is supposed to protect.
    /// </summary>
    [Fact]
    public async Task Get_ETag_DescribesTheStoredRevision_NotTheProjection()
    {
        await using var app = await TestApp.StartAsync(
            s => s.AddScoped<ISettingProjector<ProjectedSettings>, UppercasingProjector>());

        await app.Client.PostAsJsonAsync(Url, new { DisplayName = "shared" });

        var response = await app.Client.GetAsync(Url);

        // The body is projected...
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("SHARED", body.GetProperty("displayName").GetString());

        // ...and the tag is still exactly what GetVersionAsync computes from the stored rows.
        using var scope = app.Services.CreateScope();
        var stored = await scope.ServiceProvider.GetRequiredService<ISettingManager>()
            .For<ProjectedSettings>().GetVersionAsync();

        Assert.Equal($"\"{stored}\"", response.Headers.ETag!.Tag);
    }

    /// <summary>
    /// A projector must not be able to influence what is stored. POST neither consults it nor
    /// accepts its shape back.
    /// </summary>
    [Fact]
    public async Task Post_DoesNotConsultTheProjector()
    {
        var projector = new CountingProjector();
        await using var app = await TestApp.StartAsync(
            s => s.AddScoped<ISettingProjector<ProjectedSettings>>(_ => projector));

        await app.Client.PostAsJsonAsync(Url, new { DisplayName = "written" });

        Assert.Equal(0, projector.Calls);
    }

    [Fact]
    public async Task Projector_DoesNotAffectProgrammaticReads()
    {
        await using var app = await TestApp.StartAsync(
            s => s.AddScoped<ISettingProjector<ProjectedSettings>, UppercasingProjector>());

        using var scope = app.Services.CreateScope();
        var manager = scope.ServiceProvider.GetRequiredService<ISettingManager>();

        var group = await manager.For<ProjectedSettings>().GetAsync();

        Assert.Equal("stored", group.DisplayName);
    }
}

[SettingGroup("api-projected", Name = "api-projected-group")]
public class ProjectedSettings
{
    public string DisplayName { get; set; } = "stored";
}

/// <summary>Stands in for the real case: a group reshaped with per-request context.</summary>
internal sealed class UppercasingProjector : ISettingProjector<ProjectedSettings>
{
    public Task<object> ProjectAsync(ProjectedSettings group, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(group);
        return Task.FromResult<object>(new
        {
            DisplayName = group.DisplayName.ToUpperInvariant(),
            Language    = "xx-test"
        });
    }
}

internal sealed class CountingProjector : ISettingProjector<ProjectedSettings>
{
    public int Calls { get; private set; }

    public Task<object> ProjectAsync(ProjectedSettings group, CancellationToken ct = default)
    {
        Calls++;
        return Task.FromResult<object>(group);
    }
}
