using Microsoft.AspNetCore.Identity;
using System.ComponentModel.DataAnnotations;

namespace Jcf.NetworkManager.Domain;

public sealed class OperatorUser : IdentityUser<Guid>
{
    [MaxLength(140)] public string DisplayName { get; set; } = "";
    public bool IsDisabled { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}

public sealed class SitePermission
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public Guid LocationId { get; set; }
    [MaxLength(50)] public string Role { get; set; } = "Viewer";
    public DateTime GrantedUtc { get; set; } = DateTime.UtcNow;
    public Guid? GrantedBy { get; set; }
}
