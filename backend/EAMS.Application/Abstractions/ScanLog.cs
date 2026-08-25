namespace EAMS.Application.Abstractions;

/// <summary>
/// The vocabulary of the per-event scan log: the <c>AuditLogs</c> rows written for scans the server
/// could not resolve to a student.
///
/// <para>
/// <b>Constants rather than literals, because the writer and the reader are in different assemblies.</b>
/// <c>AttendanceService</c> writes these rows and <c>EventService</c> queries them back; a string
/// typed twice is a report that silently returns nothing the day one of them is edited, with no
/// compiler error and no failing test unless a test happens to span both.
/// </para>
/// </summary>
public static class ScanLog
{
    /// <summary>
    /// <c>AuditLog.EntityType</c> for a scan-log row. The event is the entity the scan is filed
    /// under, which is what makes <c>IX_AuditLogs_Entity</c> serve the report.
    /// </summary>
    public const string EventEntityType = "Event";

    /// <summary>
    /// <c>AuditLog.Action</c> for a scan whose card matched no active card in this school.
    ///
    /// <para>
    /// Namespaced like the auth actions (<c>auth.admin.created</c>) so that an operator filtering the
    /// audit trail by prefix gets a coherent set rather than a keyword search.
    /// </para>
    /// </summary>
    public const string UnresolvedAction = "attendance.scan.unresolved";
}
