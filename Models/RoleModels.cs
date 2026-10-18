using System.ComponentModel.DataAnnotations;

namespace AngularCrudApi.Models;

/// <summary>Row of GET /api/roles.</summary>
public sealed class RoleListItem
{
    public int Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public string? Description { get; init; }
    public bool IsSystem { get; init; }
    public int UserCount { get; init; }
    public int PermissionCount { get; init; }
    public List<string> Permissions { get; init; } = [];
    public DateTime CreatedAtUtc { get; init; }
    public DateTime? UpdatedAtUtc { get; init; }
}

public sealed record RoleMember(int Id, string Username, string DisplayName, bool IsActive);

/// <summary>GET /api/roles/{id}.</summary>
public sealed record RoleDetailResponse(
    int Id,
    string Name,
    string? Description,
    bool IsSystem,
    string[] Permissions,
    int UserCount,
    RoleMember[] Members,
    DateTime CreatedAtUtc,
    DateTime? UpdatedAtUtc);

/// <summary>Body of POST /api/roles and PUT /api/roles/{id}.</summary>
public class RoleRequest
{
    [Required(ErrorMessage = "Name is required"), StringLength(64, ErrorMessage = "Max 64 characters"),
     RegularExpression(@"^[^,]+$", ErrorMessage = "Commas are not allowed")]
    public string Name { get; set; } = string.Empty;

    [StringLength(200, ErrorMessage = "Max 200 characters")]
    public string? Description { get; set; }

    [MaxLength(60, ErrorMessage = "At most 60 permissions")]
    public List<string> Permissions { get; set; } = new();
}
