namespace EAMS.Api.Domain;

// Core-slice entities from the Technical Plan §4 (Schools, Students, RfidCards, Events, AttendanceRecords).
// Audit columns kept minimal for the mock build.

public abstract class AuditableEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public class School : AuditableEntity
{
    public string Name { get; set; } = "";
    public string Code { get; set; } = "";
    public string? Address { get; set; }
    public string? ContactEmail { get; set; }
    public string TimeZone { get; set; } = "Asia/Manila";
    public bool IsActive { get; set; } = true;

    public ICollection<Student> Students { get; set; } = new List<Student>();
    public ICollection<Event> Events { get; set; } = new List<Event>();
}

public class Student : AuditableEntity
{
    public Guid SchoolId { get; set; }
    public School? School { get; set; }

    public string StudentNumber { get; set; } = "";
    public string FirstName { get; set; } = "";
    public string? MiddleName { get; set; }
    public string LastName { get; set; } = "";
    public string? Email { get; set; }
    public string? Course { get; set; }
    public string? YearLevel { get; set; }
    public string? Section { get; set; }
    public string? Gender { get; set; }
    public string Status { get; set; } = "Active"; // Active/Inactive/Graduated
    public string? SisExternalId { get; set; }
    public bool IsDeleted { get; set; }

    public ICollection<RfidCard> Cards { get; set; } = new List<RfidCard>();

    public string FullName => string.Join(' ',
        new[] { FirstName, MiddleName, LastName }.Where(s => !string.IsNullOrWhiteSpace(s)));
}

public class RfidCard : AuditableEntity
{
    public Guid StudentId { get; set; }
    public Student? Student { get; set; }

    public string CardUid { get; set; } = ""; // canonical uppercase hex
    public string? Label { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime IssuedAt { get; set; } = DateTime.UtcNow;
    public DateTime? DeactivatedAt { get; set; }
}

public class Event : AuditableEntity
{
    public Guid SchoolId { get; set; }
    public School? School { get; set; }

    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public string? Location { get; set; }
    public DateTime StartAt { get; set; }
    public DateTime EndAt { get; set; }
    public string AttendanceMode { get; set; } = "Single"; // Single / TimeInOut
    public int GraceMinutes { get; set; }
    public bool RequireRegistration { get; set; }
    public string Status { get; set; } = "Draft"; // Draft/Open/Closed/Cancelled
    public bool IsDeleted { get; set; }

    public ICollection<AttendanceRecord> AttendanceRecords { get; set; } = new List<AttendanceRecord>();
}

public class AttendanceRecord : AuditableEntity
{
    public Guid EventId { get; set; }
    public Event? Event { get; set; }

    public Guid StudentId { get; set; }
    public Student? Student { get; set; }

    public Guid? RfidCardId { get; set; }

    public DateTime? CheckInAt { get; set; }
    public DateTime? CheckOutAt { get; set; }
    public string Status { get; set; } = "Present"; // Present/Late/Absent/Excused
    public string CaptureMethod { get; set; } = "Rfid"; // Rfid/Manual/Import

    public Guid? DeviceId { get; set; }
    public string? DeviceTapId { get; set; } // client-generated, idempotency key
    public string? Notes { get; set; }
}
