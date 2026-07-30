using System.Security.Cryptography;
using ClosedXML.Excel;
using EAMS.Application.Abstractions;
using EAMS.Domain;

namespace EAMS.Infrastructure.Sis;

/// <summary>
/// Turns an uploaded <c>.xlsx</c> into staged rows. Reading only — it knows nothing about students,
/// terms or the database.
///
/// <para>
/// <b>ClosedXML, not EPPlus.</b> EPPlus moved to a commercial licence at version 5 and this is client
/// work, so it is not an option regardless of how well it reads a sheet. ClosedXML is MIT.
/// </para>
///
/// <para>
/// <b>The workbook's second sheet is skipped at batch level, not per row.</b> See
/// <see cref="SelectRosterSheet"/>.
/// </para>
/// </summary>
internal static class ExcelRosterReader
{
    /// <summary>
    /// A parsed workbook: which sheet was read, its header row in file order, and its data rows.
    /// </summary>
    internal sealed record RosterFile(
        string SheetName,
        IReadOnlyList<string> Columns,
        IReadOnlyList<RosterSourceRow> Rows,
        string FileHash);

    /// <summary>
    /// One data row, keyed by <see cref="SisRosterColumns.HeaderKey"/> and holding the <em>raw</em> cell
    /// text. Cleaning happens downstream: what is staged has to be what the file said, or
    /// <c>RawData</c> stops being evidence.
    /// </summary>
    internal sealed record RosterSourceRow(int RowNumber, IReadOnlyDictionary<string, string> Cells)
    {
        /// <summary>The raw text of one column, or the empty string when the column is absent or blank.</summary>
        public string Raw(string column) =>
            Cells.TryGetValue(SisRosterColumns.HeaderKey(column), out var value) ? value : "";
    }

    /// <summary>
    /// Hashes, opens and parses the workbook.
    /// </summary>
    /// <exception cref="SisImportException">
    /// The stream is not a readable workbook, or no worksheet in it carries
    /// <see cref="SisRosterColumns.Required"/>.
    /// </exception>
    public static RosterFile Read(Stream content)
    {
        if (!content.CanSeek)
            throw new SisImportException(
                "The upload stream must be seekable: it is read once to fingerprint the bytes and once " +
                "to parse them. Buffer the request body before calling.");

        content.Position = 0;
        var fileHash = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
        content.Position = 0;

        XLWorkbook workbook;
        try
        {
            workbook = new XLWorkbook(content);
        }
        catch (Exception ex) when (ex is not SisImportException)
        {
            // Wrapped, not swallowed: the inner exception travels with it and is logged, while the
            // operator gets a sentence they can act on instead of a ClosedXML stack trace. An upload of
            // the wrong file is the single most likely way this method fails.
            throw new SisImportException(
                "The uploaded file could not be opened as an .xlsx workbook. If it is a .xls or a .csv, " +
                "re-save it as .xlsx and upload again.", ex);
        }

        using (workbook)
        {
            var sheet = SelectRosterSheet(workbook);
            var used = sheet.RangeUsed()
                ?? throw new SisImportException($"Worksheet '{sheet.Name}' is empty.");

            var headers = ReadHeaders(used);
            var rows = ReadRows(used, headers);

            return new RosterFile(sheet.Name, headers, rows, fileHash);
        }
    }

    /// <summary>
    /// Picks the worksheet that is the roster.
    ///
    /// <para>
    /// <b>By header content, not by name or by position.</b> The sample workbook has two sheets: the
    /// 17-column <c>Faculty Evaluation Report</c> and a three-column <c>Sheet1</c> holding a redundant
    /// name/e-mail projection of the same students. Selecting by name would break the first time the
    /// registrar renames a tab; selecting the first sheet would work by luck. Selecting the first sheet
    /// whose header row carries every required column is the rule that describes what is actually being
    /// looked for, and it rejects <c>Sheet1</c> without reading one of its rows.
    /// </para>
    ///
    /// <para>
    /// <b>This is what "skip the second sheet at batch level" means.</b> Filtering it per row would
    /// stage 536 rows that exist only to be skipped, double every counter on the batch, and bury the
    /// 27 genuine skips among them.
    /// </para>
    /// </summary>
    private static IXLWorksheet SelectRosterSheet(XLWorkbook workbook)
    {
        var rejected = new List<string>();

        foreach (var sheet in workbook.Worksheets)
        {
            var used = sheet.RangeUsed();
            if (used is null)
            {
                rejected.Add($"'{sheet.Name}' (empty)");
                continue;
            }

            var present = ReadHeaders(used).Select(SisRosterColumns.HeaderKey).ToHashSet(StringComparer.Ordinal);
            var missing = SisRosterColumns.Required
                .Where(c => !present.Contains(SisRosterColumns.HeaderKey(c)))
                .ToList();

            if (missing.Count == 0) return sheet;

            rejected.Add($"'{sheet.Name}' (missing {string.Join(", ", missing)})");
        }

        throw new SisImportException(
            "No worksheet in this workbook carries the roster columns this import needs. Required: " +
            string.Join(", ", SisRosterColumns.Required) + ". Sheets examined: " +
            string.Join("; ", rejected) + ".");
    }

