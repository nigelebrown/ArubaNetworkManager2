using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Jcf.NetworkManager.Services;

/// <summary>Development-only synthetic personas. Never registered unless DemoMode=true in Development/Testing.</summary>
public static class DemoPersonas
{
    public const string CookieName = "jcf_demo_persona";
    public sealed record Persona(string Key, string UserName, string Label, string[] Roles);
    public static readonly Persona[] All = new Persona[]
    {
        new("operator", "demo-operator", "Network operator", new[] { JcfRoles.NetworkOperator }),
        new("reviewer-one", "demo-reviewer-one", "Change approver #1", new[] { JcfRoles.ChangeApprover }),
        new("reviewer-two", "demo-reviewer-two", "Change approver #2", new[] { JcfRoles.ChangeApprover }),
        new("admin", "demo-admin", "Platform administrator", new[] { JcfRoles.PlatformAdmin }),
        new("viewer", "demo-viewer", "Read-only viewer", new[] { JcfRoles.Viewer }),
    };
    public static Persona Resolve(string? key) => All.FirstOrDefault(p => p.Key == key) ?? All[0];

    public static void MapDemoPersonas(this WebApplication app)
    {
        // Switching persona lets one person walk through separation-of-duties flows in the demo.
        app.MapGet("/demo/persona/{key}", (string key, string? returnUrl, HttpContext ctx) =>
        {
            var persona = Resolve(key);
            ctx.Response.Cookies.Append(CookieName, persona.Key, new CookieOptions { HttpOnly = true, SameSite = SameSiteMode.Strict, IsEssential = true });
            var target = LocalAuthEndpoints.SafeLocalUrl(returnUrl);
            return Results.Redirect(target);
        }).AllowAnonymous();
    }
}

public sealed class DemoAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> opts,ILoggerFactory logger,UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(opts,logger,encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var persona = DemoPersonas.Resolve(Request.Cookies[DemoPersonas.CookieName]);
        var claims = new List<Claim> { new(ClaimTypes.Name, persona.UserName), new("jcf:demo-persona", persona.Label) };
        claims.AddRange(persona.Roles.Select(r => new Claim(ClaimTypes.Role, r)));
        var principal=new ClaimsPrincipal(new ClaimsIdentity(claims,Scheme.Name));
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal,Scheme.Name)));
    }
}
