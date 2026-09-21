using ClosedXML.Excel;
using EAMS.Domain;

namespace EAMS.Infrastructure.Sis;

/// <summary>
/// Writes the roster template workbook: an <b>Instructions</b> sheet and a <b>Roster</b> sheet whose
/// header row is the import profile's source columns. Pure — no database, no HTTP.
///
/// <para>
/// <b>The Instructions sheet is safe to ship inside the file the school uploads back</b> because
/// <see cref="ExcelRosterReader"/> selects the roster by header content: a sheet whose first row does not
/// carry every <see cref="SisRosterColumns.Required"/> heading is never read. Instructions is placed
/// FIRST on purpose, so every upload of this template exercises that rule rather than passing because
/// the roster happened to be the first sheet.
/// </para>
/// </summary>
internal static class SisImportTemplateWorkbook
{
    public const string RosterSheetName = "Roster";
    public const string InstructionsSheetName = "Instructions";

    /// <summary>Excel's built-in "Text" number format (<c>@</c>).</summary>
    public const int TextNumberFormatId = 49;

    /// <summary>The comment on a header cell whose value must be filled in. Tests read it.</summary>
    public const string RequiredHeaderComment = "Required: a row with this cell blank is not imported.";

    private const int InstructionsColumnWidth = 110;
    private const int InstructionsTitleFontSize = 14;
    private const int RosterColumnPadding = 4;
    private const int RosterMinimumColumnWidth = 12;

    /// <summary>One profile row, as the profile table (or the built-in entries) records it.</summary>
    internal sealed record ProfileRow(
        string SourceColumn, string SourceColumnKey, string TargetField, bool IsRequired);

    /// <summary>One assignable classification.</summary>
    internal sealed record VocabularyEntry(string Axis, string Name);

    /// <summary>One header in the template, after distinct-by-header folding.</summary>
    internal sealed record TemplateColumn(string Header, bool ValueRequired, bool IsText, string? Axis);

    /// <summary>
    /// The template's columns: the distinct source columns in profile order, keyed by
    /// <see cref="SisRosterColumns.HeaderKey"/> because that is how the reader matches a header — two
    /// profile rows naming <c>COLLEGE_NAME</c> (one per target) are one column in the file.
    ///
    /// <para>
    /// <b>Then every <see cref="SisRosterColumns.Required"/> header the profile left out, appended in that
    /// list's order.</b> <see cref="ExcelRosterReader"/> picks the roster sheet by that fixed list, not by
    /// the profile, so an operator-authored profile that omitted one would otherwise produce a template the
    /// reader rejects. An appended header is described by the built-in entries for it, because those are
    /// the rules the importer applies to it (its value columns are read by fixed header names too).
    /// </para>
    /// </summary>
    public static IReadOnlyList<TemplateColumn> ColumnsOf(IReadOnlyList<ProfileRow> rows)
    {
        var columns = new List<TemplateColumn>();
        var indexByKey = new Dictionary<string, int>(StringComparer.Ordinal);

        Fold(columns, indexByKey, rows);

        foreach (var required in SisRosterColumns.Required)
        {
            var key = SisRosterColumns.HeaderKey(required);
            if (indexByKey.ContainsKey(key)) continue;

            var builtIn = SisImportProfileTemplate.Entries
                .Where(e => SisRosterColumns.HeaderKey(e.SourceColumn) == key)
                .Select(e => new ProfileRow(e.SourceColumn, key, e.TargetField, e.IsRequired))
                .ToList();

            Fold(columns, indexByKey, builtIn.Count > 0 ? builtIn : [new ProfileRow(required, key, "(none)", false)]);
        }

        return columns;
    }

