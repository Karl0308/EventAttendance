using EAMS.Domain;
using Microsoft.EntityFrameworkCore;

namespace EAMS.Infrastructure.Data;

/// <summary>
/// Dev-only convenience data. The guard is the school row: on a persistent database the second and
/// every later start finds it and returns without writing, so seeding stays a no-op once applied.
/// </summary>
internal static class SeedData
{
    public static async Task InitializeAsync(EamsDbContext db, CancellationToken ct = default)
    {
        if (await db.Schools.AnyAsync(ct)) return;

        var school = new School
        {
            Name = "University of San Agustin",
            Code = "USA",
            Address = "General Luna St, Iloilo City",
            ContactEmail = "cicss@usa.edu.ph",
            TimeZone = "Asia/Manila",
        };
        db.Schools.Add(school);

        var seed = new (string No, string First, string? Middle, string Last, string Course, string Year, string Section, string Gender, string Uid)[]
        {
            ("2023-0001", "Maria",   "Reyes",   "Santos",     "BSIT", "3rd Year", "A", "Female", "04A1B2C3"),
            ("2023-0002", "Juan",    "Cruz",    "Dela Cruz",  "BSIT", "3rd Year", "A", "Male",   "04D4E5F6"),
            ("2023-0003", "Andrea",  null,      "Lim",        "BSCS", "2nd Year", "B", "Female", "04A7B8C9"),
            ("2023-0004", "Miguel",  "Tan",     "Gonzales",   "BSCS", "2nd Year", "B", "Male",   "0411A2B3"),
            ("2023-0005", "Sofia",   "Villa",   "Ramos",      "BSIT", "1st Year", "C", "Female", "04C4D5E6"),
            ("2023-0006", "Gabriel", null,      "Flores",     "BSA",  "4th Year", "A", "Male",   "04F7081A"),
            ("2023-0007", "Isabella","Marie",   "Aquino",     "BSN",  "2nd Year", "A", "Female", "041B2C3D"),
            ("2023-0008", "Diego",   "Luis",    "Mendoza",    "BSIT", "3rd Year", "A", "Male",   "044E5F60"),
        };

        var students = new List<Student>();
        foreach (var s in seed)
        {
            var student = new Student
            {
                SchoolId = school.Id,
                StudentNumber = s.No,
                FirstName = s.First,
                MiddleName = s.Middle,
                LastName = s.Last,
                Email = $"{s.No}@usa.edu.ph",
                Course = s.Course,
                YearLevel = s.Year,
                Section = s.Section,
                Gender = s.Gender,
                // ADR-001 "Accepted Context": a synthetic value, not a real Mastersoft key.
                SisExternalId = $"SIS-{s.No}",
            };
            // SchoolId is denormalized onto the card (ADR-001 D-3) and must match the owner's.
            student.Cards.Add(new RfidCard { SchoolId = school.Id, CardUid = s.Uid, Label = "Primary ID" });
            students.Add(student);
        }
        db.Students.AddRange(students);

        var now = DateTime.UtcNow;
        var openEvent = new Event
        {
            SchoolId = school.Id,
            Name = "University Convocation 2026",
            Description = "Opening convocation for the academic year.",
            Location = "USA Gymnasium",
            StartAt = now.AddMinutes(-30),
            EndAt = now.AddHours(2),
            AttendanceMode = AttendanceMode.Single,
            GraceMinutes = 15,
            Status = EventStatus.Open,
        };
        var pastEvent = new Event
        {
            SchoolId = school.Id,
            Name = "IT Week Seminar",
            Description = "Guest lecture on cloud computing.",
            Location = "AVR 2",
            StartAt = now.AddDays(-3),
            EndAt = now.AddDays(-3).AddHours(3),
            AttendanceMode = AttendanceMode.Single,
            GraceMinutes = 10,
            Status = EventStatus.Closed,
        };
        db.Events.AddRange(openEvent, pastEvent);

        // A few taps already recorded on the open event.
        db.AttendanceRecords.AddRange(
            new AttendanceRecord
            {
                EventId = openEvent.Id, StudentId = students[0].Id,
                RfidCardId = students[0].Cards.First().Id,
                CheckInAt = now.AddMinutes(-25), Status = AttendanceStatus.Present,
                CaptureMethod = CaptureMethod.Rfid,
                DeviceTapId = Guid.NewGuid().ToString(),
            },
            new AttendanceRecord
            {
                EventId = openEvent.Id, StudentId = students[1].Id,
                RfidCardId = students[1].Cards.First().Id,
                CheckInAt = now.AddMinutes(-5), Status = AttendanceStatus.Late,
                CaptureMethod = CaptureMethod.Rfid,
                DeviceTapId = Guid.NewGuid().ToString(),
            }
        );

        await db.SaveChangesAsync(ct);
    }
}
