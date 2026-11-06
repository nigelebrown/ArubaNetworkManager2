using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Jcf.NetworkManager.Data;

/// <summary>EF Core design-time migrations without launching the network/API workers.</summary>
public sealed class NetworkDbContextDesignTimeFactory : IDesignTimeDbContextFactory<NetworkDbContext>
{
    public NetworkDbContext CreateDbContext(string[] args)
    {
        var connection=Environment.GetEnvironmentVariable("ConnectionStrings__NetworkManager") ??
            Environment.GetEnvironmentVariable("JCF_NETWORK_SQL_CONNECTION");
        if(string.IsNullOrWhiteSpace(connection))
            throw new InvalidOperationException("Set ConnectionStrings__NetworkManager for EF migrations.");
        var options=new DbContextOptionsBuilder<NetworkDbContext>().UseSqlServer(connection).Options;
        return new NetworkDbContext(options);
    }
}
