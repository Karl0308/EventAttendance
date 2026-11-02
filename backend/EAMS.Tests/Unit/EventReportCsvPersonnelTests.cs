using EAMS.Api.Reports;
using EAMS.Application.Dtos;
using EAMS.Domain;
using Xunit;

namespace EAMS.Tests.Unit;

/// <summary>
/// ADR-015 D-101 and D-102 in the CSV: the ID column carries a personnel number, the names go through the
/// student export's quoting (<c>a8aa92e</c>), and "Expected" is the total above its two populations. The
/// whole-file pins are typed out, never built from <see cref="EventReportCsv"/>, so history cannot move
/// with the writer.
/// </summary>
public class EventReportCsvPersonnelTests
{
    private static readonly DateTime Start = new(2026, 7, 28, 1, 0, 0, DateTimeKind.Utc);

    private static EventReportExport Export(
        string mode, EventTimeOutTotalsDto? timeOut, int expectedStudents, int expectedPersonnel,
        params EventReportAttendeeDto[] attendees)
    {
        var id = new Guid("11111111-1111-1111-1111-111111111111");
        var details = new EventReportDetailsDto(
            id, "Convocation", "Gym", Start, Start.AddHours(3), mode, 15, EventStatus.Closed);
        var summary = new EventReportRowDto(
            id, "Convocation", EventStatus.Closed, Start,
            expectedStudents + expectedPersonnel, 1, 1, 0, 0, 0, 0, 50,
            expectedStudents, expectedPersonnel);

        return new EventReportExport(new EventDetailReportDto(details, summary, timeOut), attendees);
    }

    private static EventReportAttendeeDto Student(DateTime? timeOut = null) => new(
        Guid.NewGuid(), "2023-0001", "Dela Cruz", "Ana", null, Start,
        timeOut, timeOut is null ? null : 3600, AttendeePersonType.Student, null, null);

    private static EventReportAttendeeDto Personnel(
        string number = "EMP-0001", string last = "Rizal", string first = "Jose", string? middle = "P") => new(
        null, null, last, first, middle, Start, null, null, AttendeePersonType.Personnel, Guid.NewGuid(), number);

    /// <summary>
    /// History does not move: with no personnel, this is the file as it was before ADR-015, byte for byte,
    /// plus the lines "Expected students" and "Expected personnel". A <c>Single</c> event.
    /// </summary>
    [Fact]
    public void A_student_only_single_event_writes_the_old_file_plus_the_two_expected_rows()
    {
        var text = EventReportCsv.WriteText(Export(AttendanceMode.Single, null, 2, 0, Student()));

        Assert.Equal(
            "\"Event Attendance Report\"\r\n" +
            "\"Event\",\"Convocation\"\r\n" +
            "\"Location\",\"Gym\"\r\n" +
            "\"Starts (UTC)\",\"2026-07-28T01:00:00Z\"\r\n" +
            "\"Ends (UTC)\",\"2026-07-28T04:00:00Z\"\r\n" +
            "\"Mode\",\"Single\"\r\n" +
            "\"Grace (minutes)\",\"15\"\r\n" +
            "\"Status\",\"Closed\"\r\n" +
            "\r\n" +
            "\"Expected\",\"2\"\r\n" +
            "\"Expected students\",\"2\"\r\n" +
            "\"Expected personnel\",\"0\"\r\n" +
            "\"Attended\",\"1\"\r\n" +
            "\"Present\",\"1\"\r\n" +
            "\"Late\",\"0\"\r\n" +
            "\"Absent\",\"0\"\r\n" +
            "\"Excused\",\"0\"\r\n" +
            "\"Unexpected\",\"0\"\r\n" +
            "\"Attendance Rate (%)\",\"50.0\"\r\n" +
            "\r\n" +
            "\"Student Number\",\"Last Name\",\"First Name\",\"Middle Name\",\"Time In (UTC)\"\r\n" +
            "\"2023-0001\",\"Dela Cruz\",\"Ana\",\"\",\"2026-07-28T01:00:00Z\"\r\n",
            text);
        Assert.DoesNotContain("Person Type", text);
    }

