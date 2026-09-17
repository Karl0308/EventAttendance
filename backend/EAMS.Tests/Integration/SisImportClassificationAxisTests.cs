using EAMS.Application.Abstractions;
using EAMS.Application.Dtos;
using EAMS.Domain;
using EAMS.Infrastructure.Data;
using EAMS.Infrastructure.Sis;
using EAMS.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EAMS.Tests.Integration;

/// <summary>
/// <b>Pins <see cref="SisImportWarningCode.ClassificationAxisUnknown"/></b> — the batch-wide warning
/// raised when the pinned import profile maps a <c>StudentClassification.&lt;axis&gt;</c> target whose
/// suffix is not one of the four <see cref="ClassificationAxis"/> values.
///
/// <para>
/// This is a misconfiguration of the <em>profile</em>, not of any one row, so every test here builds
/// its own profile version and pins the batch to it — the same pattern
/// <see cref="SisImportClassificationTests.A_batch_pinned_to_the_pre_Task_5_profile_version_classifies_nobody"/>
/// uses for the same reason.
/// </para>
/// </summary>
[Collection(DatabaseCollection.Name)]
public class SisImportClassificationAxisTests : IntegrationTest
{
    public SisImportClassificationAxisTests(SqlServerFixture sql) : base(sql) { }

    private sealed record World(Guid SchoolId, Guid TermId);

    private async Task<World> ArrangeAsync()
    {
        await using var db = NewDbContext();
        var school = TestData.NewSchool();
        db.Schools.Add(school);
        var term = TestData.NewTerm(school.Id);
        db.Terms.Add(term);
        db.Classifications.AddRange(TestData.ClassificationVocabulary(school.Id));
        await db.SaveChangesAsync();
        return new World(school.Id, term.Id);
    }

    private async Task<List<SisImportRowDto>> RowsOfAsync(Guid batchId)
    {
        await using var db = NewDbContext();
        return (await SisImportOn(db).GetRowsAsync(batchId, result: null)).ToList();
    }

    /// <summary>
    /// Builds and pins a profile version whose classification columns are exactly
    /// <paramref name="axisTargets"/> — each a pair of (source column, axis suffix as written in
    /// <c>TargetField</c>, e.g. <c>"Faculty"</c> or <c>""</c> for the empty-suffix case). Returns the
    /// batch id, unrun.
    /// </summary>
    private async Task<Guid> UploadPinnedToProfileAsync(
        World world, IReadOnlyList<(string Column, string AxisSuffix)> axisTargets, Stream? file = null)
    {
        var owned = file is null ? SyntheticRoster.Build() : null;
        var content = file ?? owned!;

        try
        {
            content.Position = 0;

            Guid batchId;
            await using (var db = NewDbContext())
            {
                var preview = await SisImportOn(db).UploadAsync(
                    new SisImportUploadRequest(content, "Copy-of-CCJ.xlsx", world.TermId));
                batchId = preview.Batch.Id;
            }

            await using (var db = NewDbContext())
            {
                var profile = new SisImportProfile
                {
                    SchoolId = world.SchoolId,
                    Name = SisImportProfileTemplate.ProfileName,
                    NameKey = AcademicKey.NormalizeOrUnspecified(SisImportProfileTemplate.ProfileName),
                    Version = SisImportProfileTemplate.BuiltInVersion + 1,
                    Source = SisImportSource.Excel,
                    IsActive = false,
                    Description = "Test-authored version with a mistyped classification axis.",
                };
                db.SisImportProfiles.Add(profile);

                var ordinal = 0;
                foreach (var (column, axisSuffix) in axisTargets)
                {
                    db.SisImportProfileColumns.Add(new SisImportProfileColumn
                    {
                        Profile = profile,
                        SourceColumn = column,
                        SourceColumnKey = SisRosterColumns.HeaderKey(column),
                        TargetField = SisImportProfileTemplate.ClassificationTargetPrefix + axisSuffix,
                        NormalizationRule = "RosterClassification.Resolve",
                        IsRequired = false,
                        Ordinal = ordinal++,
                    });
                }

                var batch = await db.SisImportBatches.IgnoreQueryFilters()
                    .SingleAsync(b => b.Id == batchId);
                batch.ImportProfileId = profile.Id;
                await db.SaveChangesAsync();
            }

            return batchId;
        }
        finally
        {
            owned?.Dispose();
        }
    }

