using System.Net;
using System.Net.Http.Json;
using System.Text;
using EAMS.Api.Reports;
using EAMS.Application.Abstractions;
using EAMS.Domain;
using EAMS.Tests.Integration.Infrastructure;
using Xunit;

namespace EAMS.Tests.Integration;

/// <summary>
/// Client job order personnelimport.docx: the personnel export opened as <c>Last | Name | First | Name</c>
/// because the client's spreadsheet reader treats SPACE as a delimiter as well as comma, and our cells were
/// unquoted. AC: in every CSV export, a header or data cell containing a space stays ONE column even when
/// the reader also splits on spaces. Each test reads the response the way that reader does (<see cref="Read"/>)
/// and pins the header row, and the data row where a spaced value is arranged, to exact cells.
/// </summary>
[Collection(DatabaseCollection.Name)]
public class CsvExportSpaceSplittingTests : IntegrationTest
{
    public CsvExportSpaceSplittingTests(SqlServerFixture sql) : base(sql) { }

    private static readonly DateTime In = TestData.Now;

    /// <summary>
    /// Mirrors the Excel text-import repro: delimiters are comma AND space, <c>"</c> is the text qualifier
    /// (doubled <c>""</c> is a literal quote inside it) but, like Excel, it is only honoured at the START of a
    /// field, so a neutralise-after-quote bug (<c>'"=1"</c>) is not read as a clean cell. Written independently of the writer. A delimiter
    /// outside quotes ends a cell; inside quotes it is text. Blank separator lines yield no record.
    /// </summary>
    private static List<List<string>> Read(string text)
    {
        var records = new List<List<string>>();
        var record = new List<string>();
        var cell = new StringBuilder();
        var quoted = false;
        var cellOpen = false;

        void EndCell() { record.Add(cell.ToString()); cell.Clear(); cellOpen = false; }

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else cell.Append(c);
            }
            else if (c == '"' && !cellOpen && cell.Length == 0) { quoted = true; cellOpen = true; }
            else if (c == ',' || c == ' ') EndCell();
            else if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
            {
                if (cellOpen || cell.Length > 0 || record.Count > 0)
                {
                    EndCell();
                    records.Add(record);
                    record = [];
                }
                i++;
            }
            else { cell.Append(c); cellOpen = true; }
        }

        Assert.False(quoted, "The CSV ends inside an open quote.");
        Assert.True(!cellOpen && record.Count == 0, "The CSV's last record is not terminated by CRLF.");
        return records;
    }

    private static async Task<List<List<string>>> ReadResponseAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var bytes = await response.Content.ReadAsByteArrayAsync();
        return Read(Encoding.UTF8.GetString(bytes).TrimStart('﻿'));
    }

    private async Task<Guid> NewSchoolAsync()
    {
        await using var db = NewDbContext();
        var school = TestData.NewSchool();
        db.Schools.Add(school);
        await db.SaveChangesAsync();
        return school.Id;
    }

    [Fact]
    public async Task Personnel_export_cells_survive_a_reader_that_also_splits_on_spaces()
    {
        await NewSchoolAsync();

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory);

        var import = await client.PostAsJsonAsync("/api/v1/personnel/import", new
        {
            rows = new[]
            {
                new
                {
                    personnelNumber = "EMP-0001",
                    firstName = "Maria Clara",
                    middleName = "Reyes Santos",
                    lastName = "Dela Cruz",
                    email = "EMP-0001@usa.edu.ph",
                    classification = (string?)null,
                    department = "College of Information Technology",
                    organization = (string?)null,
                    position = "Assistant Professor",
                    rfidUid = (string?)null,
                    status = "Active",
                },
            },
        });
        Assert.Equal(HttpStatusCode.OK, import.StatusCode);

        var records = await ReadResponseAsync(await client.GetAsync("/api/v1/personnel/export.csv"));

        Assert.Equal(2, records.Count);
        Assert.Equal(PersonnelCsv.Header, records[0]);
        Assert.Equal(
            ["EMP-0001", "", "Dela Cruz", "Maria Clara", "Reyes Santos", "EMP-0001@usa.edu.ph", "",
             "College of Information Technology", "", "Active", "Assistant Professor"],
            records[1]);
    }

    [Fact]
    public async Task Clearance_export_cells_survive_a_reader_that_also_splits_on_spaces()
    {
        Guid schoolId, studentId;
        await using (var db = NewDbContext())
        {
            var school = TestData.NewSchool();
            db.Schools.Add(school);
            var student = TestData.NewStudent(
                school.Id, "2023-5001", firstName: "Maria Clara", middleName: null, lastName: "Dela Cruz");
            db.Students.Add(student);
            var ev = TestData.NewEvent(school.Id, EventStatus.Closed, AttendanceMode.Single);
            ev.Name = "Annual Sports Fest";
            db.Events.Add(ev);
            db.EventGroups.Add(new EventGroup { EventId = ev.Id, StudentId = student.Id });
            db.AttendanceRecords.Add(new AttendanceRecord
            {
                SchoolId = school.Id, EventId = ev.Id, StudentId = student.Id,
                CheckInAt = In, Status = AttendanceStatus.Present, CaptureMethod = CaptureMethod.Import,
            });
            await db.SaveChangesAsync();
            (schoolId, studentId) = (school.Id, student.Id);
        }

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory, schoolId);

        var records = await ReadResponseAsync(
            await client.GetAsync($"/api/v1/clearance/students/{studentId}/export"));

        Assert.Equal(2, records.Count);
        Assert.Equal(
            ["Student ID", "Name", "Department", "Program", "College", "Event Name", "Event Date",
             "Attendance", "Check-in (UTC)", "Check-out (UTC)"],
            records[0]);
        Assert.Equal(10, records[1].Count);
        Assert.Equal("2023-5001", records[1][0]);
        Assert.Equal("Dela Cruz, Maria Clara", records[1][1]);
        Assert.Equal("Annual Sports Fest", records[1][5]);
        Assert.Equal("Attended", records[1][7]);
    }

    [Fact]
    public async Task Attendance_analytics_export_cells_survive_a_reader_that_also_splits_on_spaces()
    {
        Guid schoolId;
        await using (var db = NewDbContext())
        {
            var school = TestData.NewSchool();
            db.Schools.Add(school);
            var ev = TestData.NewEvent(school.Id, status: EventStatus.Open);
            db.Events.Add(ev);
            var student = TestData.NewStudent(school.Id, "2023-0001", course: "BS Information Technology");
            db.Students.Add(student);
            db.AttendanceRecords.Add(new AttendanceRecord
            {
                SchoolId = school.Id, EventId = ev.Id, StudentId = student.Id,
                Status = AttendanceStatus.Present, CheckInAt = In,
            });
            await db.SaveChangesAsync();
            schoolId = school.Id;
        }

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory, schoolId);

        var records = await ReadResponseAsync(
            await client.GetAsync("/api/v1/reports/attendance-analytics/export.csv?groupBy=Course"));

        Assert.Equal(3, records.Count);
        Assert.Equal(
            ["Group", "Event Date", "Total Events", "People", "Present", "Late", "Absent", "Excused",
             "Total", "Attendance Rate %"],
            records[0]);
        Assert.Equal(10, records[1].Count);
        Assert.Equal("BS Information Technology", records[1][0]);
        Assert.Equal(10, records[2].Count);
        Assert.Equal("TOTAL", records[2][0]);
    }

    [Fact]
    public async Task Event_report_export_cells_survive_a_reader_that_also_splits_on_spaces()
    {
        Guid schoolId, eventId;
        await using (var db = NewDbContext())
        {
            var school = TestData.NewSchool();
            db.Schools.Add(school);
            var ev = TestData.NewEvent(school.Id, EventStatus.Open, AttendanceMode.TimeInOut);
            db.Events.Add(ev);
            var student = TestData.NewStudent(
                school.Id, "2023-0000", firstName: "Maria Clara", middleName: "Reyes Santos", lastName: "Dela Cruz");
            db.Students.Add(student);
            db.EventGroups.Add(new EventGroup { EventId = ev.Id, StudentId = student.Id });
            db.AttendanceRecords.Add(new AttendanceRecord
            {
                SchoolId = school.Id, EventId = ev.Id, StudentId = student.Id,
                CheckInAt = In, CheckOutAt = In.AddHours(1),
                Status = AttendanceStatus.Present, CaptureMethod = CaptureMethod.Rfid,
            });
            await db.SaveChangesAsync();
            (schoolId, eventId) = (school.Id, ev.Id);
        }

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory, schoolId, EamsRoleNames.SchoolAdmin);

        var records = await ReadResponseAsync(
            await client.GetAsync($"/api/v1/reports/event/{eventId}/export.csv"));

        var at = records.FindIndex(r => r.SequenceEqual(EventReportCsv.TimeInOutListHeader));
        Assert.True(at >= 0, "The student-list heading row did not survive as separate cells.");
        var row = Assert.Single(records.Skip(at + 1));
        Assert.Equal(7, row.Count);
        Assert.Equal("2023-0000", row[0]);
        Assert.Equal("Dela Cruz", row[1]);
        Assert.Equal("Maria Clara", row[2]);
        Assert.Equal("Reyes Santos", row[3]);
        Assert.Equal("1:00:00", row[6]);

        // Totals lines are label,value pairs; "Without Time Out" must not have split into three cells.
        Assert.Contains(records, r => r.Count == 2 && r[0] == "Without Time Out");
    }
}
