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

    // ----------------------------------------------------------------- the RFID serials under test

    /// <summary>
    /// Maria's card serial, and the shape the client gave us: ten decimal digits with <b>significant
    /// leading zeros</b>. It is deliberately nothing like her REGNO — the two were the same value until
    /// the client corrected us on 2026-07-30, and a fixture where they still resembled each other would
    /// let a regression back into REGNO-derived UIDs pass unnoticed.
    ///
    /// <para>
    /// Written as a <b>text</b> cell by <see cref="Build(IReadOnlyList{string[]}, IReadOnlyList{string})"/>,
    /// unlike <see cref="PedroRegNo"/>. That is the property under test rather than a convenience: a
    /// leading-zero identifier stored as text round trips exactly, and one stored as a number has lost
    /// its zeros before any code in this repository can see it.
    /// </para>
    /// </summary>
    public const string MariaRfid = "0012503326";

    /// <summary>Distinct from <see cref="MariaRfid"/> in its last digit only — a near miss is the useful kind.</summary>
    public const string JuanRfid = "0012503327";

    public const string AnaRfid = "0012503328";

    /// <summary>
    /// The legacy student's serial. No leading zero, so it proves the pipeline does not <em>require</em>
    /// one — the rule is "preserve what the file says", not "pad to ten".
    /// </summary>
    public const string PedroRfid = "9900112233";

    public const string RosaRfid = "0012503330";
    public const string LuciaRfid = "0012503331";
    public const string NinaRfid = "0012503332";
    public const string OmarRfid = "0012503333";

    /// <summary>
    /// <see cref="MariaRfid"/> with its leading zeros removed — what an Excel <em>numeric</em> cell
    /// would read back as. Named so a test can assert the pipeline never produces it, which is the whole
    /// content of "leading zeros are significant".
    /// </summary>
    public const string MariaRfidWithLeadingZerosLost = "12503326";

    /// <summary>
    /// The one legacy REGNO, ten digits. Written as a <b>numeric</b> cell, which is how Excel stores it
    /// and is the whole point: read with default double formatting it becomes <c>2.021005781E+09</c> —
    /// a student number no later export will ever match, so the next import creates that student a
    /// second time and every fact keyed off them splits between the two, with no error anywhere to
    /// explain it.
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

    // ------------------------------------------------------------------ Task 5: the category columns

    /// <summary>The <see cref="ClassificationAxis.Student"/> value. 20,861 of the real 21,497 rows.</summary>
    public const string StudentCategory = "STUDENT";

    /// <summary>
    /// The <see cref="ClassificationAxis.Personnel"/> value carried by <see cref="PedroRegNo"/>, whose
    /// registration number is <c>2021005781</c> — <b>no <c>720000</c> prefix</b>.
    ///
    /// <para>
    /// <b>That pairing is the regression this fixture exists to hold.</b> QA's Q3 says personnel are
    /// recognised by a <c>720000XXXX</c> registration number; 21 of the real export's 292 NAP staff
    /// carry numbers like <c>0020255</c> and <c>0000000</c> instead, so a derivation that read the
    /// prefix rather than the column would misfile every one of them — silently, as students. Pedro is
    /// the fixture's copy of those 21.
    /// </para>
    /// </summary>
    public const string PersonnelCategory = "NAP";

    /// <summary>
    /// The <see cref="ClassificationAxis.Special"/> value <see cref="AnaRegNo"/> carries <em>alongside</em>
    /// <see cref="StudentCategory"/> — the fixture's copy of the real export's <c>0020242</c>, who is
    /// STUDENT and C2B2 at once. Two columns could not have said so; four can.
    /// </summary>
    public const string SpecialCategory = "C2B2";

    /// <summary>
    /// The <see cref="ClassificationAxis.Special"/> value <see cref="RosaRegNo"/> carries on her
    /// <em>second</em> row, where her <see cref="StudentCategory"/> is on her first — the cross-row
    /// axis case. Distinct from <see cref="SpecialCategory"/> so a test cannot pass by confusing Rosa's
    /// categories with Ana's.
    /// </summary>
    public const string SecondSpecialCategory = "CFI";

    /// <summary>
    /// The one seeded value containing a slash, and the one most likely to break a naive slug, route or
    /// key assumption. It reaches the importer through
    /// <c>SisImportClassificationTests.Every_seeded_vocabulary_value_survives_the_importer</c> rather
    /// than through a fixture row, so the twelve rows stay the shape of the real roster.
    /// </summary>
    public const string SupervisoryCategory = "SUPERVISORY/MANAGERIAL";

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
        Row(MariaRegNo, MariaRfid, "Maria", MiddleNamePlaceholder, "Santos",
            "maria.santos@gmail.com", "maria.santos@usa.edu.ph",
            PrimaryProgram, PrimarySectionName, RizalCourseCode, "Life and Works of Rizal",
            PrimaryTeacherFullName, "THERESA", "NAVARRO", "Ms.", "College of Law"),

        // Row 3 — the same enrollment described a second time with no teacher. This is the real file's
        // 27 "duplicate" (REGNO, COURSE_CODE) pairs: one real teacher row plus one placeholder row.
        // It also spells the course 'SSci7', so it proves the key collapse at the same time. Must be
        // Skipped with reason InstructorPlaceholder — not Failed, and not a second enrollment.
        Row(MariaRegNo, MariaRfid, "Maria", MiddleNamePlaceholder, "Santos",
            "maria.santos@gmail.com", "maria.santos@usa.edu.ph",
            PrimaryProgram, PrimarySectionName, RizalCourseCodeAlternateSpelling,
            "Life and Works of Rizal",
            TeacherPlaceholder, TeacherPlaceholder, TeacherPlaceholder, TeacherPlaceholder,
            TeacherPlaceholder),

        // Row 4 — blank section (39 real rows) and a double-spaced course code, on one row.
        Row(MariaRegNo, MariaRfid, "Maria", MiddleNamePlaceholder, "Santos",
            "maria.santos@gmail.com", "maria.santos@usa.edu.ph",
            PrimaryProgram, "", DoubleSpacedCourseCode, "Criminalistics 2",
            GenerationalTeacherFullName, GenerationalTeacherFirstName, "SALCEDO", "Mr.",
            "College of Technology"),

        // Row 5 — first sighting of the two-titled elective, and the honorific-inside-the-name teacher.
        Row(MariaRegNo, MariaRfid, "Maria", MiddleNamePlaceholder, "Santos",
            "maria.santos@gmail.com", "maria.santos@usa.edu.ph",
            PrimaryProgram, PrimarySectionName, ElectiveCourseCode, ElectiveTitleFirstSeen,
            HonorificTeacherFullName, HonorificTeacherFirstName, "MORALES", "Mrs.",
            "College of Liberal Arts Sciences and Education"),

        // Row 6 — the same elective under a different title. Must warn and still import. Also the only
        // student with a genuinely blank middle name rather than the '-' placeholder.
        Row(JuanRegNo, JuanRfid, "Juan", "", "Cruz",
            "", "juan.cruz@usa.edu.ph",
            PrimaryProgram, PrimarySectionName, ElectiveCourseCode, ElectiveTitleAlias,
            PrimaryTeacherFullName, "THERESA", "NAVARRO", "Ms.", "College of Law"),

        // Row 7 — a real middle initial, which must not be mistaken for the placeholder. Also the
        // fixture's two-axis person: STUDENT and C2B2 at once, like the real export's 0020242. Both
        // must survive, which is the whole argument for four columns rather than QA's two.
        Row(AnaRegNo, AnaRfid, "Ana", MiddleInitial, "Reyes",
            "ana.reyes@gmail.com", "ana.reyes@usa.edu.ph",
            PrimaryProgram, PrimarySectionName, RizalCourseCode, "Life and Works of Rizal",
            PrimaryTeacherFullName, "THERESA", "NAVARRO", "Ms.", "College of Law",
            specialCategory: SpecialCategory),

        // Row 8 — the legacy numeric REGNO, on a row whose only teacher is the placeholder. Unlike row
        // 3 this one is the *only* row for its enrollment, so it must import rather than skip: the
        // placeholder suppresses the instructor, never the enrollment.
        //
        // It is also the fixture's NAP staff member WITHOUT a 720000 registration number — see
        // PersonnelCategory. Not a student, so the student column is blank: this row proves the
        // category column is read rather than assumed, in both directions at once.
        Row(PedroRegNo, PedroRfid, "Pedro", MiddleNamePlaceholder, "Lim",
            "pedro.lim@gmail.com", "pedro.lim@usa.edu.ph",
            PrimaryProgram, PrimarySectionName, RizalCourseCode, "Life and Works of Rizal",
            TeacherPlaceholder, TeacherPlaceholder, TeacherPlaceholder, TeacherPlaceholder,
            TeacherPlaceholder,
            studentCategory: "", personnelCategory: PersonnelCategory),

        // Rows 9 and 10 — one student under two section keys, which §4.3's single Section column cannot
        // represent and which is the entire reason the academic layer exists.
        Row(RosaRegNo, RosaRfid, "Rosa", MiddleNamePlaceholder, "Tan",
            "rosa.tan@gmail.com", "rosa.tan@usa.edu.ph",
            PrimaryProgram, PrimarySectionName, RizalCourseCode, "Life and Works of Rizal",
            PrimaryTeacherFullName, "THERESA", "NAVARRO", "Ms.", "College of Law"),

        // Rosa's second row is also the fixture's CROSS-ROW AXIS case, and it is two cells rather than
        // a whole scenario: her STUDENT flag is on row 9 and her CFI flag is here on row 10, with each
        // row blank where the other speaks.
        //
        // A course roster's grain is the enrollment, so a person's category columns are filled in on
        // whichever row the registrar's report happened to carry them — and taking one row's set
        // entire silently drops every axis the other rows named. Before this, both of the fixture's
        // multi-axis people carried both axes on a SINGLE row, so replacing the per-axis union with
        // `group.First.Categories.Categories` left the whole suite green. It does not now.
        Row(RosaRegNo, RosaRfid, "Rosa", MiddleNamePlaceholder, "Tan",
            "rosa.tan@gmail.com", "rosa.tan@usa.edu.ph",
            PrimaryProgram, RotcSectionName, "MS 32", "Military Science 32",
            SecondTeacherFullName, "ANTONIO", "QUIZON", "Mr.", "College of Technology",
            studentCategory: "", specialCategory: SecondSpecialCategory),

        // Row 11 — invisible characters in the first name (leading BOM), the last name (trailing
        // zero-width space) and the section (a non-breaking space instead of a space). All three must
        // clean away, and the section in particular must resolve to the SAME offering as rows 2/7/9 —
        // if it does not, this student silently lands in a section of one.
        Row(LuciaRegNo, LuciaRfid, ZeroWidthNoBreakSpace + LuciaFirstName, MiddleNamePlaceholder,
            LuciaLastName + ZeroWidthSpace,
            "", "lucia.vergara@usa.edu.ph",
            PrimaryProgram, "BSCRIM" + NonBreakingSpace + "2-A", RizalCourseCode,
            "Life and Works of Rizal",
            PrimaryTeacherFullName, "THERESA", "NAVARRO", "Ms.", "College of Law"),

        // Rows 12 and 13 — one section name under two programmes. Harmless (offerings are keyed by
        // course as well as section) but warned, because it is the leading indicator of the collision
        // that is NOT harmless on Courses.
        Row(NinaRegNo, NinaRfid, "Nina", MiddleNamePlaceholder, "Uy",
            "", "nina.uy@usa.edu.ph",
            PrimaryProgram, NursingSectionName, "NSTP 2", "National Service Training Program 2",
            SecondTeacherFullName, "ANTONIO", "QUIZON", "Mr.", "College of Technology"),

        Row(OmarRegNo, OmarRfid, "Omar", MiddleNamePlaceholder, "Diaz",
            "", "omar.diaz@usa.edu.ph",
            SecondaryProgram, NursingSectionName, "NSTP 2", "National Service Training Program 2",
            SecondTeacherFullName, "ANTONIO", "QUIZON", "Mr.", "College of Technology"),
    ];

    /// <summary>
    /// The three rows of <see cref="Rows"/> that raise <b>no warning of any kind</b> — worksheet rows 2,
    /// 7 and 9: Maria, Ana and Rosa, each filed under <see cref="PrimaryProgram"/> in
    /// <see cref="PrimarySectionName"/>, all enrolled in <see cref="RizalCourseCode"/> under one
    /// spelling and one title, all taught by <see cref="PrimaryTeacherFullName"/>.
    ///
    /// <para>
    /// <b>It exists so a test can assert <c>Completed</c> rather than <c>CompletedWithWarnings</c>.</b>
    /// The full twelve carry six deliberate warning rows — a blank section, two placeholder teachers, a
    /// title alias and a two-programme section pair — so no batch built from them can ever be silent.
    /// That makes them useless for pinning the one claim that needs an otherwise-silent batch to be
    /// falsifiable: <em>a student with no RFID card is not a warning</em>. On the full roster that
    /// assertion would pass against a pipeline that warned on every card-less row, because the status
    /// was already <c>CompletedWithWarnings</c> for other reasons.
    /// </para>
    ///
    /// <para>
    /// Selected by index rather than by a predicate over the rows. A predicate would have to
    /// re-implement the warning rules, and an expectation computed by the implementation's own logic
    /// proves only that the code agrees with itself — the same argument this class's summary makes
    /// about every count in <see cref="SisImportPipelineTests"/>.
    /// </para>
    /// </summary>
    public static List<string[]> RowsWithoutWarnings()
    {
        var rows = Rows();
        return [rows[0], rows[5], rows[7]];
    }

    /// <summary>
    /// The columns of a workbook that <b>has no RFID column at all</b> — which is the roster we actually
    /// hold, and therefore the shape the pipeline has to import cleanly with every student getting no
    /// card and no complaint.
    ///
    /// <para>
    /// It is a column list rather than a blanked-out cell on purpose: an absent column and a present-but-
    /// empty one reach the parse by different routes (no dictionary entry versus an entry holding
    /// <c>""</c>) and only one of them is today's reality. Rows stay parallel to
    /// <see cref="SisRosterColumns.All"/> either way; the writer skips the columns that are not here.
    /// </para>
    /// </summary>
    public static readonly IReadOnlyList<string> ColumnsWithoutRfid =
        SisRosterColumns.All.Where(c => c != SisRosterColumns.RfidCardSerial).ToList();

    /// <summary>
    /// The columns of a workbook carrying <b>none of the four Task 5 category columns</b> — which is
    /// every roster file that existed before the template changed, and therefore the shape the pipeline
    /// has to import in complete silence: no classification assigned, and <b>no warning</b>, because a
    /// warning on 100% of the rows of every legacy batch would make <c>CompletedWithWarnings</c> the
    /// permanent status of every import.
    ///
    /// <para>
    /// A column list rather than blanked-out cells, for the reason <see cref="ColumnsWithoutRfid"/>
    /// gives: an absent column and a present-but-empty one reach the parse by different routes, and here
    /// they deliberately produce <em>different</em> outcomes — silence versus
    /// <c>ClassificationMissing</c> — so a fixture that conflated them could not tell the two apart.
    /// </para>
    /// </summary>
    public static readonly IReadOnlyList<string> ColumnsWithoutCategories = SisRosterColumns.All
        .Where(c => SisRosterColumns.ClassificationColumns.All(x => x.Column != c))
        .ToList();

    /// <summary>
    /// The index of one column inside a <see cref="Rows"/> array, so a test can change a single cell
    /// and prove what the import does about it. Rows stay parallel to
    /// <see cref="SisRosterColumns.All"/> whichever column list a workbook is built from.
    /// </summary>
    public static int ColumnIndex(string column) => IndexOf(column);

    /// <summary>
    /// <see cref="Rows"/> with every category cell on every row cleared — a file that <b>carries</b> the
    /// four columns and fills none of them. The tier-3 case, twelve times over.
    /// </summary>
    public static List<string[]> RowsWithoutCategories()
    {
        var rows = Rows();
        foreach (var row in rows)
            foreach (var (_, column) in SisRosterColumns.ClassificationColumns)
                row[IndexOf(column)] = "";

        return rows;
    }

    /// <summary>Builds the roster as it existed before Task 5: all columns except the four categories.</summary>
    public static MemoryStream BuildWithoutCategoryColumns() =>
        Build(Rows(), ColumnsWithoutCategories);

    /// <summary>Builds the default workbook: all eighteen columns, every student carrying a serial.</summary>
    public static MemoryStream Build() => Build(Rows());

    /// <summary>
    /// Builds a workbook from caller-supplied rows, so a test can change one cell — a corrected
    /// surname, a removed student, a duplicated serial — and prove what the second import does about it.
    /// </summary>
    public static MemoryStream Build(IReadOnlyList<string[]> rows) =>
        Build(rows, SisRosterColumns.All);

    /// <summary>
    /// The roster as it exists today: the header row omits <see cref="SisRosterColumns.RfidCardSerial"/>
    /// entirely, so no student has a card and none of them may fail because of it.
    /// </summary>
    public static MemoryStream BuildWithoutRfidColumn() =>
        Build(Rows(), ColumnsWithoutRfid);

    /// <inheritdoc cref="BuildWithoutRfidColumn()"/>
    public static MemoryStream BuildWithoutRfidColumn(IReadOnlyList<string[]> rows) =>
        Build(rows, ColumnsWithoutRfid);

    /// <summary>
    /// Builds a workbook carrying only <paramref name="columns"/>, in that order.
    /// <paramref name="rows"/> stay parallel to <see cref="SisRosterColumns.All"/> regardless, so the
    /// row constants above are written once and every shape reads from them.
    /// </summary>
    public static MemoryStream Build(IReadOnlyList<string[]> rows, IReadOnlyList<string> columns)
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
        for (var c = 0; c < columns.Count; c++)
            sheet.Cell(1, c + 1).Value = columns[c];

        for (var r = 0; r < rows.Count; r++)
        {
            for (var c = 0; c < columns.Count; c++)
            {
                var value = rows[r][IndexOf(columns[c])];
                var cell = sheet.Cell(r + 2, c + 1);

                // An all-digit REGNO is written as a *number*, which is what Excel does with
                // 2021005781 and is the case ExcelRosterReader.ReadCell exists to survive. Writing it
                // as text here would make the fixture tidier than the file it stands in for, and the
                // scientific-notation defect would ship.
                //
                // The RFID column is deliberately NOT given the same treatment even though its values
                // are all digits too. A card serial's leading zeros are significant, so writing
                // 0012503326 as a number would destroy it inside the fixture and the test would be
                // asserting against a value the file never contained. Text is also how a leading-zero
                // identifier is genuinely stored, so this is the honest shape rather than the convenient
                // one. See ExcelRosterReader.ReadCell for the numeric-cell case that remains open.
                if (columns[c] == SisRosterColumns.RegNo
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
    /// <param name="studentCategory">
    /// Defaults to <see cref="StudentCategory"/> because 20,861 of the real export's 21,497 rows carry
    /// it and a fixture whose every row were uncategorised would make the warning path, not the
    /// ordinary one, the thing under test. Pass <c>""</c> for a row that is not a student.
    /// </param>
    private static string[] Row(
        string regNo, string rfid, string firstName, string middleName, string lastName,
        string personalEmail, string institutionalEmail,
        string program, string sectionName, string courseCode, string courseName,
        string teacherFullName, string teacherFirstName, string teacherLastName,
        string teacherSuffix, string teacherCollege,
        string studentCategory = StudentCategory, string personnelCategory = "",
        string friarsCategory = "", string specialCategory = "") =>
    [
        regNo, rfid, firstName, middleName, lastName,
        string.Join(' ', new[] { firstName, middleName, lastName }
            .Where(p => !string.IsNullOrWhiteSpace(p) && p != MiddleNamePlaceholder)),
        personalEmail, institutionalEmail,
        CollegeName, program, sectionName, courseCode, courseName,
        teacherFullName, teacherFirstName, teacherLastName, teacherSuffix, teacherCollege,
        studentCategory, personnelCategory, friarsCategory, specialCategory,
    ];
}