    private static readonly IReadOnlyList<(string Column, string AxisSuffix)> AllFourValidAxes =
    [
        (SisRosterColumns.StudentCategory, ClassificationAxis.Student),
        (SisRosterColumns.PersonnelCategory, ClassificationAxis.Personnel),
        (SisRosterColumns.FriarsCategory, ClassificationAxis.Friars),
        (SisRosterColumns.SpecialCategory, ClassificationAxis.Special),
    ];

    // ============================================================================== the trigger

    /// <summary>
    /// <b>A profile mapping an axis that is not one of the four fires the warning on every row of the
    /// batch</b>, including a row that would otherwise import warning-free — pinned as
    /// <c>WarningRows == TotalRows</c>, the implementer's own claimed signature. Verified empirically
    /// rather than trusted: every one of the twelve fixture rows is checked, not just the count.
    /// </summary>
    [Fact]
    public async Task A_mistyped_classification_axis_warns_every_row_of_the_batch()
    {
        var world = await ArrangeAsync();

        // 'Faculty' is not Student/Personnel/Friars/Special under any casing.
        var batchId = await UploadPinnedToProfileAsync(
            world, [(SisRosterColumns.StudentCategory, "Faculty")]);

        var batch = await SisImportOn(NewDbContext()).RunAsync(batchId, world.TermId);

        Assert.Equal(SyntheticRoster.DataRowCount, batch.TotalRows);
        Assert.Equal(batch.TotalRows, batch.WarningRows);

        var rows = await RowsOfAsync(batchId);
        Assert.Equal(SyntheticRoster.DataRowCount, rows.Count);
        Assert.All(rows, r =>
            Assert.Equal(SisImportWarningCode.ClassificationAxisUnknown, r.WarningCode));

        // Including the message content, which names the bad target.
        Assert.All(rows, r => Assert.Contains(
            "StudentClassification.Faculty", r.WarningMessage!, StringComparison.Ordinal));
    }

    /// <summary>
    /// <b>The empty suffix — <c>TargetField</c> exactly <c>"StudentClassification."</c> — is included
    /// among the cases <c>ClassificationAxis.TryNormalize</c> refuses</b>, not merely "any non-empty
    /// junk". A normalizer that special-cased blank as "no axis, skip silently" would leave this profile
    /// row unreported.
    /// </summary>
    [Fact]
    public async Task An_empty_axis_suffix_also_warns_every_row()
    {
        Assert.False(ClassificationAxis.TryNormalize("", out _),
            "the domain rule this test pins: TryNormalize must refuse the empty string");

        var world = await ArrangeAsync();
        var batchId = await UploadPinnedToProfileAsync(
            world, [(SisRosterColumns.StudentCategory, "")]);

        var batch = await SisImportOn(NewDbContext()).RunAsync(batchId, world.TermId);

        Assert.Equal(batch.TotalRows, batch.WarningRows);
        Assert.All(await RowsOfAsync(batchId), r =>
            Assert.Equal(SisImportWarningCode.ClassificationAxisUnknown, r.WarningCode));
    }

    /// <summary>
    /// <b>The other three axes still classify normally in the same batch.</b> The bad column is simply
    /// not read; it does not disable classification for the axes the profile mapped correctly.
    /// </summary>
    [Fact]
    public async Task The_other_three_axes_still_classify_correctly_alongside_a_bad_one()
    {
        var world = await ArrangeAsync();

        // Personnel, Friars and Special are mapped correctly; Student is mistyped.
        var batchId = await UploadPinnedToProfileAsync(world,
        [
            (SisRosterColumns.StudentCategory, "NotAnAxis"),
            (SisRosterColumns.PersonnelCategory, ClassificationAxis.Personnel),
            (SisRosterColumns.FriarsCategory, ClassificationAxis.Friars),
            (SisRosterColumns.SpecialCategory, ClassificationAxis.Special),
        ]);

        await SisImportOn(NewDbContext()).RunAsync(batchId, world.TermId);

        await using var read = NewDbContext();

        // Pedro's Personnel category (NAP) still resolves, from the correctly mapped Personnel column.
        var pedro = await read.Students.SingleAsync(s => s.StudentNumber == SyntheticRoster.PedroRegNo);
        Assert.Equal(SyntheticRoster.PersonnelCategory, await read.StudentClassifications
            .Where(a => a.StudentId == pedro.Id && a.Axis == ClassificationAxis.Personnel)
            .Select(a => a.Classification!.Name)
            .SingleAsync());

        // Ana's Special category (C2B2) still resolves.
        var ana = await read.Students.SingleAsync(s => s.StudentNumber == SyntheticRoster.AnaRegNo);
        Assert.Equal(SyntheticRoster.SpecialCategory, await read.StudentClassifications
            .Where(a => a.StudentId == ana.Id && a.Axis == ClassificationAxis.Special)
            .Select(a => a.Classification!.Name)
            .SingleAsync());

        // But nobody is classified on the Student axis at all — the mistyped column was never read.
        Assert.False(await read.StudentClassifications.AnyAsync(a => a.Axis == ClassificationAxis.Student));
    }

