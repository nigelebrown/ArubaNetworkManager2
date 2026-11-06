using Jcf.NetworkManager.Connectors;
using Jcf.NetworkManager.Domain;
using Jcf.NetworkManager.Services;
using Jcf.NetworkManager.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;
using Xunit;


namespace Jcf.NetworkManager.Tests
{
    public sealed class EnterpriseTelemetryTests
    {
        [Fact]
        public void Cx_parser_handles_missing_counters_as_unknown()
        {
            using var json=JsonDocument.Parse("{\"1/1/1\":{\"link_state\":\"up\",\"rx_bytes\":1000,\"tx_bytes\":2000},\"1/1/2\":{\"link_state\":\"down\"}}");
            var ports=CxConnector.ParsePortReadings(json.RootElement);
            Assert.Equal(2,ports.Count);
            Assert.Equal(1000L,ports[0].RxBytes);
            Assert.Null(ports[1].RxBytes);
        }
        [Fact]
        public void Instant_client_discovery_keeps_unverified_metrics_null()
        {
            var clients=InstantConnector.ParseClients("aa:bb:cc:dd:ee:ff 10.1.2.3\n00:11:22:33:44:55 10.1.2.4");
            Assert.Equal(2,clients.Count);
            Assert.All(clients,c=>{Assert.Null(c.RssiDbm);Assert.Null(c.SnrDb);});
        }
        [Fact]
        public async Task Audit_records_cannot_be_modified_through_EF()
        {
            var services=new ServiceCollection();
            services.AddDbContextFactory<NetworkDbContext>(o=>o.UseInMemoryDatabase("audit-"+Guid.NewGuid()));
            using var provider=services.BuildServiceProvider();
            var factory=provider.GetRequiredService<IDbContextFactory<NetworkDbContext>>();
            await using var db=await factory.CreateDbContextAsync();
            var item=new AuditEvent{Actor="someone",Action="Read",Target="device",Outcome="Success"};
            db.AuditEvents.Add(item);
            await db.SaveChangesAsync();
            item.Outcome="tampered";
            await Assert.ThrowsAsync<InvalidOperationException>(()=>db.SaveChangesAsync());
        }
    }
}
