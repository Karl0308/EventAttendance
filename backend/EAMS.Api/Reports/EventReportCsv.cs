using System.Globalization;
using System.Text;
using EAMS.Application.Dtos;
using EAMS.Domain;

namespace EAMS.Api.Reports;

/// <summary>
/// Task 9.6's CSV export of one event's report (JJ decision B: CSV from the server; Print is the SPA's
/// stylesheet; no PDF). Pure formatting over <see cref="EventReportExport"/> — it computes no figure,
/// so it cannot disagree with the JSON reads it is written beside.
///
/// <para>
/// <b>Layout.</b> Three blocks separated by a blank line: the event's particulars, the totals, and the
/// student list with its own header row. A <c>Single</c> event (QA Q12) has no time-out totals and its
/// student list has no Time Out or Duration column.
/// </para>
///
/// <para>
/// <b>Times are UTC, written ISO 8601 with a <c>Z</c>, to the second, and every time column says so in
/// its heading.</b> The stored values are UTC. <c>Schools.TimeZone</c> exists (default
/// <c>Asia/Manila</c>) but nothing in the system converts with it yet, and resolving an IANA zone id on
/// the Windows host depends on ICU being present — so rendering Philippine time here would be a new,
/// unverified convention on the deployment. Reported to JJ as a finding rather than invented.
/// Truncating to the second also makes each row's Duration equal Time Out minus Time In as printed:
/// SQL Server's <c>DATEDIFF(SECOND, …)</c> counts second boundaries crossed, which is exactly the
/// difference of the truncated times.
/// </para>
///
/// <para>
/// <b>Formula injection (OWASP "CSV Injection").</b> Names come from the SIS roster and can hold
/// anything; a cell beginning with <c>=</c>, <c>+</c>, <c>-</c>, <c>@</c>, tab or carriage return is
/// executed by Excel as a formula. Every cell that begins with one is prefixed with <c>'</c>, which Excel
/// shows as text. Then RFC 4180 quoting: a cell holding a comma, a double quote, CR or LF is wrapped in
/// double quotes with its quotes doubled. Records end in CRLF.
/// </para>
///
/// <para>
/// <b>UTF-8 with a byte-order mark.</b> Without the BOM, Excel on Windows opens a CSV in the machine's
/// ANSI code page and a name like <c>Peñaflorida</c> turns into mojibake.
/// </para>
/// </summary>
internal static class EventReportCsv
{
    /// <summary>The response's media type. The charset is stated because the body is UTF-8, not ANSI.</summary>
    internal const string ContentType = "text/csv; charset=utf-8";

    /// <summary>
    /// The download's name: this prefix, the event's start date (UTC) and its id — the date for a
    /// person sorting a Downloads folder, the id so two events on one day never collide. The event's
    /// name is deliberately not in it: it is free text and would need sanitizing for every file system.
    /// </summary>
    internal const string FileNamePrefix = "EAMS-event-report";

    /// <summary>ISO 8601, UTC, whole seconds — see the class remarks.</summary>
    internal const string TimeFormat = "yyyy-MM-dd'T'HH:mm:ss'Z'";

    /// <summary>The characters that make a spreadsheet read a cell as a formula (OWASP).</summary>
    private static readonly char[] FormulaTriggers = ['=', '+', '-', '@', '\t', '\r'];

    /// <summary>The prefix that makes a spreadsheet read such a cell as text.</summary>
    private const char FormulaNeutraliser = '\'';

    private static readonly char[] NeedsQuoting = [',', '"', '\r', '\n'];

    private const string RecordSeparator = "\r\n";

