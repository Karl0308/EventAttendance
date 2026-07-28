using ClosedXML.Excel;
using EAMS.Domain;

namespace EAMS.Tests.Integration.Infrastructure;

/// <summary>
/// A workbook shaped exactly like the CICSS export, generated in memory, carrying one instance of every
/// defect the real file contains.
///
/// <para>
/// <b>Why this exists instead of committing the real file.</b> <c>Copy-of-CCJ.xlsx</c> holds 52 real
/// students' full names and institutional e-mail addresses. This repository is on GitHub; committing
/// that file is a personal-data disclosure that survives every later deletion in the git history, and
/// relying on <c>.gitignore</c> to prevent it puts the whole thing one <c>git add -f</c> away from
/// happening. The real file is still run — locally, as a one-off verification whose counts are reported
/// — but nothing in the repository depends on it.
/// </para>
///
/// <para>
/// <b>The second reason is better than the privacy one.</b> A committed binary fixture is opaque: a
/// reader cannot see that row 3 exists to prove the <c>'TO BE ANNOUNCE'</c> partner of a duplicate pair
/// skips, or that one REGNO is deliberately a numeric cell. Here every case is a named constant with
/// the reason beside it, so a test that stops covering something is visible in a diff.
/// </para>
///
/// <para>
/// <b>Twelve data rows, and every count in <see cref="SisImportPipelineTests"/> is derived from this
/// table by hand.</b> That is deliberate: an expected value computed by the same logic as the
/// implementation proves only that the code agrees with itself.
/// </para>
/// </summary>
internal static class SyntheticRoster
{
    /// <summary>The roster sheet's name. Matched by header content, not by this — see below.</summary>
    public const string SheetName = "Faculty Evaluation Report";

    /// <summary>
    /// The redundant three-column sheet, and it is written <b>first</b> in the workbook on purpose. The
    /// real file has it second, so a reader that took the first sheet would pass against the real file
    /// and against a naive fixture. Putting it first here means "skip the sheet that is not the roster"
    /// is proven rather than coincidental.
    /// </summary>
    public const string RedundantSheetName = "Sheet1";

    public const int DataRowCount = 12;

    // ------------------------------------------------------------------ the identities under test

    /// <summary>The ordinary <c>USA#####</c> shape, 51 of the real file's 52.</summary>
    public const string MariaRegNo = "USA00001";

    public const string JuanRegNo = "USA00002";
    public const string AnaRegNo = "USA00003";

    /// <summary>
    /// The one legacy REGNO, ten digits. Written as a <b>numeric</b> cell, which is how Excel stores it
    /// and is the whole point: read with default double formatting it becomes <c>2.021005781E+09</c>,
    /// and since REGNO is also the RFID card UID that student would simply never be able to tap, with
    /// nothing anywhere to say why.
    /// </summary>
    public const string PedroRegNo = "2021005781";

    /// <summary>Enrolled under two different section keys — 12 of the real file's 52 students are.</summary>
    public const string RosaRegNo = "USA00005";

    /// <summary>Carries invisible characters in three cells. See <see cref="ZeroWidthNoBreakSpace"/>.</summary>
    public const string LuciaRegNo = "USA00006";

    public const string NinaRegNo = "USA00007";
    public const string OmarRegNo = "USA00008";

    // --------------------------------------------------------------------- the values under test

    public const string CollegeName = "College of Criminal Justice";
    public const string PrimaryProgram = "BSci - Crim";

    /// <summary>
    /// A second programme sharing a section name with the first, so
    /// <c>SisImportWarningCode.SectionSpansPrograms</c> has something to fire on. Confined to the
    /// <c>BSN 1-B</c> rows so it does not contaminate the counts everywhere else.
    /// </summary>
    public const string SecondaryProgram = "BS Criminology";

    /// <summary>Double space, exactly as the real file writes it. Key <c>CA2</c>.</summary>
    public const string DoubleSpacedCourseCode = "CA  2";

    /// <summary>Two spellings of one course. Both key to <c>SSCI7</c> and must resolve to one row.</summary>
    public const string RizalCourseCode = "SSCI 7";

    /// <inheritdoc cref="RizalCourseCode"/>
    public const string RizalCourseCodeAlternateSpelling = "SSci7";

