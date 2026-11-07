using System.Security.Claims;
using System.Net;
using Microsoft.AspNetCore.HttpOverrides;
using Jcf.NetworkManager.Components;
using Jcf.NetworkManager.Connectors;
using Jcf.NetworkManager.Data;
using Jcf.NetworkManager.Domain;
using Jcf.NetworkManager.Services;
using Jcf.NetworkManager.Workflow;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

var builder=WebApplication.CreateBuilder(args);
// Optional root-managed config for native CentOS hosting; keeps SQL credentials out of source
// and avoids putting connection strings in process arguments or systemd unit files.
var secureConfigurationFile=Environment.GetEnvironmentVariable("JCF_CONFIG_FILE");
if(!string.IsNullOrWhiteSpace(secureConfigurationFile))
    builder.Configuration.AddJsonFile(secureConfigurationFile,optional:false,reloadOnChange:false);
var demo=builder.Configuration.GetValue<bool>("Jcf:DemoMode");
var temporaryHttp=builder.Configuration.GetValue<bool>("Jcf:TemporaryHttp");
// This temporary private-network HTTP mode is NOT production-grade transport security.
// It must be explicitly enabled. Real Identity and SQL remain mandatory.
if(temporaryHttp && (demo || !builder.Environment.IsProduction()))
    throw new InvalidOperationException("TemporaryHttp is available only for real SQL Identity in Production; demonstration authentication is never exposed.");
// Sample data is independent of authentication: production identity + SQL + simulated inventory.
if(builder.Environment.IsProduction() && !demo && !string.IsNullOrWhiteSpace(secureConfigurationFile))
{
    if(string.IsNullOrWhiteSpace(builder.Configuration["Jcf:DataProtection:KeysDirectory"]) ||
       string.IsNullOrWhiteSpace(builder.Configuration["Jcf:DataProtection:CertificatePath"]))
        throw new InvalidOperationException("Production requires a persistent Data Protection key directory and key-encryption certificate.");
}

if(demo && !(builder.Environment.IsDevelopment() || builder.Environment.IsEnvironment("Testing")))
    throw new InvalidOperationException("DemoMode cannot run in Production. Set ASPNETCORE_ENVIRONMENT=Development for isolated demos.");
// Real-equipment changes are gated by an explicit target allowlist, per-firmware certification and independent approval.
// The legacy global switch alone is refused: writes must name their targets (Jcf:DeviceWrites:AllowedCidrs/AllowedIps).
var writePolicy=DeviceWritePolicy.From(builder.Configuration);
if(builder.Configuration.GetValue<bool>("Jcf:EnableDeviceWrites") && !writePolicy.Enabled)
    throw new InvalidOperationException("Jcf:EnableDeviceWrites is not supported on its own. Use Jcf:DeviceWrites:Enabled with an explicit AllowedCidrs/AllowedIps target list.");
var badWriteCidrs=writePolicy.AllowedCidrs.Where(c=>!AddressPolicy.IsValidCidr(c)).ToArray();
if(badWriteCidrs.Length>0)
    throw new InvalidOperationException("Invalid entries in Jcf:DeviceWrites:AllowedCidrs: "+string.Join(", ",badWriteCidrs));
// Fail fast if the management-network allowlist is missing or malformed. An empty list silently refuses every
// device (e.g. when the process is started outside its content root and appsettings.json is never loaded).
var managementCidrs=builder.Configuration.GetSection("Jcf:AllowedManagementCidrs").Get<string[]>() ?? [];
if(managementCidrs.Length==0)
    throw new InvalidOperationException($"Jcf:AllowedManagementCidrs is empty. Check that appsettings.json was loaded (content root: {builder.Environment.ContentRootPath}) or supply Jcf__AllowedManagementCidrs__0.");
var badCidrs=managementCidrs.Where(c=>!AddressPolicy.IsValidCidr(c)).ToArray();
if(badCidrs.Length>0)
    throw new InvalidOperationException("Invalid entries in Jcf:AllowedManagementCidrs: "+string.Join(", ",badCidrs));
