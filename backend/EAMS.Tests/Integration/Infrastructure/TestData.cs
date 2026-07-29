using EAMS.Domain;

namespace EAMS.Tests.Integration.Infrastructure;

/// <summary>
/// Entity builders with valid-by-default values, so each test states only the field it is about.
/// A tap test that has to spell out a school's contact e-mail buries its own subject.
///
/// <para>
/// Nothing here is shared between tests — every call produces new identities, and the database is
/// emptied between tests regardless. There is deliberately no "standard fixture" of rows: shared
/// arrange state is how a test starts depending on a value some other test happened to write.
/// </para>
/// </summary>
internal static class TestData
{
    /// <summary>
    /// A fixed instant rather than <c>DateTime.UtcNow</c>. Grace-period assertions are arithmetic
    /// on this value, and a clock that moves between arrange and act turns a boundary test into a
    /// coin flip that fails once a month.
    /// </summary>
    public static readonly DateTime Now = new(2026, 7, 28, 9, 0, 0, DateTimeKind.Utc);

    public static School NewSchool(string code = "USA") => new()
    {
        Name = $"Test School {code}",
        Code = code,
        TimeZone = "Asia/Manila",
    };

    public static Student NewStudent(
        Guid schoolId, string studentNumber = "2023-0001",
        string firstName = "Maria", string? middleName = "Reyes", string lastName = "Santos",
        string course = "BSIT", string status = "Active") => new()
    {
        SchoolId = schoolId,
        StudentNumber = studentNumber,
        FirstName = firstName,
        MiddleName = middleName,
        LastName = lastName,
        Email = $"{studentNumber}@test.local",
        Course = course,
        YearLevel = "3rd Year",
        Section = "A",
        Status = status,
    };

    /// <summary>
    /// Cards carry the school denormalized from their student (ADR-001 D-3) — the filtered unique
    /// index is <c>(SchoolId, CardUid) WHERE IsActive = 1</c>, so a card built with the wrong school
    /// would silently sidestep the constraint the schema tests are asserting on.
    /// </summary>
    public static RfidCard NewCard(
        Guid schoolId, Guid studentId, string cardUid = "04A7B8C9", bool isActive = true) => new()
    {
        SchoolId = schoolId,
        StudentId = studentId,
        CardUid = cardUid,
        Label = "Primary ID",
        IsActive = isActive,
        IssuedAt = Now,
        DeactivatedAt = isActive ? null : Now,
    };

    public static Event NewEvent(
        Guid schoolId, string status = "Open", string attendanceMode = "Single",
        int graceMinutes = 15, DateTime? startAt = null) => new()
    {
        SchoolId = schoolId,
        Name = "University Convocation 2026",
        Location = "USA Gymnasium",
        StartAt = startAt ?? Now,
        EndAt = (startAt ?? Now).AddHours(3),
        AttendanceMode = attendanceMode,
        GraceMinutes = graceMinutes,
        Status = status,
    };

    /// <summary>
    /// An event whose window contains the <em>real</em> clock rather than <see cref="Now"/>.
    ///
    /// <para>
    /// <b>Use this whenever the tap under test is stamped by the server</b> — an HTTP payload that omits
    /// <c>tappedAt</c>, which is what the mobile client sends and therefore what most of the contract
    /// tests post. Phase 4c's D-36 rejects a tap whose <c>tappedAt</c> falls outside
    /// <c>[StartAt − 60min, EndAt + 60min]</c>, and <see cref="Now"/> is a <em>fixed</em> instant: the
    /// day after it passes, an event anchored on it has a window that today's clock is nowhere near, so
    /// every server-stamped tap against it is correctly refused. That is the rule working, not a
    /// fixture problem — but it makes <see cref="NewEvent"/> the wrong builder for those tests.
    /// </para>
    ///
    /// <para>
    /// <b>Use <see cref="NewEvent"/> when the test supplies an explicit <c>tappedAt</c> derived from
    /// <see cref="Now"/>,</b> which is every grace-boundary test — those need a frozen clock, because
    /// arithmetic against a moving one turns a boundary assertion into a coin flip. The two builders
    /// exist because the two needs are genuinely opposite and no single anchor satisfies both:
    /// <see cref="Now"/> cannot be moved forward to meet the real clock without making
    /// <c>Now.AddHours(2)</c> a tap in the future, which D-36 also refuses.
    /// </para>
    ///
    /// <para>
    /// <b>Started five minutes ago by default, so a server-stamped tap is <c>Present</c></b> — inside
    /// the event proper rather than in the window's slack, and inside the default 15-minute grace. The
    /// earlier version of this builder started the event <em>thirty</em> minutes ago while claiming the
    /// grace still distinguished Present from Late, which was simply false: every server-stamped tap on
    /// it was <c>Late</c>, always. Nothing asserted status against it, so nothing was broken — but the
    /// first test written as "tap, assert Present" would have failed in a way that reads as a
    /// grace-period bug rather than as a fixture choice, which is the expensive kind of wrong comment.
    /// </para>
    ///
    /// <para>
    /// Pass a larger <paramref name="startedMinutesAgo"/> than <paramref name="graceMinutes"/> when
    /// <c>Late</c> is what the test wants.
    /// </para>
    /// </summary>
    public static Event NewLiveEvent(
        Guid schoolId, string status = "Open", string attendanceMode = "Single",
        int graceMinutes = 15, int startedMinutesAgo = 5) =>
        NewEvent(schoolId, status, attendanceMode, graceMinutes,
            startAt: DateTime.UtcNow.AddMinutes(-startedMinutesAgo));