    /// <summary>One code, two titles — the only case where <c>Courses.Title</c> is provably lossy.</summary>
    public const string ElectiveCourseCode = "GE Elect 2";

    /// <summary>Seen first, so it wins <c>Courses.Title</c>.</summary>
    public const string ElectiveTitleFirstSeen = "The Entrepreneurial Mind";

    /// <summary>Seen second, so it is reported as an alias and its row still imports.</summary>
    public const string ElectiveTitleAlias = "Gender and Society / Entrepreneurial Mind";

    /// <summary>Fills all five teacher columns on the rows that have no teacher. 66 real rows do this.</summary>
    public const string TeacherPlaceholder = "TO BE ANNOUNCE";

    /// <summary>A generational suffix living inside the first-name token, as the real file has it.</summary>
    public const string GenerationalTeacherFullName = "ROBERTO III MENDOZA SALCEDO";

    public const string GenerationalTeacherFirstName = "ROBERTO III";

    /// <summary>A religious honorific living inside the name. Must be stripped, or it splits the key.</summary>
    public const string HonorificTeacherFullName = "SR. CLARA BENITEZ MORALES";

    public const string HonorificTeacherFirstName = "SR. CLARA";

    /// <summary>What <see cref="HonorificTeacherFullName"/> must be stored as.</summary>
    public const string HonorificTeacherExpectedDisplayName = "CLARA BENITEZ MORALES";

    public const string PrimaryTeacherFullName = "THERESA GALANG NAVARRO";
    public const string SecondTeacherFullName = "ANTONIO BELTRAN QUIZON";

    public const string PrimarySectionName = "BSCRIM 2-A";
    public const string RotcSectionName = "ROTC";
    public const string NursingSectionName = "BSN 1-B";

    /// <summary>The middle-name placeholder. 200 of the real file's 536 rows carry it.</summary>
    public const string MiddleNamePlaceholder = "-";

    /// <summary>A genuine middle initial, which must survive — it is not a placeholder.</summary>
    public const string MiddleInitial = "E.";

    // Invisible characters, spelled numerically. A literal one in a source file is unreadable in every
    // diff and every editor, which is the same argument RosterText makes for its own set.
    private const string ZeroWidthNoBreakSpace = "\uFEFF";
    private const string ZeroWidthSpace = "\u200B";
    private const string NonBreakingSpace = "\u00A0";

    /// <summary>Prefixed with a byte-order mark; must clean to exactly this.</summary>
    public const string LuciaFirstName = "Lucia";

    /// <summary>Suffixed with a zero-width space; must clean to exactly this.</summary>
    public const string LuciaLastName = "Vergara";

    // -------------------------------------------------------------------------------- the rows