// API contract uses enum names ("Ap505", "CreateSsid", "PendingApproval"), not opaque integers.
builder.Services.ConfigureHttpJsonOptions(o=>o.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));
builder.Services.AddProblemDetails();
// Domain validation/authorization failures become 400/403/404/409 problem responses, not HTTP 500.
builder.Services.AddExceptionHandler<ApiExceptionHandler>();
// Cookie-authenticated JSON endpoints must require same-origin anti-forgery proof.
builder.Services.AddAntiforgery(options => options.HeaderName = "X-CSRF-TOKEN");
builder.Services.Configure<ForwardedHeadersOptions>(o=>{
    // Only the on-host loopback reverse proxy may supply the scheme/client address.
    o.ForwardedHeaders=ForwardedHeaders.XForwardedFor|ForwardedHeaders.XForwardedProto;
    o.KnownProxies.Add(IPAddress.Loopback);
    o.KnownProxies.Add(IPAddress.IPv6Loopback);
    o.ForwardLimit=1;
});
builder.Services.AddOpenApi();
// Data Protection protects session cookies, anti-forgery tokens and the encrypted device credential vault.
// Production: persist the key ring outside the web root and back it up with the database (lost keys = re-enter credentials).
var dataProtection=builder.Services.AddDataProtection().SetApplicationName("JCF.NetworkManager");
var keysDirectory=builder.Configuration["Jcf:DataProtection:KeysDirectory"];
if(!string.IsNullOrWhiteSpace(keysDirectory))
{
    dataProtection.PersistKeysToFileSystem(new DirectoryInfo(keysDirectory));
    var certificatePath=builder.Configuration["Jcf:DataProtection:CertificatePath"];
    if(!string.IsNullOrWhiteSpace(certificatePath))
    {
        var passwordFile=builder.Configuration["Jcf:DataProtection:CertificatePasswordFile"];
        var password=string.IsNullOrWhiteSpace(passwordFile) ? null : File.ReadAllText(passwordFile).TrimEnd('\r','\n');
        dataProtection.ProtectKeysWithCertificate(System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadPkcs12FromFile(certificatePath,password));
    }
    else if(OperatingSystem.IsWindows() && builder.Configuration.GetValue("Jcf:DataProtection:ProtectWithDpapi",true))
        dataProtection.ProtectKeysWithDpapi(protectToLocalMachine:true);
}
builder.Services.AddRateLimiter(o => {
    o.RejectionStatusCode=StatusCodes.Status429TooManyRequests;
    // Per-source throttle against credential stuffing. Per-account guessing is stopped separately by
    // Identity lockout (5 failures). Many operators can share one NAT/proxy address, so the per-IP
    // budget must comfortably exceed a shift change at a busy station.
    var loginPermits=Math.Clamp(builder.Configuration.GetValue("Jcf:LoginRateLimit:PermitLimit",30),3,1000);
    var loginWindow=TimeSpan.FromMinutes(Math.Clamp(builder.Configuration.GetValue("Jcf:LoginRateLimit:WindowMinutes",5),1,60));
    o.AddPolicy("login",ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString()??"unknown",_=>new FixedWindowRateLimiterOptions {
        PermitLimit=loginPermits,Window=loginWindow,QueueLimit=0,AutoReplenishment=true
    }));
});
if(demo)
{
    // Do not expose development's synthetic identity beyond the same machine.
    // Docker must bind to the container interface. The host exposure stays loopback-only
    // via Compose's explicit 127.0.0.1 published port.
    if(!builder.Configuration.GetValue<bool>("Jcf:ContainerMode"))
        builder.WebHost.UseUrls("http://127.0.0.1:5180");
    builder.Services.AddAuthentication("Demo")
        .AddScheme<AuthenticationSchemeOptions,DemoAuthenticationHandler>("Demo",_=>{});
    builder.Services.AddDbContextFactory<NetworkDbContext>(o=>o.UseInMemoryDatabase(builder.Configuration["Jcf:DemoDatabaseName"] ?? "JCF-NetworkManager-Demo"));
}
else
{
    var connection=builder.Configuration.GetConnectionString("NetworkManager");
    if(string.IsNullOrWhiteSpace(connection))
        throw new InvalidOperationException("Production startup requires a SQL Server connection string.");
    builder.Services.AddDbContextFactory<NetworkDbContext>(o=>o.UseSqlServer(connection,
        opts=>opts.EnableRetryOnFailure(3,TimeSpan.FromSeconds(4),null)));
    builder.Services.AddIdentity<OperatorUser,IdentityRole<Guid>>(o=> {
        o.Password.RequiredLength=14;
        o.Password.RequireDigit=true;
        o.Password.RequireUppercase=true;
        o.Password.RequireLowercase=true;
        o.Password.RequireNonAlphanumeric=true;
        o.Lockout.DefaultLockoutTimeSpan=TimeSpan.FromMinutes(15);
        o.Lockout.MaxFailedAccessAttempts=5;
        o.User.RequireUniqueEmail=false;
    }).AddEntityFrameworkStores<NetworkDbContext>().AddDefaultTokenProviders();
    builder.Services.ConfigureApplicationCookie(o=> {
        o.Cookie.Name="JcfNetworkManager.Session";
        o.Cookie.HttpOnly=true;
        o.Cookie.SecurePolicy=temporaryHttp ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
        o.Cookie.SameSite=SameSiteMode.Lax;
        o.ExpireTimeSpan=TimeSpan.FromHours(8);
        o.SlidingExpiration=false;
        o.LoginPath="/account/login";
        o.AccessDeniedPath="/account/login";
        // REST clients get 401/403 status codes instead of an HTML login redirect.
        o.Events.OnRedirectToLogin=ctx => ApiAwareRedirect(ctx,StatusCodes.Status401Unauthorized);
        o.Events.OnRedirectToAccessDenied=ctx => ApiAwareRedirect(ctx,StatusCodes.Status403Forbidden);
    });
    builder.Services.Configure<SecurityStampValidatorOptions>(o=>o.ValidationInterval=TimeSpan.FromMinutes(1));

}

