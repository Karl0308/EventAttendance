namespace EAMS.Domain;

// Core-slice entities from the Technical Plan §4.2–§4.5 and §4.9.
// Column sets follow the per-table lists in §4 literally; see docs/adr/ADR-001 for approved drift.

/// <summary>Every §4 table has a GUID <c>Id</c> primary key (§4 conventions paragraph).</summary>
public abstract class Entity
{
    public Guid Id { get; set; } = Guid.NewGuid();
}

/// <summary>
/// §4 tables whose column list includes <c>CreatedAt</c> / <c>UpdatedAt</c>.
/// Not every table carries these — junctions and append-only tables do not (see §4.7, §4.8, §4.11–§4.13).
/// </summary>
public abstract class AuditableEntity : Entity
{
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

// §4.2 Schools
public class School : AuditableEntity
{
    public string Name { get; set; } = "";
    public string Code { get; set; } = "";
    public string? Address { get; set; }
    public string? ContactEmail { get; set; }
    public string? LogoUrl { get; set; }
    public string TimeZone { get; set; } = "Asia/Manila";
    public bool IsActive { get; set; } = true;

    public ICollection<Student> Students { get; set; } = new List<Student>();
    public ICollection<Event> Events { get; set; } = new List<Event>();
}

// §4.3 Students
public class Student : AuditableEntity
{
    public Guid SchoolId { get; set; }
    public School? School { get; set; }

    public string StudentNumber { get; set; } = "";
    public string FirstName { get; set; } = "";
    public string? MiddleName { get; set; }
    public string LastName { get; set; } = "";

    /// <summary>
    /// The institutional address. The roster's <c>USA_EMAIL</c> feeds this one, not <c>EMAIL_ID</c>.
    ///
    /// <para>
    /// <b>Which of the source's two e-mail columns is "the" e-mail is a decision, not a detail.</b>
    /// <c>USA_EMAIL</c> is <c>@usa.edu.ph</c> on all 536 sample rows and unique per student;
    /// <c>EMAIL_ID</c> is a personal address on 164 of them, mostly gmail. This column is what the
    /// system will send to and match on, so it has to be the one the institution controls and can
    /// vouch for — a personal address is neither, and it is also the one a student changes without
    /// telling anyone.
    /// </para>
    /// </summary>
    public string? Email { get; set; }

    /// <summary>
    /// The personal address from the roster's <c>EMAIL_ID</c> column. Not in Technical Plan §4.3 — an
    /// additive column, recorded as drift.
    ///
    /// <para>
    /// <b>Why keep it at all rather than drop the column on the floor.</b> It is populated for 164 of
    /// the sample's rows and it is the only contact route for a student whose institutional mailbox they
    /// do not read — which, for an attendance system that will eventually notify people, is the whole
    /// point of having an address. Discarding it at import would mean re-importing the entire roster to
    /// get it back once someone asks.
    /// </para>
    ///
    /// <para>
    /// <b>Deliberately not unique and not a login identifier.</b> Shared family addresses are ordinary,
    /// and the moment this becomes a lookup key it becomes an authentication surface for an
    /// unverified, self-asserted value.
    /// </para>
    /// </summary>
    public string? AlternateEmail { get; set; }

    /// <summary>
    /// <b>DERIVED READ-ONLY CACHE. NOT A SOURCE OF TRUTH, AND NEVER A JOIN KEY.</b> (ADR-001 D-2,
    /// in force since the academic tables landed.)
    ///
    /// <para>
    /// <b>The rule, because getting it wrong is silent and quantified.</b> Any report, denominator,
    /// event audience, or filter that selects students by course or section <b>must join
    /// <c>Enrollments</c> → <c>CourseOfferings</c></b> (or <c>StudentTermRecords</c> for the student's
    /// own programme/college/year), never these three columns. In the real sample roster <b>12 of 52
    /// students — 23% — sit in more than one section</b>, and a single-valued column can only name
    /// one of them. A section-filtered query reading <c>Section</c> therefore misses roughly a quarter
    /// of the students it should return, and it does so by returning a plausible, non-empty answer:
    /// there is no error, no empty grid, and nothing to notice. That is the entire reason the academic
    /// layer exists.
    /// </para>
    ///
    /// <para>
    /// <b>They survive only because the SPA binds them.</b> <c>StudentDto</c> exposes
    /// <c>course</c>/<c>yearLevel</c>/<c>section</c> and the students grid renders and filters on
    /// them; dropping populated columns is also forbidden outright. So they stay as a denormalization
    /// for list rendering, and writes are refused — see <c>EamsDbContext</c>'s academic-cache guard,
    /// which throws on any attempt to modify one outside the refresh path. Writes go to
    /// <c>Enrollments</c> / <c>StudentTermRecords</c>; the cache is refreshed from them, and
    /// <see cref="AcademicCacheUpdatedAt"/> says when.
    /// </para>
    /// </summary>
    public string? Course { get; set; }

