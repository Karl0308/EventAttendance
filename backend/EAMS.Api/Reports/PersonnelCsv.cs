using System.Text;
using EAMS.Application.Dtos;

namespace EAMS.Api.Reports;

/// <summary>
/// CSV export of the Personnel tab (StudentsEmployees.docx), one row per record. Reuses
/// <see cref="EventReportCsv"/>'s cell escaping (OWASP formula-injection neutralisation + RFC 4180 quoting)
/// and UTF-8-with-BOM encoding, so this export cannot disagree with the others about how a value with a
/// comma or a leading <c>=</c> is written.
///
/// <para>
/// <b>The header row is exactly the columns <c>POST /personnel/import</c> reads back</b>, so an exported file
/// round-trips through import unchanged. The composed <c>FullName</c> is deliberately left out — it is
/// server-derived and not an import field.
/// </para>
/// </summary>
internal static class PersonnelCsv
{
    internal const string ContentType = EventReportCsv.ContentType;

    /// <summary>The import columns, in order. Kept in step with <c>PersonnelWriteRequest</c>.</summary>
    internal static readonly string[] Header =
        ["PersonnelNumber", "FirstName", "MiddleName", "LastName", "Email", "Classification",
         "Department", "Organization", "Position", "RfidUid", "Status"];

    internal static string FileName() => $"EAMS-personnel-{DateTime.UtcNow:yyyy-MM-dd}.csv";

    internal static byte[] Write(IReadOnlyList<PersonnelDto> rows)
    {
        var csv = new StringBuilder();
        Record(csv, Header);

        foreach (var p in rows)
        {
            Record(csv,
                p.PersonnelNumber, p.FirstName, p.MiddleName ?? "", p.LastName, p.Email ?? "",
                p.Classification ?? "", p.Department ?? "", p.Organization ?? "", p.Position ?? "",
                p.RfidUid ?? "", p.Status);
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