builder.Services.AddAuthorizationBuilder()
    .AddPolicy("Viewer",p=>p.RequireAuthenticatedUser())
    .AddPolicy("Operator",p=>p.RequireRole(JcfRoles.Operators))
    .AddPolicy("Approver",p=>p.RequireRole(JcfRoles.Approvers))
    .AddPolicy("Admin",p=>p.RequireRole(JcfRoles.Admins));
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton<CredentialVault>();
builder.Services.AddSingleton<SecretResolver>(sp=>new SecretResolver(sp.GetRequiredService<CredentialVault>()));
builder.Services.AddSingleton<DeviceHttpFactory>(sp=>new DeviceHttpFactory(sp.GetRequiredService<SecretResolver>()));
// Every installation can monitor real equipment. The simulator only ever serves sample devices (Device.IsSimulated).
builder.Services.AddSingleton<IDeviceConnector,InstantConnector>();
builder.Services.AddSingleton<IDeviceConnector,CxConnector>();
builder.Services.AddSingleton<IDeviceConnector,DemoConnector>();
builder.Services.AddScoped<DeviceService>();
builder.Services.AddScoped<HardwareAcceptanceService>();
builder.Services.AddScoped<AnalysisService>();
builder.Services.AddScoped<ChangeWorkflowService>();
builder.Services.AddScoped<CapabilityService>();
builder.Services.AddScoped<EnterpriseOperationsService>();
builder.Services.AddScoped<SiteConfigurationService>();
builder.Services.AddScoped<TemplateCatalogService>();
builder.Services.AddScoped<SiteWizardService>();
builder.Services.AddScoped<ConfigurationImportService>();
builder.Services.AddScoped<SwitchPortManagerService>();
builder.Services.AddScoped<DeploymentPlanService>();
builder.Services.AddScoped<ConfigurationVarianceService>();
builder.Services.AddScoped<NetworkDiagnosticsService>();
builder.Services.AddScoped<IncidentService>();
builder.Services.AddScoped<AlertService>();
builder.Services.AddScoped<SampleDataService>();
if(!demo) builder.Services.AddScoped<OperatorAccountService>();
// Only the approval workflow resolves the gateway. Sample devices -> simulation; registered devices -> Aruba REST.
builder.Services.AddScoped<DemoChangeGateway>();
builder.Services.AddScoped<ArubaLabApprovedChangeGateway>();
builder.Services.AddScoped<IApprovedChangeGateway,RoutingChangeGateway>();
builder.Services.AddHostedService<PollingWorker>();
builder.Services.AddHostedService<HourlyAnalysisWorker>();
builder.Services.AddHostedService<ExecutionRecoveryWorker>();
builder.Services.AddHostedService<MaintenanceWorker>();