    public static Device NewDevice(Guid schoolId, string name) => new()
    {
        SchoolId = schoolId,
        Name = name,
        DeviceType = "Mobile",
    };

    public static StudentGroup NewGroup(Guid schoolId, string name = "BSIT 3-A") => new()
    {
        SchoolId = schoolId,
        Name = name,
        Type = "Section",
    };

    public static User NewUser(Guid schoolId, string email = "organizer@test.local") => new()
    {
        SchoolId = schoolId,
        Email = email,
        PasswordHash = "not-a-real-hash",
        FullName = "Test Organizer",
    };

    // ------------------------------------------------------------- ADR-001 D-1 academic layer
    //
    // Every builder below derives its *Key column through EAMS.Domain.AcademicKey rather than
    // hard-coding a normalized string. That is on purpose: it means the fixtures exercise the same
    // single normalization rule the Phase 2 importer will, so a test cannot pass against a key the
    // production path would never produce.

    public static Term NewTerm(
        Guid schoolId, string code = "2025-2026-1", bool isCurrent = true) => new()
    {
        SchoolId = schoolId,
        Code = code,
        SchoolYear = "2025-2026",
        Semester = "1st Semester",
        IsCurrent = isCurrent,
    };

    public static College NewCollege(
        Guid schoolId, string name = "College of Criminal Justice", string? code = "CCJ") => new()
    {
        SchoolId = schoolId,
        Name = name,
        NameKey = AcademicKey.NormalizeOrUnspecified(name),
        Code = code,
    };

    public static AcademicProgram NewProgram(
        Guid schoolId, Guid collegeId, string code = "BSCRIM") => new()
    {
        SchoolId = schoolId,
        CollegeId = collegeId,
        Code = code,
        CodeKey = AcademicKey.NormalizeOrUnspecified(code),
        Name = code,
    };

    public static Course NewCourse(
        Guid schoolId, string code = "SSCI 7", string? title = "Introduction to Criminology",
        Guid? collegeId = null) => new()
    {
        SchoolId = schoolId,
        CollegeId = collegeId,
        Code = code,
        CodeKey = AcademicKey.NormalizeOrUnspecified(code),
        Title = title,
    };

    /// <summary>
    /// Defaults to the roster's <c>'TO BE ANNOUNCE'</c> sentinel, because that is what 66 of the
    /// sample's rows actually carry and a fixture that pretends otherwise tests a tidier world than
    /// the one being built for.
    /// </summary>
    public static Instructor NewInstructor(
        Guid schoolId, string displayName = "TO BE ANNOUNCE", string? externalId = null) => new()
    {
        SchoolId = schoolId,
        DisplayName = displayName,
        NameKey = AcademicKey.NormalizeOrUnspecified(displayName),
        ExternalId = externalId,
    };

    /// <summary>
    /// <paramref name="sectionName"/> may be null or blank — 39 sample rows are — and the key then
    /// falls to <see cref="AcademicKey.Unspecified"/> exactly as the importer's will.
    /// </summary>
    public static CourseOffering NewOffering(
        Guid termId, Guid courseId, string? sectionName = "BSFS 2-A") => new()
    {
        TermId = termId,
        CourseId = courseId,
        SectionKey = AcademicKey.NormalizeOrUnspecified(sectionName),
        SectionName = string.IsNullOrWhiteSpace(sectionName) ? null : sectionName,
    };

    public static Enrollment NewEnrollment(Guid studentId, Guid courseOfferingId) => new()
    {
        StudentId = studentId,
        CourseOfferingId = courseOfferingId,
    };

    public static StudentTermRecord NewTermRecord(
        Guid studentId, Guid termId, Guid? programId = null, Guid? collegeId = null,
        string? yearLevel = "2nd Year", string? homeSection = "BSFS 2-A") => new()
    {
        StudentId = studentId,
        TermId = termId,
        ProgramId = programId,
        CollegeId = collegeId,
        YearLevel = yearLevel,
        HomeSectionKey = homeSection is null ? null : AcademicKey.NormalizeOrUnspecified(homeSection),
        HomeSectionName = homeSection,
    };
}