    // ==================================================================== precedence over the code slot

    /// <summary>
    /// <b>The unknown-axis warning wins the single <c>WarningCode</c> slot over every other code on the
    /// same row</b> — pinned against <see cref="SisImportWarningCode.StudentIdentityConflict"/>, which
    /// sits second in <c>RowLedger.WarningPrecedence</c>. Both warnings are present in the ledger; only
    /// one survives onto <c>WarningCode</c>, and it must be the axis one — but the identity-conflict
    /// text must still be readable in <c>WarningMessage</c>, which carries every warning.
    /// </summary>
    [Fact]
    public async Task It_wins_the_warning_code_slot_over_a_lower_precedence_code_on_the_same_row()
    {
        var world = await ArrangeAsync();

        // Rosa's two rows, contradicting each other on the Personnel category — this is exactly the
        // fixture SisImportClassificationTests.Two_rows_contradicting_each_other_on_one_axis_are_reported
        // uses to raise StudentIdentityConflict, reused here so it fires alongside our bad axis mapping.
        var rows = SyntheticRoster.Rows();
        rows[7][SyntheticRoster.ColumnIndex(SisRosterColumns.PersonnelCategory)] =
            SyntheticRoster.PersonnelCategory;
        rows[8][SyntheticRoster.ColumnIndex(SisRosterColumns.PersonnelCategory)] = "ACAD";
        await using var file = SyntheticRoster.Build(rows);

        // The Personnel column must be mapped correctly, or the identity-conflict check has no
        // resolved category to compare across Rosa's two rows in the first place. Student stays
        // mistyped, which is the fault under test.
        var batchId = await UploadPinnedToProfileAsync(world,
        [
            (SisRosterColumns.StudentCategory, "Faculty"),
            (SisRosterColumns.PersonnelCategory, ClassificationAxis.Personnel),
        ], file);

        var batch = await SisImportOn(NewDbContext()).RunAsync(batchId, world.TermId);
        var rowDtos = await RowsOfAsync(batchId);

        // Rosa's second row (row number 10) is the one that names the identity conflict.
        var rosaRow = rowDtos.Single(r => r.RowNumber == 10);

        Assert.Equal(SisImportWarningCode.ClassificationAxisUnknown, rosaRow.WarningCode);
        Assert.Contains($"[{SisImportWarningCode.StudentIdentityConflict}]", rosaRow.WarningMessage!,
            StringComparison.Ordinal);
        Assert.Contains($"[{SisImportWarningCode.ClassificationAxisUnknown}]", rosaRow.WarningMessage!,
            StringComparison.Ordinal);

        // Every other row of the batch still carries the axis code — the batch-wide claim holds even on
        // a batch that also has a per-row anomaly.
        Assert.Equal(batch.TotalRows, batch.WarningRows);
    }

    // ============================================================================ status transition

    /// <summary>
    /// <b>A batch with the mapping fault and no failed rows completes as <c>CompletedWithWarnings</c></b>
    /// — this fixture, clean, previously reported plain <c>Completed</c> (see
    /// <see cref="SyntheticRoster.RowsWithoutWarnings"/>'s remarks on the un-mutated three rows), so this
    /// status is entirely the new warning's doing.
    /// </summary>
    [Fact]
    public async Task A_batch_with_the_bad_mapping_and_no_row_failures_completes_with_warnings()
    {
        var world = await ArrangeAsync();
        var batchId = await UploadPinnedToProfileAsync(
            world, [(SisRosterColumns.StudentCategory, "Faculty")]);

        var batch = await SisImportOn(NewDbContext()).RunAsync(batchId, world.TermId);

        Assert.Equal(0, batch.FailedRows);
        Assert.Equal(SisImportStatus.CompletedWithWarnings, batch.Status);
    }

