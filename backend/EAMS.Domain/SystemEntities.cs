namespace EAMS.Domain;

// Technical Plan §4.13 — AuditLogs & SystemSettings.

/// <summary>Append-only. §4.13's column list carries <c>CreatedAt</c> but no <c>UpdatedAt</c>.</summary>
public class AuditLog : Entity
{
    public Guid? UserId { get; set; }
    public User? User { get; set; }

    public string Action { get; set; } = ""; // e.g. Event.Updated
    public string? EntityType { get; set; }
    public Guid? EntityId { get; set; }
    public string? Changes { get; set; } // nvarchar(max) JSON before/after
    public string? IpAddress { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>Key is unique per school; a NULL <c>SchoolId</c> is the global scope.</summary>
public class SystemSetting : Entity
{
    public Guid? SchoolId { get; set; }
    public School? School { get; set; }

    public string Key { get; set; } = "";
    public string? Value { get; set; } // nvarchar(max)
    public string? DataType { get; set; }
    public string? Description { get; set; }
}