    private static void Fold(
        List<TemplateColumn> columns, Dictionary<string, int> indexByKey, IEnumerable<ProfileRow> rows)
    {
        foreach (var row in rows)
        {
            var key = row.SourceColumnKey.Length > 0 ? row.SourceColumnKey : SisRosterColumns.HeaderKey(row.SourceColumn);
            if (key.Length == 0) continue;

            var isText = row.TargetField
                is SisImportProfileTemplate.RfidCardUidTarget or SisImportProfileTemplate.StudentNumberTarget;
            string? axis = null;
            if (row.TargetField.StartsWith(SisImportProfileTemplate.ClassificationTargetPrefix, StringComparison.Ordinal)
                && ClassificationAxis.TryNormalize(
                    row.TargetField[SisImportProfileTemplate.ClassificationTargetPrefix.Length..], out var canonical))
                axis = canonical;

            if (indexByKey.TryGetValue(key, out var at))
            {
                var seen = columns[at];
                columns[at] = seen with
                {
                    ValueRequired = seen.ValueRequired || row.IsRequired,
                    IsText = seen.IsText || isText,
                    Axis = seen.Axis ?? axis,
                };
                continue;
            }

            indexByKey[key] = columns.Count;
            columns.Add(new TemplateColumn(row.SourceColumn.Trim(), row.IsRequired, isText, axis));
        }
    }

    /// <summary>
    /// Builds the workbook into memory and returns it positioned at 0.
    ///
    /// <para>
    /// <b>Buffered once, and only once.</b> An .xlsx is a zip package, which the OpenXML writer under
    /// ClosedXML can only write synchronously to a seekable stream — neither of which a response body is.
    /// The file is a few kilobytes; the caller streams this buffer out without copying it again.
    /// </para>
    /// </summary>
    public static MemoryStream Build(
        string profileName, int profileVersion,
        IReadOnlyList<ProfileRow> rows, IReadOnlyList<VocabularyEntry> vocabulary)
    {
        var columns = ColumnsOf(rows);

        using var workbook = new XLWorkbook();
        var instructions = workbook.AddWorksheet(InstructionsSheetName);
        var roster = workbook.AddWorksheet(RosterSheetName);

        WriteRoster(roster, columns);
        WriteInstructions(instructions, profileName, profileVersion, columns, vocabulary);
        instructions.SetTabActive();

        var buffer = new MemoryStream();
        workbook.SaveAs(buffer);
        buffer.Position = 0;
        return buffer;
    }

    private static void WriteRoster(IXLWorksheet sheet, IReadOnlyList<TemplateColumn> columns)
    {
        for (var i = 0; i < columns.Count; i++)
        {
            var column = columns[i];
            var number = i + 1;
            var header = sheet.Cell(1, number);

            // Written as text explicitly, so a header that looks like a number stays the header.
            header.SetValue(column.Header);
            header.Style.Font.Bold = true;
            header.Style.Fill.BackgroundColor = column.ValueRequired ? XLColor.LightGoldenrodYellow : XLColor.LightGray;

            if (column.ValueRequired) header.GetComment().AddText(RequiredHeaderComment);

            // Leading zeros are the identifier (0012503326 is not 12503326). A cell Excel has stored as a
            // number has already lost them before the importer sees it, so the column is Text from the
            // start. Column-level, so every row the school types into inherits it.
            if (column.IsText) sheet.Column(number).Style.NumberFormat.NumberFormatId = TextNumberFormatId;

            sheet.Column(number).Width = Math.Max(RosterMinimumColumnWidth, column.Header.Length + RosterColumnPadding);
        }

        sheet.SheetView.FreezeRows(1);
    }

    private static void WriteInstructions(
        IXLWorksheet sheet, string profileName, int profileVersion,
        IReadOnlyList<TemplateColumn> columns, IReadOnlyList<VocabularyEntry> vocabulary)
    {
        var lines = InstructionLines(profileName, profileVersion, columns, vocabulary);

        sheet.Column(1).Width = InstructionsColumnWidth;
        for (var i = 0; i < lines.Count; i++)
        {
            var (text, heading) = lines[i];
            var cell = sheet.Cell(i + 1, 1);
            cell.SetValue(text);
            cell.Style.Alignment.WrapText = true;
            cell.Style.Alignment.Vertical = XLAlignmentVerticalValues.Top;
            if (heading) cell.Style.Font.Bold = true;
        }

        sheet.Cell(1, 1).Style.Font.FontSize = InstructionsTitleFontSize;
    }

