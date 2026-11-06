using Jcf.NetworkManager.Data;
using Jcf.NetworkManager.Domain;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Jcf.NetworkManager.Services;

/// <summary>
/// Local account lifecycle (create, disable/enable, unlock, role change). Every change is audited and
/// rotates the security stamp so existing sessions are re-validated within one minute.
/// Guards prevent an administrator from locking the platform out of administration.
/// </summary>
public sealed class OperatorAccountService(UserManager<OperatorUser> users,IDbContextFactory<NetworkDbContext> dbf)
{
    public sealed record AccountView(Guid Id,string? UserName,string DisplayName,bool IsDisabled,bool IsLockedOut,string Role,DateTime CreatedUtc);

    public async Task<List<AccountView>> ListAsync(CancellationToken ct=default)
    {
        var list=await users.Users.AsNoTracking().OrderBy(x=>x.UserName).Take(500).ToListAsync(ct);
        var result=new List<AccountView>(list.Count);
        foreach(var u in list)
        {
            var roles=await users.GetRolesAsync(u);
            var locked=u.LockoutEnd is DateTimeOffset end && end>DateTimeOffset.UtcNow;
            result.Add(new(u.Id,u.UserName,u.DisplayName,u.IsDisabled,locked && !u.IsDisabled,string.Join(",",roles),u.CreatedUtc));
        }
        return result;
    }

    public async Task<AccountView> CreateAsync(string username,string displayName,string password,string role,string actor,CancellationToken ct=default)
    {
        if(!JcfRoles.All.Contains(role,StringComparer.Ordinal)) throw new ArgumentException("Unknown role.");
        username=(username??"").Trim();
        if(username.Length is 0 or >100) throw new ArgumentException("Username must be 1-100 characters.");
        displayName=(displayName??"").Trim();
        var user=new OperatorUser{UserName=username,DisplayName=displayName[..Math.Min(140,displayName.Length)]};
        var created=await users.CreateAsync(user,password??"");
        if(!created.Succeeded) throw new ArgumentException("Account rejected: "+string.Join("; ",created.Errors.Select(e=>e.Description)));
        var assigned=await users.AddToRoleAsync(user,role);
        if(!assigned.Succeeded)
        {
            await users.DeleteAsync(user);
            throw new InvalidOperationException("Role assignment failed; the account was not kept.");
        }
        await AuditAsync(actor,"Create operator account",user.Id,"Role: "+role,ct);
        return new(user.Id,user.UserName,user.DisplayName,false,false,role,user.CreatedUtc);
    }

    public async Task SetDisabledAsync(Guid id,bool disabled,string actor,CancellationToken ct=default)
    {
        var user=await Find(id);
        if(disabled)
        {
            if(string.Equals(user.UserName,actor,StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("You cannot disable your own account.");
            await EnsureAnotherActiveAdminAsync(user);
        }
        user.IsDisabled=disabled;
        Check(await users.UpdateAsync(user));
        // A lockout end far in the future blocks sign-in at the Identity layer as well.
        Check(await users.SetLockoutEndDateAsync(user,disabled?DateTimeOffset.MaxValue:null));
        Check(await users.UpdateSecurityStampAsync(user));
        await AuditAsync(actor,disabled?"Disable operator account":"Enable operator account",id,disabled?"Disabled; sessions revoked":"Enabled",ct);
    }

    public async Task UnlockAsync(Guid id,string actor,CancellationToken ct=default)
    {
        var user=await Find(id);
        if(user.IsDisabled) throw new InvalidOperationException("Account is disabled; enable it instead of unlocking.");
        Check(await users.SetLockoutEndDateAsync(user,null));
        Check(await users.ResetAccessFailedCountAsync(user));
        await AuditAsync(actor,"Unlock operator account",id,"Lockout cleared",ct);
    }

    public async Task SetRoleAsync(Guid id,string role,string actor,CancellationToken ct=default)
    {
        if(!JcfRoles.All.Contains(role,StringComparer.Ordinal)) throw new ArgumentException("Unknown role.");
        var user=await Find(id);
        var current=await users.GetRolesAsync(user);
        if(current.Contains(JcfRoles.PlatformAdmin) && role!=JcfRoles.PlatformAdmin)
        {
            if(string.Equals(user.UserName,actor,StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("You cannot remove your own administrator role.");
            await EnsureAnotherActiveAdminAsync(user);
        }
        if(current.Count>0) Check(await users.RemoveFromRolesAsync(user,current));
        Check(await users.AddToRoleAsync(user,role));
        Check(await users.UpdateSecurityStampAsync(user));
        await AuditAsync(actor,"Change operator role",id,string.Join(",",current)+" -> "+role,ct);
    }

    private async Task EnsureAnotherActiveAdminAsync(OperatorUser target)
    {
        var admins=await users.GetUsersInRoleAsync(JcfRoles.PlatformAdmin);
        if(admins.Any(a=>a.Id==target.Id) && !admins.Any(a=>a.Id!=target.Id && !a.IsDisabled))
            throw new InvalidOperationException("At least one other active platform administrator must remain.");
    }
    private async Task<OperatorUser> Find(Guid id) => await users.FindByIdAsync(id.ToString()) ?? throw new KeyNotFoundException("Account not found.");
    private static void Check(IdentityResult r) { if(!r.Succeeded) throw new InvalidOperationException("Account update failed: "+string.Join("; ",r.Errors.Select(e=>e.Code))); }
    private async Task AuditAsync(string actor,string action,Guid target,string outcome,CancellationToken ct)
    {
        await using var db=await dbf.CreateDbContextAsync(ct);
        db.AuditEvents.Add(new AuditEvent{Actor=actor,Action=action,Target=target.ToString(),Outcome=outcome[..Math.Min(200,outcome.Length)]});
        await db.SaveChangesAsync(ct);
    }
}
