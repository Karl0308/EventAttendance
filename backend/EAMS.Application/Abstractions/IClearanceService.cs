using EAMS.Application.Dtos;

namespace EAMS.Application.Abstractions;

/// <summary>
/// <b>The Clearance Checker (Clearance-Checker-Module.docx).</b> Given a student, show the events they
/// were eligible for and how they attended each — Attended, Missed, Excused or Late. <b>No clearance
/// status is computed</b>; the administrator interprets the results.
///
/// <para>
/// "Eligible for" is every event where the student is in the audience — attached individually or through a
/// section/group, and every frozen event whose materialized roster names them — regardless of whether
/// they turned up. An event with no attendance record for the student reads as Missed. Cancelled and
/// soft-deleted events are excluded: a cancelled event was not held, so "missed" would be a false
/// accusation.
/// </para>
///
/// <para>
/// Each report fetch writes a <c>clearance.check</c> audit row (the spec's "audit log of clearance
/// checks"), attributed to the current user.
/// </para>
/// </summary>
public interface IClearanceService
{
    /// <summary>
    /// The clearance report for one student, or null when this tenant has no such (non-deleted) student.
    /// </summary>
    /// <param name="dateFrom">Optional lower bound (inclusive) on event start date.</param>
    /// <param name="dateTo">Optional upper bound (inclusive) on event start date.</param>
    Task<ClearanceReportDto?> GetReportAsync(
        Guid studentId, DateTime? dateFrom, DateTime? dateTo, CancellationToken ct = default);
}