    /// <summary>
    /// The Instructions sheet, one cell per line. Every statement here is a description of what
    /// <c>SisImportService</c> does today — change the importer, change this.
    /// </summary>
    internal static List<(string Text, bool Heading)> InstructionLines(
        string profileName, int profileVersion,
        IReadOnlyList<TemplateColumn> columns, IReadOnlyList<VocabularyEntry> vocabulary)
    {
        var lines = new List<(string, bool)>();
        void Heading(string text) { lines.Add(("", false)); lines.Add((text, true)); }
        void Line(string text) => lines.Add((text, false));

        var headerRequired = SisRosterColumns.Required
            .Select(SisRosterColumns.HeaderKey)
            .ToHashSet(StringComparer.Ordinal);

        var textColumns = columns.Where(c => c.IsText).Select(c => c.Header).ToList();
        var placeholderColumns = new[] { SisRosterColumns.CollegeName, SisRosterColumns.Program, SisRosterColumns.CourseCode };
        var placeholderList = string.Join(", ", placeholderColumns);

        lines.Add(("EAMS roster import template: instructions", true));
        Line($"Generated from import profile '{profileName}', version {profileVersion}. Download a fresh copy " +
             "if the import rules change; an older copy may carry headings the import no longer reads.");

        Heading("How to use this file");
        Line($"1. Fill in the '{RosterSheetName}' sheet, starting on row 2. Keep its first row exactly as it is: " +
             "the import finds the roster by these headings, not by the sheet's name or position. Do not " +
             "delete, rename or reorder a heading. If you have nothing for a column, leave its cells blank.");
        Line($"2. One row per student per class. The same {SisRosterColumns.RegNo} appears on as many rows as " +
             "the person has classes.");
        Line("3. Save as an Excel Workbook (.xlsx) and upload it on the Students > Import roster page, choosing " +
             "the term it belongs to.");
        Line($"4. This '{InstructionsSheetName}' sheet can stay in the file. The import ignores any sheet whose " +
             "first row does not carry the roster headings.");

        Heading("Columns");
        Line("Headings with a yellow fill and a comment must have a value on every row: a row with one of " +
             "them blank is rejected and nothing on it is imported. The other columns may be left blank.");
        foreach (var column in columns)
        {
            var rule = column.ValueRequired
                ? "MUST be filled in."
                : headerRequired.Contains(SisRosterColumns.HeaderKey(column.Header))
                    ? "May be left blank, but the heading must stay."
                    : "Optional.";
            var note = column.Axis is not null
                ? $" Category for the {column.Axis} group; see 'Category columns' below."
                : column.IsText
                    ? " Formatted as Text so leading zeros are kept; see 'Leading zeros' below."
                    : "";
            Line($"- {column.Header}: {rule}{note}");
        }

        Heading("Category columns");
        var categoryColumns = columns.Where(c => c.Axis is not null).ToList();
        Line("A person can have one value in each category column, and values in several columns at once " +
             "(for example STUDENT in the Student column and NAP in the Personnel column). Type each value in " +
             "the column for its group. Accepted values, from this school's classification list when this " +
             "file was downloaded:");
        foreach (var axis in ClassificationAxis.All)
        {
            var column = categoryColumns.FirstOrDefault(c => c.Axis == axis);
            var names = vocabulary.Where(v => v.Axis == axis).Select(v => v.Name).ToList();
            var accepted = names.Count == 0 ? "(none defined yet; ask an EAMS administrator)" : string.Join(", ", names);
            Line(column is null
                ? $"- {axis}: this import profile has no column for this group, so the import cannot set it."
                : $"- {column.Header} ({axis}): {accepted}");
        }
        Line("Values are matched ignoring upper and lower case, spaces and punctuation. The import never " +
             "creates a new category: a value that is not in the list above is not applied, the person is " +
             "still imported, and the import reports a warning for the row. Only an EAMS administrator can " +
             "add or reactivate a category.");
        Line(NeverReplacesRule);
        Line(WrongColumnRule);
        Line(BlankCategoriesRule);

        Heading($"Staff and other people with no class: {placeholderList}");
        Line($"{placeholderList} must be filled in on every row. A row with any of them blank is rejected " +
             "as a missing required value and nothing on it is imported. A person whose every row is " +
             "rejected (for example a staff member with no class) is not imported, not enrolled and not " +
             "classified at all.");
        Line(NoPlaceholderRule);
        Line(NoClassRule);

        if (textColumns.Count > 0)
        {
            Heading("Leading zeros");
            Line($"The {string.Join(" and ", textColumns)} column{(textColumns.Count == 1 ? " is" : "s are")} " +
                 LeadingZerosRuleTail);
            Line(PasteRule);
        }

        return lines;
    }