var app=builder.Build();
if(!demo) app.UseForwardedHeaders();
app.UseExceptionHandler();
// Browser hardening for an operations console: no framing (clickjacking), no MIME sniffing, no referrer
// leakage, and a CSP that permits only this origin's scripts and the Blazor Server WebSocket.
app.Use(async (context,next)=>{
    var h=context.Response.Headers;
    h["X-Content-Type-Options"]="nosniff";
    h["X-Frame-Options"]="DENY";
    h["Referrer-Policy"]="no-referrer";
    h["Permissions-Policy"]="camera=(), microphone=(), geolocation=()";
    h["Content-Security-Policy"]="default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; "+
        "connect-src 'self' ws: wss:; frame-ancestors 'none'; base-uri 'self'; form-action 'self'; object-src 'none'";
    await next();
});
if(!demo && !temporaryHttp) {app.UseHsts();app.UseHttpsRedirection();}
app.UseStaticFiles();
app.UseRouting();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
// Defense in depth against cross-site configuration submissions through Identity cookies.
// The Blazor server UI uses service methods and does not bypass its own authorization checks.
if(!demo) app.Use(async (context,next) => {
    bool unsafeApi = context.Request.Path.StartsWithSegments("/api/v1") &&
        (HttpMethods.IsPost(context.Request.Method) || HttpMethods.IsPut(context.Request.Method) ||
         HttpMethods.IsPatch(context.Request.Method) || HttpMethods.IsDelete(context.Request.Method));
    if(unsafeApi && context.User.Identity?.IsAuthenticated==true)
    {
        var antiforgery=context.RequestServices.GetRequiredService<IAntiforgery>();
        if(!await antiforgery.IsRequestValidAsync(context))
        {
            context.Response.StatusCode=StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new {error="Anti-forgery token required; GET /api/v1/security/csrf first."});
            return;
        }
    }
    await next();
});
app.UseAntiforgery();

