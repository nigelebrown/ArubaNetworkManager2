using Jcf.NetworkManager.Data;
using Jcf.NetworkManager.Domain;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Jcf.NetworkManager.Services;

/// <summary>
/// Initial admin is created exactly once. Never seeds a known password. For production,
/// provision the password from a root-readable file outside the webroot, then delete it.
/// </summary>
public static class LocalIdentityBootstrap
{
    public static readonly string[] Roles = JcfRoles.All;

    public static async Task InitialiseAsync(IServiceProvider services,IConfiguration configuration)
    {
        using var scope=services.CreateScope();
        var roles=scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
        var users=scope.ServiceProvider.GetRequiredService<UserManager<OperatorUser>>();
        foreach(var role in Roles)
            if(!await roles.RoleExistsAsync(role))
            {
                var created=await roles.CreateAsync(new IdentityRole<Guid>(role));
                if(!created.Succeeded) throw new InvalidOperationException("Cannot create security role: "+role);
            }

        if(users.Users.Any()) return;
        var name=configuration["Jcf:BootstrapAdmin:Username"];
        var passwordFile=configuration["Jcf:BootstrapAdmin:PasswordFile"];
        if(string.IsNullOrWhiteSpace(name)||string.IsNullOrWhiteSpace(passwordFile)||!File.Exists(passwordFile))
            throw new InvalidOperationException("Database has no users. Set bootstrap admin username and a protected password-file path. Fail closed.");
        var password=(await File.ReadAllTextAsync(passwordFile)).TrimEnd('\r','\n');
        try
        {
            var user=new OperatorUser {UserName=name,DisplayName="Initial Platform Administrator",EmailConfirmed=false};
            var result=await users.CreateAsync(user,password);
            if(!result.Succeeded) throw new InvalidOperationException("Admin bootstrap did not meet password policy: "+
                string.Join("; ",result.Errors.Select(x=>x.Code)));
            var assigned=await users.AddToRoleAsync(user,"PlatformAdmin");
            if(!assigned.Succeeded) throw new InvalidOperationException("Cannot assign bootstrap administrator role.");
        }
        finally { password=""; }
    }
}
