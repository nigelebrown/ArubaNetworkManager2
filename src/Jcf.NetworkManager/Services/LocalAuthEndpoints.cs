using System.Text.Encodings.Web;
using Jcf.NetworkManager.Domain;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Identity;

namespace Jcf.NetworkManager.Services;

public static class LocalAuthEndpoints
{
    private static readonly HtmlEncoder E=HtmlEncoder.Default;

    /// <summary>Only same-application relative paths are accepted as post-login destinations (no open redirect).</summary>
    public static string SafeLocalUrl(string? url)
    {
        if(string.IsNullOrEmpty(url) || url.Length>2000) return "/";
        if(url[0]!='/' || url.StartsWith("//",StringComparison.Ordinal) || url.StartsWith("/\\",StringComparison.Ordinal) || url.Contains('\\')) return "/";
        if(url.StartsWith("/account/log",StringComparison.OrdinalIgnoreCase)) return "/";
        return url;
    }
    public static void MapLocalAuth(this WebApplication app)
    {
        app.MapGet("/account/login",(HttpContext ctx,IAntiforgery antiforgery) =>
        {
            var token=E.Encode(antiforgery.GetAndStoreTokens(ctx).RequestToken??"");
            var message=ctx.Request.Query["error"]=="1" ? "Login unsuccessful. Check credentials or account status." : "";
            var returnUrl=E.Encode(SafeLocalUrl(ctx.Request.Query["ReturnUrl"].ToString()));
            var html=@"<!doctype html><html lang='en'><head><meta charset='utf-8'><meta name='viewport' content='width=device-width,initial-scale=1'>
            <title>JCF Network Manager - Sign in</title><style>
            body{font:16px system-ui;background:#0e1a2d;color:#edf4ff;min-height:100vh;display:grid;place-items:center;margin:0}
            main{max-width:360px;width:90%;padding:36px;background:#17263d;border:1px solid #344961;border-radius:14px}
            label{display:block;margin:16px 0 5px}input{width:100%;box-sizing:border-box;padding:12px;border-radius:6px;border:1px solid #71839e}
            button{width:100%;margin-top:22px;padding:14px;background:#37a5e5;color:#00172b;border:0;border-radius:7px;font-weight:700}
            small{color:#abc0d6}p{color:#ffc1c1}</style></head><body><main>
            <small>JCF - TECHNOLOGY BRANCH</small><h1>Network Manager</h1><p>__MESSAGE__</p>
            <form action='/account/login' method='post'><input type='hidden' name='__RequestVerificationToken' value='__ANTITOKEN__'><input type='hidden' name='returnUrl' value='__RETURN__'>
            <label for='u'>Username</label><input id='u' name='username' autocomplete='username' required maxlength='100'>
            <label for='p'>Password</label><input id='p' type='password' name='password' autocomplete='current-password' required>
            <button type='submit'>Sign in</button></form></main></body></html>";
            return Results.Content(html.Replace("__ANTITOKEN__",token).Replace("__RETURN__",returnUrl).Replace("__MESSAGE__",E.Encode(message)),"text/html");
        }).AllowAnonymous();

        app.MapPost("/account/login",async(HttpContext ctx,IAntiforgery antiforgery,SignInManager<OperatorUser> signin,
            UserManager<OperatorUser> users)=>
        {
            await antiforgery.ValidateRequestAsync(ctx);
            var form=await ctx.Request.ReadFormAsync();
            var name=form["username"].ToString().Trim();
            var password=form["password"].ToString();
            var target=SafeLocalUrl(form["returnUrl"].ToString());
            var failed="/account/login?error=1"+(target=="/"?"":"&ReturnUrl="+Uri.EscapeDataString(target));
            var user=await users.FindByNameAsync(name);
            if(user is null || user.IsDisabled || !(await users.GetRolesAsync(user)).Any())
                return Results.Redirect(failed);
            var result=await signin.PasswordSignInAsync(user,password,isPersistent:false,lockoutOnFailure:true);
            return Results.Redirect(result.Succeeded?target:failed);
        }).AllowAnonymous().RequireRateLimiting("login");

        app.MapGet("/account/password",(HttpContext ctx,IAntiforgery antiforgery)=>
        {
            var token=E.Encode(antiforgery.GetAndStoreTokens(ctx).RequestToken??"");
            var html=@"<!doctype html><html lang='en'><head><meta charset='utf-8'><meta name='viewport' content='width=device-width,initial-scale=1'><title>Change password</title></head>
            <body style='font:16px system-ui;max-width:440px;margin:60px auto'><h1>Change your network manager password</h1>
            <form method='post' action='/account/password'><input type='hidden' name='__RequestVerificationToken' value='__ANTITOKEN__'>
            <label>Current password<input type='password' name='oldPassword' required autocomplete='current-password'></label>
            <label>New password<input type='password' name='newPassword' required autocomplete='new-password'></label>
            <button>Update password</button></form><a href='/'>Back to dashboard</a></body></html>";
            return Results.Content(html.Replace("__ANTITOKEN__",token),"text/html");
        }).RequireAuthorization();
        app.MapPost("/account/password",async(HttpContext ctx,IAntiforgery antiforgery,UserManager<OperatorUser> users,
            SignInManager<OperatorUser> signIn)=>
        {
            await antiforgery.ValidateRequestAsync(ctx);
            var user=await users.GetUserAsync(ctx.User);
            if(user is null || user.IsDisabled) return Results.Unauthorized();
            var form=await ctx.Request.ReadFormAsync();
            var result=await users.ChangePasswordAsync(user,form["oldPassword"].ToString(),form["newPassword"].ToString());
            if(!result.Succeeded) return Results.BadRequest("Password update rejected; check current password and password policy.");
            await signIn.RefreshSignInAsync(user);
            return Results.Redirect("/");
        }).RequireAuthorization().RequireRateLimiting("login");

        app.MapGet("/account/logout",(HttpContext ctx,IAntiforgery antiforgery)=>
        {
            var token=E.Encode(antiforgery.GetAndStoreTokens(ctx).RequestToken??"");
            var html="<html><body><form method='post' action='/account/logout'><input type='hidden' name='__RequestVerificationToken' value='"+
                token+"'/><button>Confirm sign out</button></form></body></html>";
            return Results.Content(html,"text/html");
        }).RequireAuthorization();
        app.MapPost("/account/logout",async(HttpContext ctx,IAntiforgery antiforgery,SignInManager<OperatorUser> signin)=>
        {
            await antiforgery.ValidateRequestAsync(ctx);
            await signin.SignOutAsync();
            return Results.Redirect("/account/login");
        }).RequireAuthorization();
    }
}
