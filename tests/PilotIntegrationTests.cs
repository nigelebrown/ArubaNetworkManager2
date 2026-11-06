using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Xunit;

namespace Jcf.NetworkManager.Tests;

public sealed class PilotIntegrationTests : IClassFixture<PilotIntegrationTests.AppFactory>
{
    private readonly AppFactory factory;
    public PilotIntegrationTests(AppFactory factory) => this.factory=factory;

    public sealed class AppFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("Jcf:DemoMode","true");
            builder.UseSetting("Jcf:Polling:Enabled", "false");
            builder.UseSetting("Jcf:EnableDeviceWrites","false");
            builder.UseEnvironment("Development");
        }
    }

    [Fact]
    public async Task Enterprise_dashboard_serves_simulated_inventory_without_live_writes()
    {
        using var client=factory.CreateClient();
        var response=await client.GetAsync("/api/v1/dashboard/summary");
        Assert.Equal(HttpStatusCode.OK,response.StatusCode);
        var body=await response.Content.ReadAsStringAsync();
        Assert.Contains("pendingRecommendations",body);
        Assert.Contains("devices",body);
    }

    [Fact]
    public async Task New_operations_and_topology_pages_render()
    {
        using var client=factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK,(await client.GetAsync("/operations")).StatusCode);
        Assert.Equal(HttpStatusCode.OK,(await client.GetAsync("/topology")).StatusCode);
    }

    [Fact]
    public async Task Health_endpoint_reports_demo()
    {
        using var client=factory.CreateClient();
        var response=await client.GetAsync("/healthz");
        Assert.Equal(HttpStatusCode.OK,response.StatusCode);
        var data=await response.Content.ReadAsStringAsync();
        Assert.Contains("demo",data,StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Nested_routes_load_the_blazor_script_from_the_site_root()
    {
        // 0.8.1 used a relative script path without <base href>: /admin/users requested /admin/_framework/blazor.web.js (404)
        // and the administration page never became interactive.
        using var client=factory.CreateClient();
        client.DefaultRequestHeaders.Add("Cookie","jcf_demo_persona=admin");
        var html=await client.GetStringAsync("/admin/users");
        Assert.Contains("<base href=\"/\"",html);
        Assert.Contains("src=\"/_framework/blazor.web.js\"",html);
        Assert.Equal(System.Net.HttpStatusCode.OK,(await client.GetAsync("/_framework/blazor.web.js")).StatusCode);
    }

    [Fact]
    public async Task Device_inventory_returns_simulated_devices()
    {
        using var client=factory.CreateClient();
        var data=await client.GetStringAsync("/api/v1/devices");
        Assert.Contains("Sample AP-505",data);
        Assert.Contains("Sample CX 6100",data);
        Assert.Contains("\"isSimulated\":true",data);
        Assert.Contains("192.0.2.",data);   // sample devices never occupy real management addresses
    }

    [Fact]
    public async Task Device_execution_is_disabled_even_when_called_as_authenticated_demo_operator()
    {
        using var client=factory.CreateClient();
        var response=await client.PostAsync("/api/v1/execution-jobs",new StringContent("{}"));
        Assert.Equal(HttpStatusCode.NotImplemented,response.StatusCode);
    }

    [Fact]
    public async Task Approved_width_policy_is_visible_and_read_only()
    {
        using var client=factory.CreateClient();
        var data=await client.GetStringAsync("/api/v1/radio-policy");
        Assert.Contains("20",data);Assert.Contains("40",data);
        Assert.Contains("52",data);Assert.Contains("144",data);
    }

    [Fact]
    public async Task Blazor_navigation_pages_are_served()
    {
        using var client=factory.CreateClient();
        foreach(var path in new[]{"/","/devices","/locations","/wireless","/switches","/recommendations","/audit","/configuration","/changes","/groups","/capabilities","/alerts","/client-experience","/kiosk"})
        {
            var response=await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.OK,response.StatusCode);
        }
    }
}
