using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
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

    /// <summary>
    /// Deliberately unlike a notification, whose exception is logged and swallowed. A failed
    /// notification must not break a write that already committed; a failed projection means
    /// the response would be wrong, and a wrong response must not be hidden.
    /// </summary>
    [Fact]
    public async Task Get_WithAProjectorThatThrows_FailsTheRequest()
    {
        await using var app = await TestApp.StartAsync(
            s => s.AddScoped<ISettingProjector<ProjectedSettings>, ThrowingProjector>());

        // The test host rethrows an unhandled exception rather than turning it into a 500, so
        // either outcome proves the point: the failure is not swallowed.
        var failure = await Record.ExceptionAsync(() => app.Client.GetAsync(Url));

        if (failure is null)
        {
            var response = await app.Client.GetAsync(Url);
            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        }
    }

    // ------------------------------------------------- the declared OpenAPI response type

    /// <summary>
    /// Asserted on endpoint metadata rather than a generated OpenAPI document: AddOpenApi is
    /// .NET 9+, and this suite also runs on net8.0. <c>.Produces</c> is metadata either way.
    /// </summary>
    [Fact]
    public async Task Get_WithATypedProjector_DeclaresTheProjectionAsIts200()
    {
        await using var app = await TestApp.StartAsync(
            configureSettings: b => b.UseProjector<ProjectedSettings, TypedProjector>());

        Assert.Equal(typeof(BrandingView), DeclaredResponseType(app, Url));
    }

    /// <summary>
    /// The regression that matters most: an untyped projector is what every existing consumer
    /// has, and its endpoint must go on declaring the stored group.
    /// </summary>
    [Fact]
    public async Task Get_WithAnUntypedProjector_StillDeclaresTheGroup()
    {
        await using var app = await TestApp.StartAsync(
            configureSettings: b => b.UseProjector<ProjectedSettings, UppercasingProjector>());

        Assert.Equal(typeof(ProjectedSettings), DeclaredResponseType(app, Url));
    }

    [Fact]
    public async Task Get_WithNoProjector_DeclaresTheGroup()
    {
        await using var app = await TestApp.StartAsync();

        Assert.Equal(typeof(ProjectedSettings), DeclaredResponseType(app, Url));
    }

    /// <summary>
    /// Proves the default interface member really dispatches through the endpoint's reflective
    /// call path — the body is the typed projector's output, not the stored group.
    /// </summary>
    [Fact]
    public async Task Get_WithATypedProjector_StillReturnsTheProjectedBody()
    {
        await using var app = await TestApp.StartAsync(
            configureSettings: b => b.UseProjector<ProjectedSettings, TypedProjector>());

        var body = await app.Client.GetFromJsonAsync<JsonElement>(Url);

        Assert.Equal("STORED", body.GetProperty("displayName").GetString());
        Assert.Equal("xx-test", body.GetProperty("language").GetString());
    }

    /// <summary>
    /// POST keeps accepting the stored group. Advertising the projection as a request body
    /// would promise a write the API refuses.
    /// </summary>
    [Fact]
    public async Task Post_StillAcceptsTheGroup_NotTheProjection()
    {
        await using var app = await TestApp.StartAsync(
            configureSettings: b => b.UseProjector<ProjectedSettings, TypedProjector>());

        var accepted = app.Services.GetRequiredService<EndpointDataSource>()
            .Endpoints
            .OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText == "api/settings/api-projected/")
            .Select(e => e.Metadata.GetMetadata<IAcceptsMetadata>())
            .FirstOrDefault(m => m is not null);

        Assert.Equal(typeof(ProjectedSettings), accepted!.RequestType);
    }

    [Fact]
    public void AProjectorDeclaringTwoProjections_ForOneGroup_ThrowsNamingBoth()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            new ServiceCollection().AddSettings()
                .UseProjector<ProjectedSettings, AmbiguousProjector>());

        Assert.Contains(nameof(BrandingView), ex.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(OtherView), ex.Message, StringComparison.Ordinal);
    }

    private static Type? DeclaredResponseType(TestApp app, string url)
    {
        var pattern = url.TrimStart('/') + "/";

        return app.Services.GetRequiredService<EndpointDataSource>()
            .Endpoints
            .OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText == pattern)
            .Where(e => e.Metadata.GetMetadata<IHttpMethodMetadata>()!.HttpMethods.Contains("GET"))
            .SelectMany(e => e.Metadata.OfType<IProducesResponseTypeMetadata>())
            .First(m => m.StatusCode == 200)
            .Type;
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

/// <summary>What a typed projector returns, and what GET must then advertise.</summary>
public sealed record BrandingView(string DisplayName, string Language);

/// <summary>A second view, only so one projector can ambiguously declare two.</summary>
public sealed record OtherView(string DisplayName);

/// <summary>
/// The same projection as <see cref="UppercasingProjector"/>, but named — so the endpoint can
/// declare it without the library having to resolve a scoped projector at map time.
/// </summary>
internal sealed class TypedProjector : ISettingProjector<ProjectedSettings, BrandingView>
{
    public Task<BrandingView> ProjectAsync(ProjectedSettings group, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(group);
        return Task.FromResult(new BrandingView(group.DisplayName.ToUpperInvariant(), "xx-test"));
    }
}

/// <summary>Declares two projections for one group, which has no single answer.</summary>
internal sealed class AmbiguousProjector
    : ISettingProjector<ProjectedSettings, BrandingView>, ISettingProjector<ProjectedSettings, OtherView>
{
    Task<BrandingView> ISettingProjector<ProjectedSettings, BrandingView>.ProjectAsync(
        ProjectedSettings group, CancellationToken ct) => Task.FromResult(new BrandingView("", ""));

    Task<OtherView> ISettingProjector<ProjectedSettings, OtherView>.ProjectAsync(
        ProjectedSettings group, CancellationToken ct) => Task.FromResult(new OtherView(""));

    public Task<object> ProjectAsync(ProjectedSettings group, CancellationToken ct = default)
        => Task.FromResult<object>(group);
}

internal sealed class ThrowingProjector : ISettingProjector<ProjectedSettings>
{
    public Task<object> ProjectAsync(ProjectedSettings group, CancellationToken ct = default)
        => throw new InvalidOperationException("the translator is unavailable");
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
