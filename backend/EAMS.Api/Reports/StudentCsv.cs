using System.Text;
using EAMS.Application.Dtos;

namespace EAMS.Api.Reports;

/// <summary>
/// CSV export of the Students tab (ADR-011 D-83), one row per student. Reuses <see cref="EventReportCsv"/>'s
/// cell escaping (OWASP formula-injection neutralisation + RFC 4180 quoting of every cell, commit
/// <c>a8aa92e</c>) and UTF-8-with-BOM encoding, exactly as <see cref="PersonnelCsv"/> does, so the exports
/// cannot disagree about how a value with a comma or a leading <c>=</c> is written.
///
/// <para>
/// <b>This file is not re-importable, and is not meant to be.</b> The roster import takes XLSX only (JJ,
/// 2026-08-01), and these are display headers, not the import template's. <c>Course</c>, <c>Year Level</c>
/// and <c>Section</c> are the single-valued ADR-001 D-2 cache, so a student in several sections shows one
/// of them — the same value the grid this exports shows.
/// </para>
/// </summary>
internal static class StudentCsv
{
    internal const string ContentType = EventReportCsv.ContentType;

    /// <summary>What joins a student's several active card serials into the one RFID cell.</summary>
    internal const string CardUidSeparator = "; ";

    /// <summary>The display columns, in ADR-011 D-83's order.</summary>
    internal static readonly string[] Header =
        ["Student No.", "RFID UID (active)", "Last Name", "First Name", "Middle Name", "Email",
         "Course", "Year Level", "Section", "Organization", "Status"];

    internal static string FileName() => $"EAMS-students-{DateTime.UtcNow:yyyy-MM-dd}.csv";

    internal static byte[] Write(IReadOnlyList<StudentExportRow> rows)
    {
        var csv = new StringBuilder();
        Record(csv, Header);

        foreach (var s in rows)
        {
            // Same order as Header. Every active card, joined, so no card is hidden (JJ, 2026-10-23).
            Record(csv,
                s.StudentNumber, string.Join(CardUidSeparator, s.ActiveCardUids), s.LastName, s.FirstName,
                s.MiddleName ?? "", s.Email ?? "", s.Course ?? "", s.YearLevel ?? "", s.Section ?? "",
                s.Organization ?? "", s.Status);
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
