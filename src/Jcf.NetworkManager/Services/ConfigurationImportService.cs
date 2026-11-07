using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Jcf.NetworkManager.Connectors;
using Jcf.NetworkManager.Data;
using Jcf.NetworkManager.Domain;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace Jcf.NetworkManager.Services;

/// <summary>
/// Encrypted, full-fidelity MANUAL running-configuration import/recovery. The existing
/// real-device connectors deliberately drop raw configs; no unverified automatic
/// backup collection or device restore is claimed. Encrypted backup files live outside
/// webroot and SQL stores only nonsecret metadata and bounded projections.
/// </summary>
public sealed class ConfigurationImportService(
    IDbContextFactory<NetworkDbContext> factory,
    IDataProtectionProvider protection,
    IConfiguration config)
{
    public const string Scope="ConfigImport";
    private const int MaximumBytes=1_048_576;
    private IDataProtector Protector=>protection.CreateProtector("JCF.NetworkManager.ConfigImport.FullFidelity.v1");
    public sealed record Manifest(Guid Id,Guid DeviceId,string DeviceName,
        string Format,string Sha256,int Bytes,string ImportedBy,DateTime ImportedUtc,
        string Reason,int RecognizedLines,int UnrecognizedLines,string FileName);
    public sealed record Readback(Manifest Manifest,string SanitizedJson,string Quality);
    public sealed record Recovery(Manifest Manifest,byte[] RawBytes);

    private string DirectoryPath
    {
        get
        {
            var value=config["Jcf:Recovery:EncryptedBackupDirectory"];
            if(string.IsNullOrWhiteSpace(value)||!Path.IsPathFullyQualified(value))
                throw new InvalidOperationException("Set an absolute protected Jcf:Recovery:EncryptedBackupDirectory outside webroot.");
            var full=Path.GetFullPath(value);
            var current=Path.GetFullPath(Directory.GetCurrentDirectory());
            if(full.StartsWith(Path.Combine(current,"wwwroot")+Path.DirectorySeparatorChar,
                StringComparison.Ordinal))
                throw new InvalidOperationException("Recovery storage must not be under wwwroot.");
            return full;
        }
    }
    private bool EnforceTransport()=>config.GetValue<bool>("Jcf:TemporaryHttp")||
        config.GetValue<bool>("Jcf:DemoMode");
    public bool FullRecoveryRequiresTls => EnforceTransport();
    static string Actor(string actor)=>!string.IsNullOrWhiteSpace(actor)&&actor.Length<=140
       ?actor:throw new UnauthorizedAccessException("Authenticated operator required.");
    static string Reason(string reason)=>!string.IsNullOrWhiteSpace(reason)&&reason.Trim().Length>=10&&reason.Length<=250
       ?reason.Trim():throw new ArgumentException("Enter the import/recovery purpose (10–250 characters).");
    static string ManifestName(Guid id)=>"import/"+id.ToString("N");
    public static string NormalizeFormat(string value) =>
        value is "AosCxCli" or "InstantCli" ? value :
        throw new ArgumentException("Only AOS-CX and Aruba Instant CLI configuration formats are accepted.");

    public async Task<Readback> ImportManualAsync(Guid deviceId,string raw,string format,
        string actor,string reason,CancellationToken ct=default)
    {
        actor=Actor(actor);reason=Reason(reason);format=NormalizeFormat(format);
        if(EnforceTransport())
            throw new InvalidOperationException(
                "Full configuration import is disabled over temporary HTTP or demo transport. Enable HTTPS before importing secrets.");
        if(string.IsNullOrWhiteSpace(raw))throw new ArgumentException("Configuration is empty.");
        var bytes=Encoding.UTF8.GetBytes(raw);
        if(bytes.Length>MaximumBytes)throw new ArgumentException("Configuration file exceeds 1 MiB.");
        await using var db=await factory.CreateDbContextAsync(ct);
        var device=await db.Devices.AsNoTracking().SingleOrDefaultAsync(d=>d.Id==deviceId,ct)
            ??throw new KeyNotFoundException("Device not registered.");
        if(format=="InstantCli"&&!device.IsAccessPoint ||
           format=="AosCxCli"&&device.IsAccessPoint)
            throw new ArgumentException("Configuration syntax must match registered hardware family.");
        var id=Guid.NewGuid();
        var root=DirectoryPath;
        Directory.CreateDirectory(root);
        if(OperatingSystem.IsLinux()) Directory.SetUnixFileMode(root,
           UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute);
        var file=id.ToString("N")+".jcfenc";
        var path=Path.Combine(root,file);
        var temp=Path.Combine(root,id.ToString("N")+".tmp");
        // The file on disk is encrypted AND authenticated; key ring is held separately
        // and protected with the installation certificate. No raw copy is created.
        var cipher=Protector.Protect(bytes);
        try
        {
            await File.WriteAllBytesAsync(temp,cipher,ct);
            if(OperatingSystem.IsLinux())File.SetUnixFileMode(temp,UnixFileMode.UserRead|UnixFileMode.UserWrite);
            File.Move(temp,path,overwrite:false);
        }
        finally {if(File.Exists(temp))File.Delete(temp);}
        var parsed=format=="InstantCli"?ParseInstant(raw):ParseCx(raw);
        var manifest=new Manifest(id,deviceId,device.Name,format,
            Convert.ToHexString(SHA256.HashData(bytes)),bytes.Length,actor,DateTime.UtcNow,
            reason,parsed.Known,parsed.Unknown,file);
        var json=JsonSerializer.Serialize(manifest);
        try
        {
            db.PolicyTemplates.Add(new PolicyTemplate{
                Id=id,Scope=Scope,Name=ManifestName(id),LocationId=device.LocationId,
                NonSecretJson=json,CreatedBy=actor,CreatedUtc=manifest.ImportedUtc});
            var safe=JsonSerializer.Serialize(new{
                schema="import-sanitized/v1",format,recognizedLines=parsed.Known,
                unrecognizedLines=parsed.Unknown,content=parsed.SafeJson});
            if(safe.Length<=11500)
                db.ConfigurationSnapshots.Add(new ConfigurationSnapshot{
                    DeviceId=deviceId,Firmware=device.Firmware??"Unknown",
                    DataSha256=manifest.Sha256,SanitizedJson=safe,
                    CapturedBy="manual-import-unverified",CapturedUtc=manifest.ImportedUtc});
            db.AuditEvents.Add(new AuditEvent{Actor=actor,Action="Import encrypted configuration",
                Target=deviceId.ToString("N"),Outcome="Backup "+id.ToString("N")[..12]+
                    "; manual/no device write"});
            await db.SaveChangesAsync(ct);
        }
        catch { if(File.Exists(path))File.Delete(path);throw; }
        return new Readback(manifest,JsonSerializer.Serialize(parsed.SafeJson),"Manual import; not verified device readback");
    }

    private sealed record ParseResult(int Known,int Unknown,object SafeJson);
    private static ParseResult ParseInstant(string raw)
    {
        var result=InstantWlanReadback.Parse(raw);
        var known=result.IsUsable?result.Profiles.Count:0;
        var total=raw.Split('\n').Count(x=>!string.IsNullOrWhiteSpace(x));
        return new ParseResult(known,Math.Max(0,total-known),
            new {parser=result.Status,wlanProfiles=result.Profiles});
    }
    private static ParseResult ParseCx(string raw)
    {
        var vlans=new SortedSet<int>();
        var iface=new SortedDictionary<string,(int? Access,int? Native,List<int>? Tagged)>(StringComparer.Ordinal);
        string? port=null;
        int known=0,unknown=0;
        var portPattern=new Regex(@"^(?:1/[1-9][0-9]?/[1-9][0-9]{0,2}|lag[1-9][0-9]{0,2})$",
            RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(100));
        foreach(var untrusted in raw.Split('\n'))
        {
            var line=untrusted.Trim();
            if(line.Length==0||line.StartsWith('!')||line.StartsWith('#'))continue;
            if(Regex.Match(line,@"^vlan (\d{1,4})$",RegexOptions.CultureInvariant,
                TimeSpan.FromMilliseconds(100)) is var vlanMatch && vlanMatch.Success &&
               int.TryParse(vlanMatch.Groups[1].Value,out var vlan) && vlan is >=1 and <=4094)
            {vlans.Add(vlan);known++;port=null;continue;}
            if(line.StartsWith("interface ",StringComparison.OrdinalIgnoreCase))
            {
                var token=line[10..].Trim();
                if(portPattern.IsMatch(token)){port=token;if(!iface.ContainsKey(port))iface[port]=(null,null,null);known++;}
                else {port=null;unknown++;}
                continue;
            }
            if(port is null){unknown++;continue;}
            var current=iface[port];
            if(line.StartsWith("vlan access ",StringComparison.OrdinalIgnoreCase) &&
               int.TryParse(line["vlan access ".Length..],out var access) && access is >=1 and <=4094)
            {iface[port]=(access,current.Native,current.Tagged);known++;continue;}
            if(line.StartsWith("vlan trunk native ",StringComparison.OrdinalIgnoreCase) &&
               int.TryParse(line["vlan trunk native ".Length..],out var native) && native is >=1 and <=4094)
            {iface[port]=(current.Access,native,current.Tagged);known++;continue;}
            if(line.StartsWith("vlan trunk allowed ",StringComparison.OrdinalIgnoreCase))
            {
                var text=line["vlan trunk allowed ".Length..];
                var parsed=new List<int>();
                foreach(var part in text.Split(',')){
                    if(!int.TryParse(part.Trim(),out var v)||v is <1 or >4094){parsed.Clear();break;}
                    parsed.Add(v);
                }
                if(parsed.Count>0&&parsed.Count<=64){iface[port]=(current.Access,current.Native,parsed.Distinct().Order().ToList());known++;continue;}
            }
            unknown++;
        }
        // AOS-CX CLI may contain secrets, UIDs, management auth or unsupported commands:
        // only vetted typed interface + VLAN values leave this routine.
        var safe=new {
            vlans=vlans.ToArray(),
            interfaces=iface.Select(x=>new{
                port=x.Key,accessVlan=x.Value.Access,nativeVlan=x.Value.Native,
                taggedVlans=x.Value.Tagged}).ToArray()
        };
        return new ParseResult(known,unknown,safe);
    }
    public async Task<IReadOnlyList<Manifest>> ListAsync(Guid? deviceId=null,CancellationToken ct=default)
    {
        await using var db=await factory.CreateDbContextAsync(ct);
        var rows=await db.PolicyTemplates.AsNoTracking().Where(x=>x.Scope==Scope).ToListAsync(ct);
        return rows.Select(x=>JsonSerializer.Deserialize<Manifest>(x.NonSecretJson)!)
            .Where(x=>x is not null&&(deviceId is null||x.DeviceId==deviceId.Value))
            .OrderByDescending(x=>x.ImportedUtc).ToList();
    }
    public async Task<Recovery> RecoverAsync(Guid backupId,string actor,string purpose,CancellationToken ct=default)
    {
        actor=Actor(actor);purpose=Reason(purpose);
        if(EnforceTransport())
            throw new InvalidOperationException("Full recovery export is not permitted over temporary HTTP.");
        await using var db=await factory.CreateDbContextAsync(ct);
        var row=await db.PolicyTemplates.AsNoTracking().SingleOrDefaultAsync(x=>
            x.Id==backupId&&x.Scope==Scope,ct)??throw new KeyNotFoundException("Backup not found.");
        var manifest=JsonSerializer.Deserialize<Manifest>(row.NonSecretJson)
            ??throw new InvalidOperationException("Invalid backup manifest.");
        if(manifest.Id!=backupId || manifest.FileName!=backupId.ToString("N")+".jcfenc")
            throw new InvalidOperationException("Backup manifest integrity check failed.");
        var encrypted=await File.ReadAllBytesAsync(Path.Combine(DirectoryPath,manifest.FileName),ct);
        byte[] plaintext;
        try {plaintext=Protector.Unprotect(encrypted);}
        catch(CryptographicException){throw new InvalidOperationException(
            "Backup decryption failed. Restore the original protected Data Protection key ring and certificate.");}
        var digest=Convert.ToHexString(SHA256.HashData(plaintext));
        if(digest!=manifest.Sha256 || plaintext.Length!=manifest.Bytes)
            throw new InvalidOperationException("Backup payload integrity mismatch.");
        db.AuditEvents.Add(new AuditEvent {Actor=actor,Action="Export recovery configuration",
            Target=backupId.ToString("N"),Outcome="Authorized recovery export; "+purpose[..Math.Min(80,purpose.Length)]});
        await db.SaveChangesAsync(ct);
        return new Recovery(manifest,plaintext);
    }
}