    /// <summary>
    /// <b>The same mapping fault on a batch that also has a failed row completes as
    /// <c>CompletedWithErrors</c></b> — the warning fires before the REGNO check (so the failed row
    /// still carries it), but a failure anywhere in the batch still outranks a warning for the batch's
    /// own status.
    /// </summary>
    [Fact]
    public async Task A_batch_with_the_bad_mapping_and_a_failed_row_completes_with_errors()
    {
        var world = await ArrangeAsync();

        var rows = SyntheticRoster.Rows();
        rows[0][SyntheticRoster.ColumnIndex(SisRosterColumns.RegNo)] = "";
        await using var file = SyntheticRoster.Build(rows);

        var batchId = await UploadPinnedToProfileAsync(
            world, [(SisRosterColumns.StudentCategory, "Faculty")], file);

        var batch = await SisImportOn(NewDbContext()).RunAsync(batchId, world.TermId);

        Assert.True(batch.FailedRows > 0);
        Assert.Equal(SisImportStatus.CompletedWithErrors, batch.Status);

        // And the failed row still carries the axis warning — raised before the REGNO check, so a row
        // that never gets far enough to be classified still carries it.
        var failed = (await RowsOfAsync(batchId)).Single(r => r.Result == SisImportRowResult.Failed);
        Assert.Equal(SisImportWarningCode.ClassificationAxisUnknown, failed.WarningCode);

        // The signature still holds: every row, failed or not, is warned.
        Assert.Equal(batch.TotalRows, batch.WarningRows);
    }

    // ==================================================================== resolved from the pinned version

    /// <summary>
    /// <b>The warning is resolved from the batch's own pinned profile version, so a historical batch
    /// pinned to a bad version reports it on re-run</b> — "re-run" here meaning what
    /// <see cref="SisImportClassificationTests.A_batch_pinned_to_the_pre_Task_5_profile_version_classifies_nobody"/>
    /// means it, and for the same reason: <c>RunAsync</c> refuses to execute an already-completed batch
    /// a second time (a completed batch is a historical record), so "re-running" a batch that pinned a
    /// stale mapping is a fresh upload pinned to that same, untouched profile row. Two separately
    /// uploaded batches pinned to the identical bad profile version both report the fault, proving it is
    /// read from the pinned version and not cached from the first run that happened to see it.
    /// </summary>
    [Fact]
    public async Task A_batch_pinned_to_a_bad_axis_mapping_reports_it_on_every_batch_pinned_to_it()
    {
        var world = await ArrangeAsync();

        Guid badProfileId;
        var firstBatchId = await UploadPinnedToProfileAsync(
            world, [(SisRosterColumns.StudentCategory, "Faculty")]);

        var first = await SisImportOn(NewDbContext()).RunAsync(firstBatchId, world.TermId);
        Assert.Equal(first.TotalRows, first.WarningRows);

        await using (var db = NewDbContext())
        {
            badProfileId = await db.SisImportBatches.IgnoreQueryFilters()
                .Where(b => b.Id == firstBatchId)
                .Select(b => b.ImportProfileId!.Value)
                .SingleAsync();
        }

        // A second, independent batch, uploaded later and pinned by hand to that same historical
        // profile row — the untouched mapping is what a later re-run of a stale batch executes against.
        Guid secondBatchId;
        await using (var content = SyntheticRoster.Build())
        await using (var db = NewDbContext())
        {
            var preview = await SisImportOn(db).UploadAsync(
                new SisImportUploadRequest(content, "Copy-of-CCJ.xlsx", world.TermId));
            secondBatchId = preview.Batch.Id;

            var batch = await db.SisImportBatches.IgnoreQueryFilters()
                .SingleAsync(b => b.Id == secondBatchId);
            batch.ImportProfileId = badProfileId;
            await db.SaveChangesAsync();
        }

        var second = await SisImportOn(NewDbContext()).RunAsync(secondBatchId, world.TermId);

        Assert.Equal(second.TotalRows, second.WarningRows);
        Assert.All(await RowsOfAsync(secondBatchId), r =>
            Assert.Equal(SisImportWarningCode.ClassificationAxisUnknown, r.WarningCode));
    }

    // =============================== the SECOND condition folded into the same code (SisImportService.cs :526)