    private static readonly Encoding Utf8WithBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);

    /// <summary>The student list's heading row for a <c>TimeInOut</c> event.</summary>
    internal static readonly string[] TimeInOutListHeader =
        ["Student Number", "Last Name", "First Name", "Middle Name", "Time In (UTC)", "Time Out (UTC)", "Duration (h:mm:ss)"];

    /// <summary>The student list's heading row for a <c>Single</c> event — no time-out columns (QA Q12).</summary>
    internal static readonly string[] SingleListHeader =
        ["Student Number", "Last Name", "First Name", "Middle Name", "Time In (UTC)"];

    internal static string FileNameFor(EventReportDetailsDto ev) =>
        $"{FileNamePrefix}-{ev.StartAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}-{ev.EventId:N}.csv";

    /// <summary>The whole file, BOM included.</summary>
    internal static byte[] Write(EventReportExport export)
    {
        var text = WriteText(export);
        var preamble = Utf8WithBom.GetPreamble();
        var body = Utf8WithBom.GetBytes(text);

        var file = new byte[preamble.Length + body.Length];
        preamble.CopyTo(file, 0);
        body.CopyTo(file, preamble.Length);
        return file;
    }

    /// <summary>The file's text, without the BOM.</summary>
    internal static string WriteText(EventReportExport export)
    {
        var csv = new StringBuilder();
        var report = export.Report;
        var ev = report.Event;
        var s = report.Summary;
        var timeInOut = ev.AttendanceMode == AttendanceMode.TimeInOut;

        Record(csv, "Event Attendance Report");
        Record(csv, "Event", ev.Name);
        Record(csv, "Location", ev.Location ?? "");
        Record(csv, "Starts (UTC)", Time(ev.StartAt));
        Record(csv, "Ends (UTC)", Time(ev.EndAt));
        Record(csv, "Mode", ev.AttendanceMode);
        Record(csv, "Grace (minutes)", Number(ev.GraceMinutes));
        Record(csv, "Status", ev.Status);
        csv.Append(RecordSeparator);

        Record(csv, "Expected", Number(s.Expected));
        Record(csv, "Attended", Number(s.Attended));
        Record(csv, "Present", Number(s.Present));
        Record(csv, "Late", Number(s.Late));
        Record(csv, "Absent", Number(s.Absent));
        Record(csv, "Excused", Number(s.Excused));
        Record(csv, "Unexpected", Number(s.Unexpected));
        Record(csv, "Attendance Rate (%)", s.AttendanceRate.ToString("0.0", CultureInfo.InvariantCulture));

        if (report.TimeInOut is { } t)
        {
            Record(csv, "Tapped IN", Number(t.TappedIn));
            Record(csv, "Tapped OUT", Number(t.TappedOut));
            Record(csv, "With Time Out", Number(t.WithTimeOut));
            Record(csv, "Without Time Out", Number(t.WithoutTimeOut));
            Record(csv, "Average Duration (h:mm:ss, complete pairs only)",
                t.AverageDurationSeconds is { } avg ? Duration(avg) : "");
        }
        csv.Append(RecordSeparator);

        Record(csv, timeInOut ? TimeInOutListHeader : SingleListHeader);
        foreach (var a in export.Attendees)
        {
            if (timeInOut)
            {
                Record(csv,
                    a.StudentNumber, a.LastName, a.FirstName, a.MiddleName ?? "",
                    Time(a.TimeIn),
                    a.TimeOut is { } timeOut ? Time(timeOut) : "",
                    a.DurationSeconds is { } seconds ? Duration(seconds) : "");
            }
            else
            {
                Record(csv, a.StudentNumber, a.LastName, a.FirstName, a.MiddleName ?? "", Time(a.TimeIn));
            }
        }

        return csv.ToString();
    }

    /// <summary>Hours are not wrapped at 24: a two-day event's pair reads <c>49:00:00</c>.</summary>
    internal static string Duration(long totalSeconds) =>
        string.Create(CultureInfo.InvariantCulture,
            $"{totalSeconds / 3600}:{totalSeconds % 3600 / 60:D2}:{totalSeconds % 60:D2}");

    internal static string Time(DateTime utc) =>
        UtcTime.Normalize(utc).ToString(TimeFormat, CultureInfo.InvariantCulture);

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>One cell, neutralised then quoted — in that order, so the <c>'</c> lands inside the quotes.</summary>
    internal static string Cell(string value)
    {
        if (value.Length > 0 && Array.IndexOf(FormulaTriggers, value[0]) >= 0)
            value = FormulaNeutraliser + value;

        return value.IndexOfAny(NeedsQuoting) >= 0
            ? "\"" + value.Replace("\"", "\"\"") + "\""
            : value;
    }

    private static void Record(StringBuilder csv, params string[] cells)
    {
        for (var i = 0; i < cells.Length; i++)
        {
            if (i > 0) csv.Append(',');
            csv.Append(Cell(cells[i]));
        }
        csv.Append(RecordSeparator);
    }
}