    private static List<string> ReadHeaders(IXLRange used) =>
        used.Row(1).Cells().Select(cell => RosterText.Clean(ReadCell(cell)) ?? "").ToList();

    private static List<RosterSourceRow> ReadRows(IXLRange used, IReadOnlyList<string> headers)
    {
        // Duplicate headers keep the first occurrence rather than throwing. An export that repeats a
        // column is malformed, but the roster is still importable from it, and the alternative is a
        // total failure over a column nothing reads.
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < headers.Count; i++)
        {
            var key = SisRosterColumns.HeaderKey(headers[i]);
            if (key.Length > 0) index.TryAdd(key, i + 1);
        }

        var rows = new List<RosterSourceRow>(used.RowCount());

        for (var r = 2; r <= used.RowCount(); r++)
        {
            var row = used.Row(r);
            var cells = new Dictionary<string, string>(index.Count, StringComparer.Ordinal);
            var anyContent = false;

            foreach (var (key, column) in index)
            {
                var text = ReadCell(row.Cell(column));
                cells[key] = text;
                if (!string.IsNullOrWhiteSpace(text)) anyContent = true;
            }

            // A wholly blank row is not staged. Spreadsheets accumulate them below the data — from a
            // deleted row, a stray format, a scroll — and staging them would report failures against
            // rows that contain nothing, which reads as a broken import rather than a tidy file.
            if (!anyContent) continue;

            // The worksheet's own row number, so it can be typed into Excel's Go To box.
            rows.Add(new RosterSourceRow(used.FirstRow().RowNumber() + r - 1, cells));
        }

        return rows;
    }

    /// <summary>
    /// One cell as text.
    ///
    /// <para>
    /// <b>Numbers are formatted explicitly, and one student depends on it.</b> 51 REGNOs are
    /// <c>USA#####</c> and arrive as text; the legacy <c>2021005781</c> arrives as a <em>number</em>,
    /// and default .NET double formatting renders that as <c>2.021005781E+09</c>. Stored, that is a
    /// student number no later export will ever match, so the next import creates the student again as
    /// a duplicate and every fact keyed off them splits in two.
    /// <see cref="RosterText.FormatNumericCell"/> is the fix and is tested against exactly that value.
    /// </para>
    ///
    /// <para>
    /// <b>The same conversion is lossy for the RFID card serial, and this is a known open seam rather
    /// than an oversight.</b> A serial's leading zeros are significant — <c>0012503326</c> is not
    /// <c>12503326</c> — and any cell Excel holds as a <em>number</em> has already lost them before this
    /// method sees it: <see cref="IXLCell.GetDouble"/> returns <c>12503326</c> and no formatting of that
    /// double can put back a zero the value never carried. A cell holding a serial <em>as text</em>,
    /// which is how a leading-zero identifier is normally stored and how the fixture writes it, round
    /// trips exactly and is what the tests pin.
    /// </para>
    ///
    /// <para>
    /// This method is deliberately NOT special-cased for the RFID column. It is shared by all eighteen,
    /// and the legacy REGNO above depends on <see cref="RosterText.FormatNumericCell"/> — reading
    /// numbers as their displayed string instead would fix a hypothetical and break a real student. The
    /// recoverable case (a numeric cell under a <c>0000000000</c> display mask, where
    /// <c>IXLCell.GetFormattedString()</c> would return the padded text) is worth building only against
    /// the client's actual export, which does not exist yet; guessing at the mask now would be a rule
    /// invented for a file nobody has seen.
    /// </para>
    ///
    /// <para>
    /// Dates go to round-trip <c>O</c> format. No roster column is a date today; the case is handled
    /// because the alternative is a locale-dependent string, which is the same class of silent
    /// corruption as the number one.
    /// </para>
    /// </summary>
    private static string ReadCell(IXLCell cell) => cell.DataType switch
    {
        XLDataType.Number => RosterText.FormatNumericCell(cell.GetDouble()),
        XLDataType.DateTime => cell.GetDateTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture),
        XLDataType.Boolean => cell.GetBoolean() ? "TRUE" : "FALSE",
        _ => cell.GetString(),
    };
}
