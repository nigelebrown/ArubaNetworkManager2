using Jcf.NetworkManager.Data;
using Jcf.NetworkManager.Domain;
using Jcf.NetworkManager.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Jcf.NetworkManager.Tests;

/// <summary>Fail-closed tests. Real SQL + actual AP/CX tests run separately in the JCF pilot.</summary>
public sealed class HardwareAcceptanceTests
{
    [Fact]
    public async Task Disabled_pilot_does_not_access_a_device_or_a_database()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>()).Build();
        var service = new HardwareAcceptanceService(null!, null!, configuration);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(()=>
            service.CaptureAsync(Guid.NewGuid(),"engineer"));
        Assert.Contains("disabled",ex.Message,StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task In_memory_provider_cannot_be_misrepresented_as_real_sql_hardware_acceptance()
    {
        var options = new DbContextOptionsBuilder<NetworkDbContext>()
            .UseInMemoryDatabase("pilot-rejection-" + Guid.NewGuid().ToString("N")).Options;
        var dbFactory = new FakeFactory(options);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> {
            ["Jcf:PilotAcceptance:Enabled"] = "true",
            ["Jcf:PilotAcceptance:AllowedDeviceIps:0"] = "10.250.2.71"
        }).Build();
        var service = new HardwareAcceptanceService(dbFactory,null!,configuration);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(()=>
            service.CaptureAsync(Guid.NewGuid(),"engineer"));
        Assert.Contains("SQL Server",ex.Message,StringComparison.OrdinalIgnoreCase);
    }

    private sealed class FakeFactory(DbContextOptions<NetworkDbContext> options) : IDbContextFactory<NetworkDbContext>
    {
        public NetworkDbContext CreateDbContext()=>new(options);
    }
}
