using System.Net;
using DotNetBoost.Settings.Core.Attributes;
using DotNetBoost.Settings.Core.Interfaces;
using Microsoft.Extensions.DependencyInjection;

namespace DotNetBoost.Settings.ApiTests;

public class AuditEndpointTests
{
    /// <summary>
    /// ISettingAuditStore is optional. Bound as a handler parameter it was inferred as a
    /// *body* parameter when unregistered, which throws while the endpoint is being built
    /// and takes the whole application down at startup — long before any request arrives.
    /// </summary>
    [Fact]
    public async Task Application_Starts_WhenNoAuditStoreIsRegistered()
    {
        await using var app = await TestApp.StartAsync();

        var response = await app.Client.GetAsync("/api/settings/api-test");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task AuditEndpoint_Returns404_WhenNoAuditStoreIsRegistered()
    {
        await using var app = await TestApp.StartAsync();

        var response = await app.Client.GetAsync("/api/settings/api-test/audit");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("Audit store is not configured", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AuditEndpoint_ReturnsHistory_WhenAuditStoreIsRegistered()
    {
        var audit = new RecordingAuditStore();
        await using var app = await TestApp.StartAsync(s => s.AddScoped<ISettingAuditStore>(_ => audit));

        var response = await app.Client.GetAsync("/api/settings/api-test/audit");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("after", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    /// <summary>
    /// UseAuditStore&lt;T&gt;() registers the store as scoped, so the endpoint must resolve it
    /// from the request scope. Resolving from the root provider would throw at runtime.
    /// </summary>
    [Fact]
    public async Task AuditEndpoint_ResolvesScopedAuditStore()
    {
        await using var app = await TestApp.StartAsync(s => s.AddScoped<ISettingAuditStore, RecordingAuditStore>());

        var response = await app.Client.GetAsync("/api/settings/api-test/audit");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task AuditEndpoint_QueriesTheResolvedGroupName_NotTheClassName()
    {
        var audit = new RecordingAuditStore();
        await using var app = await TestApp.StartAsync(s => s.AddScoped<ISettingAuditStore>(_ => audit));

        await app.Client.GetAsync("/api/settings/api-test/audit");

        var (group, _) = Assert.Single(audit.Queries);
        Assert.Equal("api-test-group", group);
        Assert.NotEqual(nameof(ApiTestSettings), group);
    }

    [Fact]
    public async Task AuditEndpoint_ForwardsTheKeyFilter()
    {
        var audit = new RecordingAuditStore();
        await using var app = await TestApp.StartAsync(s => s.AddScoped<ISettingAuditStore>(_ => audit));

        await app.Client.GetAsync("/api/settings/api-test/audit?key=Host");

        Assert.Equal("Host", Assert.Single(audit.Queries).Key);
    }
}

[SettingGroup("api-test", Name = "api-test-group")]
public class ApiTestSettings
{
    public string Host { get; set; } = string.Empty;
}
