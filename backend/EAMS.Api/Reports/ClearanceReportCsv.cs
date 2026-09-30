using System.Globalization;
using System.Text;
using EAMS.Application.Dtos;

namespace EAMS.Api.Reports;

/// <summary>
/// CSV export of a clearance report (CLR-02), one row per event. Reuses <see cref="EventReportCsv"/>'s
/// cell escaping (OWASP formula-injection neutralisation + RFC 4180 quoting) and its UTF-8-with-BOM
/// encoding, so the two exports cannot disagree about how a name with a comma or a leading <c>=</c> is
/// written. No PDF: the SPA's Print button is the PDF path (JJ decision B, recorded on EventReportCsv).
/// </summary>
internal static class ClearanceReportCsv
{
    internal const string ContentType = EventReportCsv.ContentType;

    /// <summary>The report columns from CLR-01/CLR-02.</summary>
    private static readonly string[] Header =
        ["Student ID", "Name", "Department", "Program", "College", "Event Name", "Event Date",
         "Attendance", "Check-in (UTC)", "Check-out (UTC)"];

    internal static string FileNameFor(ClearanceReportDto report) =>
        $"EAMS-clearance-{report.Student.StudentNumber}-{DateTime.UtcNow:yyyy-MM-dd}.csv";

    internal static byte[] Write(ClearanceReportDto report)
    {
        var csv = new StringBuilder();
        Record(csv, Header);

        var s = report.Student;
        if (report.Events.Count == 0)
        {
            // A student with no events still exports one line, so an empty file is never mistaken for a
            // failed export (the spec's "No events on record" row).
            Record(csv, s.StudentNumber, s.FullName, s.Department ?? "", s.Program ?? "", s.College ?? "",
                "No events on record", "", "", "", "");
        }

        foreach (var e in report.Events)
        {
            Record(csv,
                s.StudentNumber, s.FullName, s.Department ?? "", s.Program ?? "", s.College ?? "",
                e.EventName,
                e.EventDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                e.Attendance,
                e.CheckInAt is { } cin ? EventReportCsv.Time(cin) : "",
                e.CheckOutAt is { } cout ? EventReportCsv.Time(cout) : "");
        }

        var text = csv.ToString();
        var preamble = Utf8WithBom.GetPreamble();
        var body = Utf8WithBom.GetBytes(text);
        var file = new byte[preamble.Length + body.Length];
        preamble.CopyTo(file, 0);
        body.CopyTo(file, preamble.Length);
        return file;
    }

    private static readonly Encoding Utf8WithBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);

    private static void Record(StringBuilder csv, params string[] cells)
    {
        for (var i = 0; i < cells.Length; i++)
        {
            if (i > 0) csv.Append(',');
            csv.Append(EventReportCsv.Cell(cells[i]));
        }
        csv.Append("\r\n");
    }
}