    /// <summary>The same pin for a <c>TimeInOut</c> event, whose totals and list take their own branch.</summary>
    [Fact]
    public void A_student_only_time_in_out_event_writes_the_old_file_plus_the_two_expected_rows()
    {
        var text = EventReportCsv.WriteText(Export(
            AttendanceMode.TimeInOut, new EventTimeOutTotalsDto(1, 1, 1, 0, 3600), 2, 0,
            Student(Start.AddHours(1))));

        Assert.Equal(
            "\"Event Attendance Report\"\r\n" +
            "\"Event\",\"Convocation\"\r\n" +
            "\"Location\",\"Gym\"\r\n" +
            "\"Starts (UTC)\",\"2026-07-28T01:00:00Z\"\r\n" +
            "\"Ends (UTC)\",\"2026-07-28T04:00:00Z\"\r\n" +
            "\"Mode\",\"TimeInOut\"\r\n" +
            "\"Grace (minutes)\",\"15\"\r\n" +
            "\"Status\",\"Closed\"\r\n" +
            "\r\n" +
            "\"Expected\",\"2\"\r\n" +
            "\"Expected students\",\"2\"\r\n" +
            "\"Expected personnel\",\"0\"\r\n" +
            "\"Attended\",\"1\"\r\n" +
            "\"Present\",\"1\"\r\n" +
            "\"Late\",\"0\"\r\n" +
            "\"Absent\",\"0\"\r\n" +
            "\"Excused\",\"0\"\r\n" +
            "\"Unexpected\",\"0\"\r\n" +
            "\"Attendance Rate (%)\",\"50.0\"\r\n" +
            "\"Tapped IN\",\"1\"\r\n" +
            "\"Tapped OUT\",\"1\"\r\n" +
            "\"With Time Out\",\"1\"\r\n" +
            "\"Without Time Out\",\"0\"\r\n" +
            "\"Average Duration (h:mm:ss, complete pairs only)\",\"1:00:00\"\r\n" +
            "\r\n" +
            "\"Student Number\",\"Last Name\",\"First Name\",\"Middle Name\",\"Time In (UTC)\",\"Time Out (UTC)\",\"Duration (h:mm:ss)\"\r\n" +
            "\"2023-0001\",\"Dela Cruz\",\"Ana\",\"\",\"2026-07-28T01:00:00Z\",\"2026-07-28T02:00:00Z\",\"1:00:00\"\r\n",
            text);
        Assert.DoesNotContain("Person Type", text);
    }

    /// <summary>
    /// A list holding any personnel heads its ID column "ID Number" and adds a trailing "Person Type" column,
    /// filled on every row — the student row as well as the personnel row. A <c>Single</c> event.
    /// </summary>
    [Fact]
    public void A_mixed_single_event_heads_the_id_column_id_number_and_adds_a_person_type_column()
    {
        var text = EventReportCsv.WriteText(Export(AttendanceMode.Single, null, 1, 1, Student(), Personnel()));

        Assert.EndsWith(
            "\r\n\r\n" +
            "\"ID Number\",\"Last Name\",\"First Name\",\"Middle Name\",\"Time In (UTC)\",\"Person Type\"\r\n" +
            "\"2023-0001\",\"Dela Cruz\",\"Ana\",\"\",\"2026-07-28T01:00:00Z\",\"Student\"\r\n" +
            "\"EMP-0001\",\"Rizal\",\"Jose\",\"P\",\"2026-07-28T01:00:00Z\",\"Personnel\"\r\n",
            text);
        Assert.DoesNotContain("Student Number", text);
    }

    /// <summary>The same for a <c>TimeInOut</c> event: Person Type comes after Duration.</summary>
    [Fact]
    public void A_mixed_time_in_out_event_heads_the_id_column_id_number_and_adds_a_person_type_column()
    {
        var text = EventReportCsv.WriteText(Export(
            AttendanceMode.TimeInOut, new EventTimeOutTotalsDto(2, 1, 1, 1, 3600), 1, 1,
            Student(Start.AddHours(1)), Personnel()));

        Assert.EndsWith(
            "\r\n\r\n" +
            "\"ID Number\",\"Last Name\",\"First Name\",\"Middle Name\",\"Time In (UTC)\",\"Time Out (UTC)\",\"Duration (h:mm:ss)\",\"Person Type\"\r\n" +
            "\"2023-0001\",\"Dela Cruz\",\"Ana\",\"\",\"2026-07-28T01:00:00Z\",\"2026-07-28T02:00:00Z\",\"1:00:00\",\"Student\"\r\n" +
            "\"EMP-0001\",\"Rizal\",\"Jose\",\"P\",\"2026-07-28T01:00:00Z\",\"\",\"\",\"Personnel\"\r\n",
            text);
        Assert.DoesNotContain("Student Number", text);
    }

    [Theory]
    [InlineData(AttendanceMode.Single)]
    [InlineData(AttendanceMode.TimeInOut)]
    public void A_personnel_row_carries_its_personnel_number_in_the_id_column(string mode)
    {
        var timeOut = mode == AttendanceMode.TimeInOut ? new EventTimeOutTotalsDto(1, 0, 0, 1, null) : null;

        var text = EventReportCsv.WriteText(Export(mode, timeOut, 0, 1, Personnel()));

        Assert.Contains("\r\n\"EMP-0001\",\"Rizal\",\"Jose\",\"P\",\"2026-07-28T01:00:00Z\"", text);
    }

    /// <summary>D-101's named test: a formula in a personnel cell is neutralised, inside the quotes.</summary>
    [Fact]
    public void A_formula_prefix_on_a_personnel_name_is_neutralised_and_quoted()
    {
        var text = EventReportCsv.WriteText(Export(AttendanceMode.Single, null, 0, 1, Personnel(
            number: "-1+1", last: "=HYPERLINK(\"http://x\",\"y\")", first: "+Jose", middle: "@cmd")));

        Assert.Contains(
            "\"'-1+1\",\"'=HYPERLINK(\"\"http://x\"\",\"\"y\"\")\",\"'+Jose\",\"'@cmd\",\"2026-07-28T01:00:00Z\",\"Personnel\"\r\n",
            text);
    }
}