    /// <inheritdoc cref="Course"/>
    public string? YearLevel { get; set; }

    /// <inheritdoc cref="Course"/>
    public string? Section { get; set; }

    public string? Gender { get; set; }
    public string? PhotoUrl { get; set; }
    public string Status { get; set; } = "Active"; // Active/Inactive/Graduated
    public string? SisExternalId { get; set; }
    public DateTime? LastSyncedAt { get; set; }
    public bool IsDeleted { get; set; }

    /// <summary>
    /// When <see cref="Course"/>/<see cref="YearLevel"/>/<see cref="Section"/> were last refreshed
    /// from the academic tables. <c>null</c> means never — which is every row today, because no
    /// refresher exists yet and the columns still hold whatever the §4 seed or import wrote.
    ///
    /// <para>
    /// It is here now rather than with the refresher because staleness in a denormalization is
    /// invisible without it: a UI that shows a section has no way to say "as of when", and the first
    /// question after a wrong-looking grid is exactly that.
    /// </para>
    /// </summary>
    public DateTime? AcademicCacheUpdatedAt { get; set; }

    /// <summary>
    /// The three ADR-001 D-2 cache columns, named once so the write guard and any future refresher
    /// cannot disagree about which columns are derived. Property names, matched by
    /// <c>ChangeTracker</c>.
    /// </summary>
    public static readonly IReadOnlyList<string> DerivedAcademicPropertyNames =
        [nameof(Course), nameof(YearLevel), nameof(Section)];

    public ICollection<RfidCard> Cards { get; set; } = new List<RfidCard>();
    public ICollection<Enrollment> Enrollments { get; set; } = new List<Enrollment>();
    public ICollection<StudentTermRecord> TermRecords { get; set; } = new List<StudentTermRecord>();

    public string FullName => string.Join(' ',
        new[] { FirstName, MiddleName, LastName }.Where(s => !string.IsNullOrWhiteSpace(s)));
}

// §4.4 RfidCards
public class RfidCard : AuditableEntity
{
    // ADR-001 D-3: denormalized from Student so UNIQUE(SchoolId, CardUid) WHERE IsActive = 1
    // can be expressed as a single filtered index. Must stay equal to Student.SchoolId.
    public Guid SchoolId { get; set; }
    public School? School { get; set; }

    public Guid StudentId { get; set; }
    public Student? Student { get; set; }

    public string CardUid { get; set; } = ""; // canonical uppercase hex — see CardUid.Normalize
    public string? Label { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime IssuedAt { get; set; } = DateTime.UtcNow;
    public DateTime? DeactivatedAt { get; set; }
}

// §4.5 Events
public class Event : AuditableEntity
{
    public Guid SchoolId { get; set; }
    public School? School { get; set; }

    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public string? Location { get; set; }
    public DateTime StartAt { get; set; }
    public DateTime EndAt { get; set; }
    public string AttendanceMode { get; set; } = "Single"; // see AttendanceMode (DomainValues.cs)
    public int GraceMinutes { get; set; }
    public bool RequireRegistration { get; set; }
    public string Status { get; set; } = "Draft"; // see EventStatus (DomainValues.cs)
    public Guid? OrganizerUserId { get; set; }
    public User? OrganizerUser { get; set; }
    public bool IsDeleted { get; set; }

    public ICollection<AttendanceRecord> AttendanceRecords { get; set; } = new List<AttendanceRecord>();
    public ICollection<EventSchedule> Schedules { get; set; } = new List<EventSchedule>();
}

// §4.9 AttendanceRecords
public class AttendanceRecord : AuditableEntity
{
    public Guid EventId { get; set; }
    public Event? Event { get; set; }

    public Guid? OccurrenceId { get; set; }
    public EventSchedule? Occurrence { get; set; }

    public Guid StudentId { get; set; }
    public Student? Student { get; set; }

    public Guid? RfidCardId { get; set; }
    public RfidCard? RfidCard { get; set; }

    public DateTime? CheckInAt { get; set; }
    public DateTime? CheckOutAt { get; set; }
    // The valid sets live in AttendanceStatus / CaptureMethod (DomainValues.cs) — that is what a
    // caller validates against. The literals stay spelled out here because these properties shadow
    // the same-named static classes inside this type, so naming the constant would mean writing it
    // fully qualified for no gain.
    public string Status { get; set; } = "Present"; // see AttendanceStatus
    public string CaptureMethod { get; set; } = "Rfid"; // see CaptureMethod

    public Guid? DeviceId { get; set; }
    public Device? Device { get; set; }

    public string? DeviceTapId { get; set; } // client-generated, idempotency key
    public string? Notes { get; set; }

    public Guid? RecordedByUserId { get; set; }
    public User? RecordedByUser { get; set; }
}
