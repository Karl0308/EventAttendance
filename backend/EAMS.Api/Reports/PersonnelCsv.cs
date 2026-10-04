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
/// <b>The header row is the spec's human-readable columns, in spec order</b> (StudentsEmployees.docx /
/// MDVault #540). The composed <c>FullName</c> is deliberately left out — it is server-derived and not an
/// import field. Import takes parsed JSON rows, not this CSV, so these display header names do not affect
/// the server round-trip; the front-end CSV parser maps these columns back to the import fields.
/// </para>
/// </summary>
internal static class PersonnelCsv
{
    internal const string ContentType = EventReportCsv.ContentType;

    /// <summary>
    /// The spec columns in spec order, then <c>Position</c> appended. Position is <b>not</b> a spec column;
    /// it is kept last so an export does not silently drop data the system holds for every record.
    /// </summary>
    internal static readonly string[] Header =
        ["Personnel ID", "RFID UID", "Last Name", "First Name", "Middle Name", "Email",
         "Classification", "Department", "Organization", "Status", "Position"];

    internal static string FileName() => $"EAMS-personnel-{DateTime.UtcNow:yyyy-MM-dd}.csv";

    internal static byte[] Write(IReadOnlyList<PersonnelDto> rows)
    {
        var csv = new StringBuilder();
        Record(csv, Header);

        foreach (var p in rows)
        {
            // Same order as Header: spec columns, then Position appended (see Header's note).
            Record(csv,
                p.PersonnelNumber, p.RfidUid ?? "", p.LastName, p.FirstName, p.MiddleName ?? "",
                p.Email ?? "", p.Classification ?? "", p.Department ?? "", p.Organization ?? "",
                p.Status, p.Position ?? "");
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
