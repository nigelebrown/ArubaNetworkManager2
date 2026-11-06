using System.Reflection;

namespace Jcf.NetworkManager.Services;

/// <summary>Single source of truth for the displayed release version (from the project's &lt;Version&gt;).</summary>
public static class AppInfo
{
    public static readonly string Version =
        (typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0")
        .Split('+')[0];
}