    /// <summary>
    /// <b>Pins the second condition that shares <see cref="SisImportWarningCode.ClassificationAxisUnknown"/>:</b>
    /// the axis IS one of the four valid ones, but the profile row names neither a source column nor a
    /// source key, so <c>KeyOf</c> (<c>SisImportService.cs</c>, around line 526) returns <c>null</c> and
    /// there is no cell to read. An empty <c>SourceColumn</c> triggers it through
    /// <see cref="UploadPinnedToProfileAsync"/> unassisted: that helper always derives
    /// <c>SourceColumnKey</c> from <c>SourceColumn</c> via <c>SisRosterColumns.HeaderKey</c>, and
    /// <c>HeaderKey("")</c> is itself empty, so both halves <c>KeyOf</c> checks come up empty at once.
    /// </summary>
    [Fact]
    public async Task An_axis_with_no_usable_source_column_at_all_is_reported()
    {
        var world = await ArrangeAsync();

        // Student, correctly spelled, mapped to an empty source column. No other profile row names
        // Student, so the axis ends the loop with nothing usable at all.
        var batchId = await UploadPinnedToProfileAsync(world, [("", ClassificationAxis.Student)]);

        var batch = await SisImportOn(NewDbContext()).RunAsync(batchId, world.TermId);

        Assert.Equal(batch.TotalRows, batch.WarningRows);
        var rows = await RowsOfAsync(batchId);
        Assert.Equal(SyntheticRoster.DataRowCount, rows.Count);
        Assert.All(rows, r => Assert.Equal(SisImportWarningCode.ClassificationAxisUnknown, r.WarningCode));

        // The early fragment only, never the trailing remedy sentence — a row carrying both clauses
        // (this one and the unknown-axis one, or several unreadable targets) plus their siblings can
        // clamp WarningMessage at 1,000 characters with "…", which would cut the remedy sentence first.
        Assert.All(rows, r => Assert.Contains(
            "This batch's import profile maps 'StudentClassification.Student' with no source column " +
            "it can read:", r.WarningMessage!, StringComparison.Ordinal));

        // And nobody is classified on Student — the column was never read.
        await using var read = NewDbContext();
        Assert.False(await read.StudentClassifications.AnyAsync(a => a.Axis == ClassificationAxis.Student));
    }

    /// <summary>
    /// <b>The refinement that most needs pinning.</b> The implementer does not warn at the <c>KeyOf</c>
    /// guard — it collects the unusable row into <c>unreadable</c> and only reports it AFTER the whole
    /// loop, filtered against the axes actually resolved into <c>byAxis</c>. So an unusable first row
    /// for an axis, followed by a usable later row for that <em>same</em> axis, classifies everybody
    /// normally on it and stays completely silent: the axis was rescued before the filter ever ran. A
    /// future refactor that warns eagerly at the guard instead of collecting-then-filtering would break
    /// this silently, which is exactly what makes it worth pinning.
    /// </summary>
    [Fact]
    public async Task An_axis_rescued_by_a_later_usable_column_stays_silent()
    {
        var world = await ArrangeAsync();

        // Student's first mapping is unusable (empty column); its second mapping, for the same axis, is
        // the real StudentCategory column and rescues it. The other three axes are mapped normally so
        // this reads as an ordinary, fully-mapped batch rather than one missing three axes' worth of
        // profile rows.
        var batchId = await UploadPinnedToProfileAsync(world,
        [
            ("", ClassificationAxis.Student),
            (SisRosterColumns.StudentCategory, ClassificationAxis.Student),
            (SisRosterColumns.PersonnelCategory, ClassificationAxis.Personnel),
            (SisRosterColumns.FriarsCategory, ClassificationAxis.Friars),
            (SisRosterColumns.SpecialCategory, ClassificationAxis.Special),
        ]);

        var batch = await SisImportOn(NewDbContext()).RunAsync(batchId, world.TermId);

        // No trace of the unknown/unreadable-axis code anywhere in the batch.
        Assert.DoesNotContain(SisImportWarningCode.ClassificationAxisUnknown,
            (await RowsOfAsync(batchId)).Select(r => r.WarningCode));

        // And the rescue actually worked — Student classified normally, from the rescuing column, not
        // silently skipped the way ClassificationMissing/ClassificationAxisUnknown would leave it.
        await using var read = NewDbContext();

        var pedro = await read.Students.SingleAsync(s => s.StudentNumber == SyntheticRoster.PedroRegNo);
        Assert.Equal(SyntheticRoster.PersonnelCategory, await read.StudentClassifications
            .Where(a => a.StudentId == pedro.Id && a.Axis == ClassificationAxis.Personnel)
            .Select(a => a.Classification!.Name)
            .SingleAsync());

        var ana = await read.Students.SingleAsync(s => s.StudentNumber == SyntheticRoster.AnaRegNo);
        Assert.Equal(SyntheticRoster.SpecialCategory, await read.StudentClassifications
            .Where(a => a.StudentId == ana.Id && a.Axis == ClassificationAxis.Special)
            .Select(a => a.Classification!.Name)
            .SingleAsync());
        Assert.Equal(SyntheticRoster.StudentCategory, await read.StudentClassifications
            .Where(a => a.StudentId == ana.Id && a.Axis == ClassificationAxis.Student)
            .Select(a => a.Classification!.Name)
            .SingleAsync());
    }
}
