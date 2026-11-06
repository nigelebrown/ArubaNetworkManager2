using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Jcf.NetworkManager.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Jcf.NetworkManager.Connectors;
using Jcf.NetworkManager.Workflow;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Jcf.NetworkManager.Tests;

/// <summary>Regression tests for defects found in the 0.8.1 end-to-end run.</summary>
public sealed class ReleaseHardeningTests
{
    [Theory]
    [InlineData("10.0.0.0/8", true)]
    [InlineData("10.250.2.71/32", true)]
    [InlineData("192.168.0.0/16", true)]
    [InlineData("10.0.0.0/33", false)]
    [InlineData("10.0.0/8", false)]
    [InlineData("10.0.0.0", false)]
    [InlineData("fd00::/8", false)]
    [InlineData("", false)]
    public void Management_cidr_syntax_is_validated(string cidr, bool valid) =>
        Assert.Equal(valid, AddressPolicy.IsValidCidr(cidr));

    sealed class BadCidrFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("Jcf:DemoMode", "true");
            builder.UseSetting("Jcf:Polling:Enabled", "false");
            builder.UseSetting("Jcf:DemoDatabaseName", "bad-cidr-" + Guid.NewGuid());
            builder.UseSetting("Jcf:AllowedManagementCidrs:0", "10.0.0.0/33");
        }
    }

    [Fact]
    public void App_refuses_to_start_with_a_malformed_management_allowlist()
    {
        using var factory = new BadCidrFactory();
        var ex = Assert.ThrowsAny<Exception>(() => factory.CreateClient());
        Assert.Contains("Invalid entries in Jcf:AllowedManagementCidrs", ex.ToString());
    }

    sealed class DemoFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("Jcf:DemoMode", "true");
            builder.UseSetting("Jcf:Polling:Enabled", "false");
            builder.UseSetting("Jcf:DemoDatabaseName", "hardening-" + Guid.NewGuid());
        }
    }

    [Fact]
    public async Task Health_version_matches_the_assembly_and_only_one_CSP_header_is_sent()
    {
        using var factory = new DemoFactory();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var health = await client.GetFromJsonAsync<JsonElement>("/healthz");
        Assert.Equal(AppInfo.Version, health.GetProperty("version").GetString());
        Assert.Matches(@"^\d+\.\d+\.\d+", AppInfo.Version);

        var page = await client.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var csp = page.Headers.GetValues("Content-Security-Policy").ToList();
        Assert.Single(csp);
        Assert.Contains("frame-ancestors 'none'", csp[0]);
        Assert.Contains(AppInfo.Version, await page.Content.ReadAsStringAsync()); // sidebar shows the real release
    }

    [Fact]
    public void Production_container_resolves_the_real_Aruba_adapters()
    {
        using var factory = new ProductionSecurityEndToEndTests.ProductionFactory();
        _ = factory.CreateClient();
        using var scope = factory.Services.CreateScope();
        var connectors = scope.ServiceProvider.GetServices<IDeviceConnector>().Select(c => c.GetType().Name).OrderBy(n => n).ToArray();
        // 0.9: the simulator is always registered but only serves IsSimulated sample devices (see DeviceIntegrationTests).
        Assert.Equal(new[] { "CxConnector", "DemoConnector", "InstantConnector" }, connectors);
        Assert.Single(scope.ServiceProvider.GetServices<IDeviceConnector>(), c => c.IsSimulator);
        Assert.IsType<RoutingChangeGateway>(scope.ServiceProvider.GetRequiredService<IApprovedChangeGateway>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<ArubaLabApprovedChangeGateway>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<DeviceHttpFactory>());
    }
}
