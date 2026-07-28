namespace EAMS.Domain;

/// <summary>
/// Thrown when something tries to write one of the ADR-001 D-2 derived cache columns
/// (<c>Students.Course</c> / <c>YearLevel</c> / <c>Section</c>) outside the refresh path.
///
/// <para>
/// <b>Why this is an exception and not a validation result.</b> A caller cannot recover from it by
/// sending different input — there is no correct way to set a student's section on the student row.
/// The correct action is to write an <c>Enrollment</c> or a <c>StudentTermRecord</c> instead, which is
/// a different call to a different table. So it is a programming error, and the loudest possible
/// failure is the cheapest one: without it the write would succeed, the cache would silently disagree
/// with <c>Enrollments</c>, and the divergence would surface months later as a report that is quietly
/// wrong for the 23% of students who sit in more than one section.
/// </para>
///
/// <para>
/// Lives in the domain rather than in Infrastructure so any layer can name it in a <c>catch</c>, and
/// so it is next to the entity whose rule it enforces.
/// </para>
/// </summary>
public sealed class AcademicCacheWriteException : InvalidOperationException
{
    public AcademicCacheWriteException(string propertyName, string studentNumber)
        : base($"Students.{propertyName} is a derived read-only cache (ADR-001 D-2) and cannot be " +
               $"written directly — the attempted change was on student '{studentNumber}'. It is " +
               "single-valued, and 12 of the 52 students in the real roster sit in more than one " +
               "section, so it cannot represent them and must never be a source of truth or a join " +
               "key. Write the academic tables instead: Enrollments (student × CourseOffering) for " +
               "sections and courses, StudentTermRecords for programme, college and year level. The " +
               "cache is refreshed from those, only from inside " +
               "EamsDbContext.BeginAcademicCacheRefresh().")
    {
        PropertyName = propertyName;
        StudentNumber = studentNumber;
    }

    /// <summary>Which of the three cache columns was written.</summary>
    public string PropertyName { get; }

    /// <summary>The <c>Students.StudentNumber</c> of the row that was being changed.</summary>
    public string StudentNumber { get; }
}
