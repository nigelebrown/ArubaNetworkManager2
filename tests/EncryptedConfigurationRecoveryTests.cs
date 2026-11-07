using System.Text;
using Jcf.NetworkManager.Data;
using Jcf.NetworkManager.Domain;
using Jcf.NetworkManager.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Jcf.NetworkManager.Tests;
public sealed class EncryptedConfigurationRecoveryTests
{
    sealed class Factory(DbContextOptions<NetworkDbContext> o):IDbContextFactory<NetworkDbContext>
    {
        public NetworkDbContext CreateDbContext()=>new(o);
        public Task<NetworkDbContext> CreateDbContextAsync(CancellationToken ct=default)=>Task.FromResult(CreateDbContext());
    }
    static (Factory f,ConfigurationImportService service,string path) New(bool temporaryHttp=false)
    {
        var f=new Factory(new DbContextOptionsBuilder<NetworkDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var path=Path.Combine(Path.GetTempPath(),"jcf-config-recovery-"+Guid.NewGuid().ToString("N"));
        var keys=Path.Combine(path,"keys");
        Directory.CreateDirectory(keys);
        var config=new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string,string?>{
                ["Jcf:Recovery:EncryptedBackupDirectory"]=Path.Combine(path,"backups"),
                ["Jcf:TemporaryHttp"]=temporaryHttp.ToString()
            }).Build();
        var protection=DataProtectionProvider.Create(new DirectoryInfo(keys));
        return(f,new ConfigurationImportService(f,protection,config),path);
    }
    [Fact]
    public async Task Full_original_is_encrypted_unknown_lines_preserved_and_recovery_checksum_verified()
    {
        var(f,service,path)=New();
        try
        {
            var d=new Device{Name="Real CX test",Kind=DeviceKind.Cx6200,ManagementIp="10.120.1.12"};
            await using(var db=f.CreateDbContext())
            {db.Devices.Add(d);await db.SaveChangesAsync();}
            const string original="""
                vlan 100
                vlan 200
                interface 1/1/1
                  vlan access 100
                  description Reception
                  aaa authentication local-password secret-value-MUST-NOT-BE-IN-SQL
                interface 1/1/49
                  vlan trunk native 100
                  vlan trunk allowed 100,200
                unknown-other-command WILL-BE-RECOVERABLE-ONLY
                """;
            var imported=await service.ImportManualAsync(d.Id,original,"AosCxCli",
                "test-operator","Full before-migration recovery evidence");
            Assert.True(imported.Manifest.UnrecognizedLines>0);
            var encrypted=await File.ReadAllBytesAsync(
                Path.Combine(path,"backups",imported.Manifest.FileName));
            Assert.DoesNotContain("MUST-NOT-BE-IN-SQL",Encoding.UTF8.GetString(encrypted));
            await using(var db=f.CreateDbContext())
            {
                Assert.DoesNotContain("secret-value",Assert.Single(db.ConfigurationSnapshots).SanitizedJson);
                Assert.DoesNotContain("WILL-BE-RECOVERABLE-ONLY",Assert.Single(db.ConfigurationSnapshots).SanitizedJson);
                Assert.Contains(await db.AuditEvents.ToListAsync(),x=>x.Action=="Import encrypted configuration");
            }
            var recovered=await service.RecoverAsync(imported.Manifest.Id,
                "platform-admin","Authorized restoration exercise");
            Assert.Equal(original,Encoding.UTF8.GetString(recovered.RawBytes));
            await using(var db=f.CreateDbContext()){
                Assert.Contains(await db.AuditEvents.ToListAsync(),x=>x.Action=="Export recovery configuration");
            }
        }
        finally{Directory.Delete(path,recursive:true);}
    }
    [Fact]
    public async Task Full_import_is_refused_over_temporary_http()
    {
        var(f,service,path)=New(true);
        try{
            var d=new Device{Name="AP",Kind=DeviceKind.Ap505,ManagementIp="10.0.0.1"};
            await using(var db=f.CreateDbContext()){db.Devices.Add(d);await db.SaveChangesAsync();}
            await Assert.ThrowsAsync<InvalidOperationException>(()=>
                service.ImportManualAsync(d.Id,"wlan ssid-profile STAFF","InstantCli",
                    "operator","Review config before site update"));
            await using(var check=f.CreateDbContext())
                Assert.Empty(await check.PolicyTemplates.ToListAsync());
        }finally{Directory.Delete(path,true);}
    }
    [Fact]
    public async Task Wrong_hardware_grammar_is_refused()
    {
        var(f,service,path)=New();
        try{
            var d=new Device{Name="CX",Kind=DeviceKind.Cx6100,ManagementIp="10.0.0.2"};
            await using(var db=f.CreateDbContext()){db.Devices.Add(d);await db.SaveChangesAsync();}
            await Assert.ThrowsAsync<ArgumentException>(()=>
                service.ImportManualAsync(d.Id,"wlan ssid-profile STAFF","InstantCli",
                    "operator","Configuration backup review"));
        }finally{Directory.Delete(path,true);}
    }
}