    /// <summary>
    /// The twelve data rows, in worksheet order, each an array parallel to
    /// <see cref="SisRosterColumns.All"/>. A fresh list every call so a test can mutate one row without
    /// affecting the next test.
    /// </summary>
    public static List<string[]> Rows() =>
    [
        // Row 2 — the ordinary case, and the one that creates almost everything.
        Row(MariaRegNo, "Maria", MiddleNamePlaceholder, "Santos",
            "maria.santos@gmail.com", "maria.santos@usa.edu.ph",
            PrimaryProgram, PrimarySectionName, RizalCourseCode, "Life and Works of Rizal",
            PrimaryTeacherFullName, "THERESA", "NAVARRO", "Ms.", "College of Law"),

        // Row 3 — the same enrollment described a second time with no teacher. This is the real file's
        // 27 "duplicate" (REGNO, COURSE_CODE) pairs: one real teacher row plus one placeholder row.
        // It also spells the course 'SSci7', so it proves the key collapse at the same time. Must be
        // Skipped with reason InstructorPlaceholder — not Failed, and not a second enrollment.
        Row(MariaRegNo, "Maria", MiddleNamePlaceholder, "Santos",
            "maria.santos@gmail.com", "maria.santos@usa.edu.ph",
            PrimaryProgram, PrimarySectionName, RizalCourseCodeAlternateSpelling,
            "Life and Works of Rizal",
            TeacherPlaceholder, TeacherPlaceholder, TeacherPlaceholder, TeacherPlaceholder,
            TeacherPlaceholder),

        // Row 4 — blank section (39 real rows) and a double-spaced course code, on one row.
        Row(MariaRegNo, "Maria", MiddleNamePlaceholder, "Santos",
            "maria.santos@gmail.com", "maria.santos@usa.edu.ph",
            PrimaryProgram, "", DoubleSpacedCourseCode, "Criminalistics 2",
            GenerationalTeacherFullName, GenerationalTeacherFirstName, "SALCEDO", "Mr.",
            "College of Technology"),

        // Row 5 — first sighting of the two-titled elective, and the honorific-inside-the-name teacher.
        Row(MariaRegNo, "Maria", MiddleNamePlaceholder, "Santos",
            "maria.santos@gmail.com", "maria.santos@usa.edu.ph",
            PrimaryProgram, PrimarySectionName, ElectiveCourseCode, ElectiveTitleFirstSeen,
            HonorificTeacherFullName, HonorificTeacherFirstName, "MORALES", "Mrs.",
            "College of Liberal Arts Sciences and Education"),

        // Row 6 — the same elective under a different title. Must warn and still import. Also the only
        // student with a genuinely blank middle name rather than the '-' placeholder.
        Row(JuanRegNo, "Juan", "", "Cruz",
            "", "juan.cruz@usa.edu.ph",
            PrimaryProgram, PrimarySectionName, ElectiveCourseCode, ElectiveTitleAlias,
            PrimaryTeacherFullName, "THERESA", "NAVARRO", "Ms.", "College of Law"),

        // Row 7 — a real middle initial, which must not be mistaken for the placeholder.
        Row(AnaRegNo, "Ana", MiddleInitial, "Reyes",
            "ana.reyes@gmail.com", "ana.reyes@usa.edu.ph",
            PrimaryProgram, PrimarySectionName, RizalCourseCode, "Life and Works of Rizal",
            PrimaryTeacherFullName, "THERESA", "NAVARRO", "Ms.", "College of Law"),

        // Row 8 — the legacy numeric REGNO, on a row whose only teacher is the placeholder. Unlike row
        // 3 this one is the *only* row for its enrollment, so it must import rather than skip: the
        // placeholder suppresses the instructor, never the enrollment.
        Row(PedroRegNo, "Pedro", MiddleNamePlaceholder, "Lim",
            "pedro.lim@gmail.com", "pedro.lim@usa.edu.ph",
            PrimaryProgram, PrimarySectionName, RizalCourseCode, "Life and Works of Rizal",
            TeacherPlaceholder, TeacherPlaceholder, TeacherPlaceholder, TeacherPlaceholder,
            TeacherPlaceholder),

        // Rows 9 and 10 — one student under two section keys, which §4.3's single Section column cannot
        // represent and which is the entire reason the academic layer exists.
        Row(RosaRegNo, "Rosa", MiddleNamePlaceholder, "Tan",
            "rosa.tan@gmail.com", "rosa.tan@usa.edu.ph",
            PrimaryProgram, PrimarySectionName, RizalCourseCode, "Life and Works of Rizal",
            PrimaryTeacherFullName, "THERESA", "NAVARRO", "Ms.", "College of Law"),

        Row(RosaRegNo, "Rosa", MiddleNamePlaceholder, "Tan",
            "rosa.tan@gmail.com", "rosa.tan@usa.edu.ph",
            PrimaryProgram, RotcSectionName, "MS 32", "Military Science 32",
            SecondTeacherFullName, "ANTONIO", "QUIZON", "Mr.", "College of Technology"),

        // Row 11 — invisible characters in the first name (leading BOM), the last name (trailing
        // zero-width space) and the section (a non-breaking space instead of a space). All three must
        // clean away, and the section in particular must resolve to the SAME offering as rows 2/7/9 —
        // if it does not, this student silently lands in a section of one.
        Row(LuciaRegNo, ZeroWidthNoBreakSpace + LuciaFirstName, MiddleNamePlaceholder,
            LuciaLastName + ZeroWidthSpace,
            "", "lucia.vergara@usa.edu.ph",
            PrimaryProgram, "BSCRIM" + NonBreakingSpace + "2-A", RizalCourseCode,
            "Life and Works of Rizal",
            PrimaryTeacherFullName, "THERESA", "NAVARRO", "Ms.", "College of Law"),

        // Rows 12 and 13 — one section name under two programmes. Harmless (offerings are keyed by
        // course as well as section) but warned, because it is the leading indicator of the collision
        // that is NOT harmless on Courses.
        Row(NinaRegNo, "Nina", MiddleNamePlaceholder, "Uy",
            "", "nina.uy@usa.edu.ph",
            PrimaryProgram, NursingSectionName, "NSTP 2", "National Service Training Program 2",
            SecondTeacherFullName, "ANTONIO", "QUIZON", "Mr.", "College of Technology"),

        Row(OmarRegNo, "Omar", MiddleNamePlaceholder, "Diaz",
            "", "omar.diaz@usa.edu.ph",
            SecondaryProgram, NursingSectionName, "NSTP 2", "National Service Training Program 2",
            SecondTeacherFullName, "ANTONIO", "QUIZON", "Mr.", "College of Technology"),
    ];

