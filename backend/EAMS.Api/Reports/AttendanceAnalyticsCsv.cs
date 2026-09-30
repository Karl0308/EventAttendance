using System.Globalization;
using System.Text;
using EAMS.Application.Dtos;

namespace EAMS.Api.Reports;

/// <summary>
/// CSV export of an attendance-analytics report (RPT-01), one row per group (or event, at the last level),
/// then a totals row. Reuses <see cref="EventReportCsv"/>'s cell escaping (OWASP formula-injection
/// neutralisation + RFC 4180 quoting) and UTF-8-with-BOM encoding, so the exports cannot disagree about how
/// a value with a comma or a leading <c>=</c> is written. No PDF: the SPA's Print button is the PDF path.
/// </summary>
internal static class AttendanceAnalyticsCsv
{
    internal const string ContentType = EventReportCsv.ContentType;

    private static readonly string[] Header =
        ["Group", "Event Date", "Total Events", "People", "Present", "Late", "Absent", "Excused",
         "Total", "Attendance Rate %"];

    internal static string FileNameFor(AttendanceAnalyticsReportDto report) =>
        $"EAMS-attendance-{report.GroupBy}-{DateTime.UtcNow:yyyy-MM-dd}.csv";

    internal static byte[] Write(AttendanceAnalyticsReportDto report)
    {
        var csv = new StringBuilder();
        Record(csv, Header);

        foreach (var r in report.Rows) Record(csv, Cells(r));
        Record(csv, Cells(report.Totals with { Key = "TOTAL" }));

        var text = csv.ToString();
        var preamble = Utf8WithBom.GetPreamble();
        var body = Utf8WithBom.GetBytes(text);
        var file = new byte[preamble.Length + body.Length];
        preamble.CopyTo(file, 0);
        body.CopyTo(file, preamble.Length);
        return file;
    }

    private static string[] Cells(AttendanceAnalyticsRowDto r) =>
    [
        r.Key,
        r.EventDate is { } d ? d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "",
        r.TotalEvents.ToString(CultureInfo.InvariantCulture),
        r.People.ToString(CultureInfo.InvariantCulture),
        r.Present.ToString(CultureInfo.InvariantCulture),
        r.Late.ToString(CultureInfo.InvariantCulture),
        r.Absent.ToString(CultureInfo.InvariantCulture),
        r.Excused.ToString(CultureInfo.InvariantCulture),
        r.Total.ToString(CultureInfo.InvariantCulture),
        r.AttendanceRate.ToString("0.0", CultureInfo.InvariantCulture),
    ];

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
