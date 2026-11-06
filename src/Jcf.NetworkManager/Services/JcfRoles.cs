using System.Security.Claims;

namespace Jcf.NetworkManager.Services;

/// <summary>
/// Single source of truth for role names and the role sets behind each authorization policy.
/// Blazor pages and REST endpoints must use these sets so the UI never allows less, or more,
/// than the API for the same action.
/// </summary>
public static class JcfRoles
{
    public const string Viewer = "Viewer";
    public const string NetworkOperator = "NetworkOperator";
    public const string WirelessEngineer = "WirelessEngineer";
    public const string SwitchEngineer = "SwitchEngineer";
    public const string ChangeApprover = "ChangeApprover";
    public const string Auditor = "Auditor";
    public const string PlatformAdmin = "PlatformAdmin";

    public static readonly string[] All = [Viewer, NetworkOperator, WirelessEngineer, SwitchEngineer, ChangeApprover, Auditor, PlatformAdmin];
    /// <summary>May register/probe devices, run analysis, submit proposals and execute approved changes.</summary>
    public static readonly string[] Operators = [NetworkOperator, WirelessEngineer, SwitchEngineer, PlatformAdmin];
    /// <summary>May review change proposals and optimization recommendations.</summary>
    public static readonly string[] Approvers = [ChangeApprover, PlatformAdmin];
    public static readonly string[] Admins = [PlatformAdmin];

    public static bool IsOperator(ClaimsPrincipal user) => Operators.Any(user.IsInRole);
    public static bool IsApprover(ClaimsPrincipal user) => Approvers.Any(user.IsInRole);
    public static bool IsAdmin(ClaimsPrincipal user) => Admins.Any(user.IsInRole);

    public static void RequireOperator(ClaimsPrincipal user)
    { if (!IsOperator(user)) throw new UnauthorizedAccessException("Operator or engineer role required."); }
    public static void RequireApprover(ClaimsPrincipal user)
    { if (!IsApprover(user)) throw new UnauthorizedAccessException("ChangeApprover role required."); }
    public static void RequireAdmin(ClaimsPrincipal user)
    { if (!IsAdmin(user)) throw new UnauthorizedAccessException("Platform administrator required."); }
}
