namespace EAMS.Domain;

// Technical Plan §4.11 — RBAC tables.
// These are plain application tables. §11 uses ASP.NET Identity only for the password *hasher*;
// Identity's own schema (AspNetUsers etc.) is deliberately not used.

public class User : AuditableEntity
{
    public Guid SchoolId { get; set; }
    public School? School { get; set; }

    public string Email { get; set; } = "";
    public string PasswordHash { get; set; } = ""; // nvarchar(max) per §4.11
    public string FullName { get; set; } = "";
    public string? Phone { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime? LastLoginAt { get; set; }
    public string? RefreshTokenHash { get; set; } // nvarchar(max) per §4.11

    public ICollection<UserRole> UserRoles { get; set; } = new List<UserRole>();
}

public class Role : Entity
{
    public string Name { get; set; } = ""; // SuperAdmin/SchoolAdmin/Organizer/Viewer
    public string? Description { get; set; }
    public bool IsSystem { get; set; }

    public ICollection<UserRole> UserRoles { get; set; } = new List<UserRole>();
    public ICollection<RolePermission> RolePermissions { get; set; } = new List<RolePermission>();
}

public class Permission : Entity
{
    public string Code { get; set; } = ""; // e.g. students.read, events.write, attendance.capture
    public string? Description { get; set; }

    public ICollection<RolePermission> RolePermissions { get; set; } = new List<RolePermission>();
}

public class UserRole : Entity
{
    public Guid UserId { get; set; }
    public User? User { get; set; }

    public Guid RoleId { get; set; }
    public Role? Role { get; set; }
}

public class RolePermission : Entity
{
    public Guid RoleId { get; set; }
    public Role? Role { get; set; }

    public Guid PermissionId { get; set; }
    public Permission? Permission { get; set; }
}