    /// <summary>Builds the default workbook.</summary>
    public static MemoryStream Build() => Build(Rows());

    /// <summary>
    /// Builds a workbook from caller-supplied rows, so a test can change one cell — a corrected
    /// surname, a removed student — and prove what the second import does about it.
    /// </summary>
    public static MemoryStream Build(IReadOnlyList<string[]> rows)
    {
        using var workbook = new XLWorkbook();

        // Written first. See RedundantSheetName.
        var redundant = workbook.AddWorksheet(RedundantSheetName);
        redundant.Cell(1, 1).Value = "STUDENT FULL NAME";
        redundant.Cell(1, 2).Value = "MASTERSOFT EMAIL_ID";
        redundant.Cell(1, 3).Value = "USA EMAIL ADDRESS";
        for (var i = 0; i < rows.Count; i++)
        {
            redundant.Cell(i + 2, 1).Value = rows[i][IndexOf(SisRosterColumns.FullName)];
            redundant.Cell(i + 2, 2).Value = rows[i][IndexOf(SisRosterColumns.EmailId)];
            redundant.Cell(i + 2, 3).Value = rows[i][IndexOf(SisRosterColumns.UsaEmail)];
        }

        var sheet = workbook.AddWorksheet(SheetName);
        for (var c = 0; c < SisRosterColumns.All.Count; c++)
            sheet.Cell(1, c + 1).Value = SisRosterColumns.All[c];

        for (var r = 0; r < rows.Count; r++)
        {
            for (var c = 0; c < SisRosterColumns.All.Count; c++)
            {
                var value = rows[r][c];
                var cell = sheet.Cell(r + 2, c + 1);

                // An all-digit REGNO is written as a *number*, which is what Excel does with
                // 2021005781 and is the case ExcelRosterReader.ReadCell exists to survive. Writing it
                // as text here would make the fixture tidier than the file it stands in for, and the
                // scientific-notation defect would ship.
                if (c == IndexOf(SisRosterColumns.RegNo)
                    && value.Length > 0 && value.All(char.IsDigit))
                {
                    cell.Value = double.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
                    continue;
                }

                cell.Value = value;
            }
        }

        var stream = new MemoryStream();
        workbook.SaveAs(stream);
        stream.Position = 0;
        return stream;
    }

    private static int IndexOf(string column) =>
        SisRosterColumns.All.ToList().IndexOf(column);

    /// <summary>
    /// Composes one row in <see cref="SisRosterColumns.All"/> order. <c>COLLEGE_NAME</c> and
    /// <c>FULL_NAME</c> are filled in here rather than passed: the college is constant across the whole
    /// real export, and the full name is derived, so spelling either at each call site would be twelve
    /// chances to make them disagree with the parts.
    /// </summary>
    private static string[] Row(
        string regNo, string firstName, string middleName, string lastName,
        string personalEmail, string institutionalEmail,
        string program, string sectionName, string courseCode, string courseName,
        string teacherFullName, string teacherFirstName, string teacherLastName,
        string teacherSuffix, string teacherCollege) =>
    [
        regNo, firstName, middleName, lastName,
        string.Join(' ', new[] { firstName, middleName, lastName }
            .Where(p => !string.IsNullOrWhiteSpace(p) && p != MiddleNamePlaceholder)),
        personalEmail, institutionalEmail,
        CollegeName, program, sectionName, courseCode, courseName,
        teacherFullName, teacherFirstName, teacherLastName, teacherSuffix, teacherCollege,
    ];
}