    // ------------------------------------------------------------------ the rules the school must not miss
    //
    // Constants rather than inline strings so SisImportTemplateTests can pin each one exactly: these are
    // the sentences QA asked to be guaranteed present, and each describes SisImportService as it behaves.

    /// <summary><see cref="SisImportWarningCode.ClassificationWrongAxis"/>, in the school's words.</summary>
    internal const string WrongColumnRule =
        "A value typed in another group's column is not applied. For example, NAP typed in the Student " +
        "column is not filed anywhere: the person is still imported, and the import reports a warning that " +
        "names the column the value belongs in.";

    /// <summary><c>SisImportService.ResolveClassificationsAsync</c>'s blank-cells branch.</summary>
    internal const string BlankCategoriesRule =
        "Leave a cell blank when the person has no value in that group. Blank cells never remove anything: " +
        "a person who already has a classification keeps it, silently. If all the category cells are blank " +
        "and the person has no classification yet, they are imported with none and the import reports a " +
        "warning. Nobody is ever defaulted to STUDENT.";

    /// <summary><see cref="SisImportWarningCode.ClassificationConflict"/>, in the school's words.</summary>
    internal const string NeverReplacesRule =
        "An import never replaces a category a person already has. If the file names a different value in " +
        "the same group, the existing one is kept and the import reports a warning the first time the file " +
        "names it.";

    /// <summary>The #427 "NA" trap. See <c>SisImportService.ParseRows</c>.</summary>
    internal const string NoPlaceholderRule =
        "Do NOT type NA, N/A, NONE, a dash or any other placeholder into these columns to get round this. " +
        "The import treats whatever is typed as a real name: typing NA creates a college, program and " +
        "course literally called 'NA', enrols the person in it, and those records then appear beside the " +
        "real ones wherever colleges, programs and courses are listed or picked, including when choosing " +
        "an event's audience.";

    /// <summary>Staff with no class: there is no supported path today.</summary>
    internal const string NoClassRule =
        "There is currently no way to import a person who has no college, program and course through this " +
        "file. Leave such people out of it and raise them with the EAMS administrator.";

    internal const string LeadingZerosRuleTail =
        "formatted as Text in this template, because leading zeros are part of the value: RFID card " +
        "0012503326 and card 12503326 are different cards. Type or paste values as they are printed. Do not " +
        "change these columns to a Number or General format.";

    internal const string PasteRule =
        "When pasting from another spreadsheet, use Paste Special > Values and check the zeros are still " +
        "there. A cell Excel has already turned into a number has lost its zeros, and the import cannot put " +
        "them back.";
}
