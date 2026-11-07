using System.Net;
using System.Net.Sockets;

namespace Jcf.NetworkManager.Services;

/// <summary>
/// Explicit, bounded, operator-initiated discovery of a management REST *port*, not a claim
/// that a device is Aruba or authenticated. No credentials, HTTP or configuration changes.
/// </summary>
public sealed class OnboardingDiscoveryService(IConfiguration configuration)
{
    public sealed record PortObservation(string Address,int Port,bool Reachable);
    public const int MaxAddresses=64;
    public const int MaxParallel=6;
    private static readonly TimeSpan Timeout=TimeSpan.FromMilliseconds(850);

    public static IReadOnlyList<string> ExpandRange(string start,string end,IReadOnlyList<string> allowed)
    {
        if(!IPAddress.TryParse(start,out var low) || !IPAddress.TryParse(end,out var high) ||
           low.AddressFamily!=AddressFamily.InterNetwork || high.AddressFamily!=AddressFamily.InterNetwork)
            throw new ArgumentException("Enter IPv4 start and end addresses.");
        static uint Number(IPAddress addr)
        {
            var b=addr.GetAddressBytes();
            return ((uint)b[0]<<24)|((uint)b[1]<<16)|((uint)b[2]<<8)|b[3];
        }
        static IPAddress Address(uint n) => new(new byte[]{(byte)(n>>24),(byte)(n>>16),(byte)(n>>8),(byte)n});
        var a=Number(low);var b=Number(high);
        if(b<a || (ulong)b-a+1>MaxAddresses)
            throw new ArgumentException($"Scan up to {MaxAddresses} consecutive IPv4 addresses in one batch.");
        var result=new List<string>((int)((ulong)b-a+1));
        for(ulong index=a;index<=b;index++)
        {
            var n=(uint)index;
            var value=Address(n).ToString();
            if(!AddressPolicy.IsAllowed(value,allowed))
                throw new InvalidOperationException($"IP {value} is outside Jcf:AllowedManagementCidrs. An administrator must explicitly authorize that subnet before scanning.");
            // Prevent accidental link-local/localhost/API documentation-space scanning.
            if(n>=0x7F000000 && n<=0x7FFFFFFF || n>=0xA9FE0000 && n<=0xA9FEFFFF)
                throw new InvalidOperationException("Localhost and link-local addresses cannot be scanned.");
            result.Add(value);
        }
        return result;
    }

    public async Task<IReadOnlyList<PortObservation>> ScanAsync(string start,string end,bool accessPoints,CancellationToken ct=default)
    {
        var allowed=configuration.GetSection("Jcf:AllowedManagementCidrs").Get<string[]>()??[];
        var addresses=ExpandRange(start,end,allowed);
        var port=accessPoints?4343:443;
        using var limit=new SemaphoreSlim(MaxParallel);
        var tasks=addresses.Select(async address =>
        {
            await limit.WaitAsync(ct);
            try
            {
                using var tcp=new TcpClient(AddressFamily.InterNetwork);
                try
                {
                    await tcp.ConnectAsync(IPAddress.Parse(address),port,ct).AsTask().WaitAsync(Timeout,ct);
                    return new PortObservation(address,port,true);
                }
                catch(Exception ex) when(ex is SocketException or IOException or TimeoutException or OperationCanceledException)
                {
                    if(ct.IsCancellationRequested) ct.ThrowIfCancellationRequested();
                    return new PortObservation(address,port,false);
                }
            }
            finally {limit.Release();}
        });
        return await Task.WhenAll(tasks);
    }
}
