namespace EAMS.Domain;

// Technical Plan §4.13 — AuditLogs & SystemSettings.

/// <summary>Append-only. §4.13's column list carries <c>CreatedAt</c> but no <c>UpdatedAt</c>.</summary>
public class AuditLog : Entity
{
    /// <summary>
    /// The tenant the audited action happened in. <b>Not in §4.13 — an additive column, recorded as
    /// drift</b>, and nullable because a system-generated entry belongs to no school.
    ///
    /// <para>
    /// <b>It exists because <c>EamsDbContext</c> explicitly could not decide this table's query filter
    /// without it.</b> Every other tenant-scoped table owns a <c>SchoolId</c>; this one's only link to
    /// a school was a <em>nullable</em> <c>UserId</c>, so a filter reached through the user would have
    /// hidden every entry that had no user — which is precisely the set of entries a security review
    /// most wants to see. The recorded decision was to defer the filter "to when §11 lands", and a
    /// deferral over a column that does not exist is not a deferral, it is a rewrite waiting to
    /// happen: the filter cannot be added later without a backfill that has no source to backfill
    /// from.
    /// </para>
    ///
    /// <para>
    /// <b>The filter itself is still not written, and that is deliberate.</b> This phase adds the
    /// column so that rows written from here on can carry a tenant; turning it into a query filter
    /// changes what existing callers see and belongs with the rest of §11 enforcement. A NULL here
    /// means "not attributed to a school" and must stay visible under every tenant when that filter
    /// arrives, the same way <c>SystemSettings</c>' NULL means the global scope.
    /// </para>
    /// </summary>
    public Guid? SchoolId { get; set; }
    public School? School { get; set; }

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