if(!demo) app.MapLocalAuth();
else app.MapDemoPersonas();
app.MapGet("/readyz",async(IDbContextFactory<NetworkDbContext> factory)=>{
    try {
        await using var db=await factory.CreateDbContextAsync();
        return await db.Database.CanConnectAsync() ? Results.Ok(new{status="ready"}) : Results.StatusCode(503);
    }
    catch { return Results.StatusCode(503); }
}).AllowAnonymous();
app.MapOpenApi("/api/openapi.json").RequireAuthorization("Admin");
app.MapGet("/healthz",()=>Results.Ok(new {app="JCF Network Manager",status="up",version=AppInfo.Version,demo})).AllowAnonymous();
// Read-only external application APIs; all mutation requests are handled through the authenticated UI workflows.
var api=app.MapGroup("/api/v1").RequireAuthorization("Viewer");
api.MapGet("/security/csrf",(HttpContext context,IAntiforgery antiforgery) => {
    var tokens=antiforgery.GetAndStoreTokens(context);
    return Results.Ok(new {header="X-CSRF-TOKEN",token=tokens.RequestToken});
});
// Operator-only non-secret configuration exports; no device adapters invoked.
api.MapGet("/reports/variance/{siteId:guid}/{format}",
    async(Guid siteId,string format,ConfigurationVarianceService variance,
        IDbContextFactory<NetworkDbContext> dbf,CancellationToken ct)=>{
        await using var db=await dbf.CreateDbContextAsync(ct);
        if(!await db.Locations.AsNoTracking().AnyAsync(x=>x.Id==siteId,ct))
            return Results.NotFound();
        var report=await variance.BuildAsync(siteId,ct);
        var stem="JCF-Variance-"+siteId.ToString("N");
        return format.ToLowerInvariant() switch {
            "csv"=>Results.File(VarianceExportService.Csv(report),"text/csv; charset=utf-8",stem+".csv"),
            "xlsx"=>Results.File(VarianceExportService.Excel(report),
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",stem+".xlsx"),
            "pdf"=>Results.File(VarianceExportService.Pdf(report),"application/pdf",stem+".pdf"),
            _=>Results.NotFound()
        };
    }).RequireAuthorization("Operator");
api.MapGet("/switches/{deviceId:guid}/ports.csv",
    async(Guid deviceId,SwitchPortManagerService ports,CancellationToken ct)=>{
        try{
            var rows=await ports.InventoryAsync(deviceId,ct);
            var csv=SwitchPortManagerService.ExportCsv(
                rows.Where(x=>x.Mapping is not null).Select(x=>x.Mapping!));
            return Results.File(System.Text.Encoding.UTF8.GetBytes(csv),"text/csv; charset=utf-8",
                "JCF-PortMapping-"+deviceId.ToString("N")+".csv");
        }catch(KeyNotFoundException){return Results.NotFound();}
        catch(ArgumentException){return Results.BadRequest();}
    }).RequireAuthorization("Operator");
api.MapGet("/devices",async(IDbContextFactory<NetworkDbContext> dbf)=>{
    await using var db=await dbf.CreateDbContextAsync();
    return await db.Devices.AsNoTracking().OrderBy(d=>d.Name).Select(d=>new {
        d.Id,d.Name,Model=d.Kind.ToString(),d.ManagementIp,d.LocationId,d.Firmware,d.LastStatus,d.LastSeenUtc,
        d.Enabled,d.IsSimulated,d.ClusterName,CredentialProfile=d.SecretRef,TlsPinned=d.TlsCertSha256!=null,d.LastIssue
    }).ToListAsync();
});
api.MapGet("/locations",async(IDbContextFactory<NetworkDbContext> dbf)=>{
    await using var db=await dbf.CreateDbContextAsync();
    return await db.Locations.AsNoTracking().ToListAsync();
});
api.MapGet("/clusters",async(IDbContextFactory<NetworkDbContext> dbf)=>{
    await using var db=await dbf.CreateDbContextAsync();
    return await db.ClusterMembers.AsNoTracking().ToListAsync();
});
api.MapGet("/recommendations",async(IDbContextFactory<NetworkDbContext> dbf)=>{
    await using var db=await dbf.CreateDbContextAsync();
    return await db.Recommendations.AsNoTracking().OrderByDescending(x=>x.AtUtc).Take(200).ToListAsync();
});
api.MapGet("/analysis-runs",async(IDbContextFactory<NetworkDbContext> dbf)=>{
    await using var db=await dbf.CreateDbContextAsync();
    return await db.AnalysisRuns.AsNoTracking().OrderByDescending(x=>x.AtUtc).Take(100).ToListAsync();
});
api.MapGet("/latest-samples",async(IDbContextFactory<NetworkDbContext> dbf)=>{
    await using var db=await dbf.CreateDbContextAsync();
    return await db.TelemetrySnapshots.AsNoTracking().OrderByDescending(x=>x.AtUtc).Take(100).ToListAsync();
});
// Retired 0.8 placeholder. Approved changes execute through POST /api/v1/change-proposals/{id}/execute.
api.MapPost("/execution-jobs",()=>Results.Problem(
    title:"Use the change-proposal workflow",detail:"There is no free-form execution API. Execute an approved proposal with POST /api/v1/change-proposals/{id}/execute.",statusCode:501))
    .RequireAuthorization("Operator");
api.MapGet("/config-proposals",async(IDbContextFactory<NetworkDbContext> dbf)=>{
    await using var db=await dbf.CreateDbContextAsync();
    return await db.ConfigProposals.AsNoTracking().OrderByDescending(x=>x.RequestedUtc).Take(100)
      .Select(x=>new {x.Id,x.DeviceId,x.Action,x.State,x.RequestedBy,x.RequestedUtc,x.ApprovedBy,x.ApprovedUtc,x.LastOutcome}).ToListAsync();
});
api.MapGet("/capabilities",async(IDbContextFactory<NetworkDbContext> dbf)=>{
    await using var db=await dbf.CreateDbContextAsync();
    return await db.DeviceCapabilities.AsNoTracking().ToListAsync();
});
api.MapGet("/groups",async(IDbContextFactory<NetworkDbContext> dbf)=>{
    await using var db=await dbf.CreateDbContextAsync();
    return await db.DeviceGroups.AsNoTracking().ToListAsync();
});
api.MapGet("/alerts",async(IDbContextFactory<NetworkDbContext> dbf)=>{
    await using var db=await dbf.CreateDbContextAsync();
    return await db.NetworkAlerts.AsNoTracking().OrderByDescending(x=>x.LastSeenUtc).Take(150).ToListAsync();
});
app.MapEnterpriseApis(identityEnabled:!demo);
if(!demo)
{
    // Downloading a recoverable full configuration returns secrets; HTTPS, Admin RBAC,
    // explicit audit reason, no-cache headers, and the original encrypted key ring are mandatory.
    app.MapGet("/api/v1/recovery/{id:guid}/download",
        async(Guid id,string? purpose,HttpContext context,ConfigurationImportService service,
            System.Threading.CancellationToken ct)=>{
            if(context.Request.Scheme!="https" || service.FullRecoveryRequiresTls)
                return Results.Problem("Full recovery export requires HTTPS.",statusCode:403);
            if(string.IsNullOrWhiteSpace(purpose) || purpose.Length<10)
                return Results.BadRequest(new{error="An audited 10-character recovery purpose is required."});
            var actor=context.User.Identity?.Name??"";
            try
            {
                var recovered=await service.RecoverAsync(id,actor,purpose,ct);
                context.Response.Headers.CacheControl="no-store";
                context.Response.Headers.Pragma="no-cache";
                context.Response.Headers["X-Content-Type-Options"]="nosniff";
                var filename="JCF-Recovery-"+recovered.Manifest.DeviceId.ToString("N")+
                    "-"+recovered.Manifest.Id.ToString("N")+".cfg";
                return Results.File(recovered.RawBytes,"application/octet-stream",filename);
            }
            catch(KeyNotFoundException){return Results.NotFound();}
            catch(InvalidOperationException){return Results.Problem(
                "Recovery verification failed. Check encrypted backup storage and key ring.",
                statusCode:409);}
        }).RequireAuthorization("Admin");
}

api.MapGet("/radio-policy",()=>new {approvedWidthsMhz=NetworkChangePolicy.ApprovedWidthsMhz,
    permittedNonDfsChannels5Ghz=NetworkChangePolicy.PreferredNonDfs5GhzChannels,
    excludedDfsChannels5Ghz=NetworkChangePolicy.ExcludedDfs5GhzChannels});
// The app's own CSP already sends frame-ancestors 'none'; suppress Blazor's default second CSP header ('self').
app.MapRazorComponents<App>().AddInteractiveServerRenderMode(o=>o.ContentSecurityFrameAncestorsPolicy=null).RequireAuthorization("Viewer");
await DemoSeed.InitialiseAsync(app.Services,demo,app.Configuration.GetValue("Jcf:SampleData:Enabled",demo));
if(!demo && string.IsNullOrWhiteSpace(keysDirectory))
    app.Logger.LogWarning("Jcf:DataProtection:KeysDirectory is not set. Device credential profiles and sessions depend on the default per-user key ring; set a protected, backed-up directory for production.");
if(!demo) await LocalIdentityBootstrap.InitialiseAsync(app.Services,app.Configuration);
app.Run();

static Task ApiAwareRedirect(Microsoft.AspNetCore.Authentication.RedirectContext<CookieAuthenticationOptions> ctx,int status)
{
    if(ctx.Request.Path.StartsWithSegments("/api")) ctx.Response.StatusCode=status;
    else ctx.Response.Redirect(ctx.RedirectUri);
    return Task.CompletedTask;
}

public partial class Program { } // integration-test WebApplicationFactory entry point
