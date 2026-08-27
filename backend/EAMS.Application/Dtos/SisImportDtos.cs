using EAMS.Domain;

namespace EAMS.Application.Dtos;

/// <summary>
/// A batch's summary. The five outcome counters are the reconciliation ADR-001 D-5 exists to make
/// possible: <c>InsertedRows + UpdatedRows + FailedRows + SkippedRows == TotalRows</c> on a
/// <em>terminal</em> batch. <see cref="WarningRows"/> is orthogonal — a warned row is already counted
/// in one of the four.
///
/// <para>
/// The <c>Progress*</c> fields are all nullable and all NULL on a batch that has never reported
/// progress — see <c>SisImportBatch</c> for why there is no zero-valued alternative.
/// </para>
/// </summary>
public record SisImportBatchDto(
    Guid Id,
    Guid TermId,
    string TermCode,
    string Source,
    string? FileName,
    string? SourceSheetName,
    string? FileHash,
    string Status,
    int TotalRows,
    int InsertedRows,
    int UpdatedRows,
    int FailedRows,
    int SkippedRows,
    int WarningRows,
    DateTime? StartedAt,
    DateTime? FinishedAt,
    string? ProgressPhase,
    int? ProgressPhaseNumber,
    int? ProgressPhaseCount,
    int? ProgressUnitsDone,
    int? ProgressUnitsTotal,
    DateTime? ProgressUpdatedAt,
    string? FailureReason)
{
    /// <summary>
    /// Whether the counters add up. Exposed rather than left for the caller to compute because it is
    /// the one check an operator runs, and a UI that has to derive it will eventually derive it wrong.
    ///
    /// <para>
    /// <b>It is a statement about a terminal batch, and only about a terminal batch.</b> On a
    /// <c>Pending</c> or <c>Running</c> one it is <em>expected</em> to be false: <c>TotalRows</c> is
    /// the staged row count from the moment of upload, while the four outcome counters stay at zero
    /// until the tally at the end of the run. That was true before anything polled a running batch, so
    /// nobody ever saw it — the only reader was a finished run's summary.
    /// </para>
    ///
    /// <para>
    /// <b>The computation is deliberately unchanged, and must stay unchanged.</b> The obvious "fix" —
    /// returning <c>true</c> while <see cref="IsTerminal"/> is false — would make a reconciliation
    /// check that cannot fail during the only window in which the pipeline is actually writing the
    /// numbers it reconciles. A check that is true by construction proves nothing. So this stays
    /// arithmetic, and it is the <em>caller's</em> job to ask it only of a batch whose run is over:
    /// gate on <see cref="IsTerminal"/>.
    /// </para>
    /// </summary>
    public bool CountersReconcile =>
        InsertedRows + UpdatedRows + FailedRows + SkippedRows == TotalRows;

    /// <summary>
    /// Whether the run is over — i.e. whether polling should stop and the results are worth reading.
    ///
    /// <para>
    /// <b>Expressed as "not one of the two live states" rather than as a list of the four finished
    /// ones, and the two are not equivalent under drift.</b> A status this build has never heard of is
    /// classified <em>terminal</em> here, which is the safe direction: the alternative leaves a poller
    /// waiting forever on a run it will never recognise as finished. It is also the only reading under
    /// which a batch whose status is somehow corrupt can be looked at at all.
    /// </para>
    ///
    /// <para>
    /// <b>This must agree, value for value, with the SPA's <c>isTerminalStatus</c> in
    /// <c>web-admin/src/sisImport.ts</c>, which is written the same way and for the same reason.</b>
    /// The two are the same predicate on two sides of the wire: if they disagree, one side stops
    /// polling while the other still shows a spinner, or the results step renders over a batch that
    /// has not run. Changing either without the other is the defect. There is a unit test pinning this
    /// side against the whole of <see cref="SisImportStatus.All"/> plus a value from outside the set.
    /// </para>
    /// </summary>
    public bool IsTerminal =>
        Status is not (SisImportStatus.Pending or SisImportStatus.Running);
}

/// <summary>One staged source row and what became of it.</summary>
public record SisImportRowDto(
    Guid Id,
    int RowNumber,
    string Result,
    string? SkipReason,
    string? WarningCode,
    string? WarningMessage,
    string? ErrorMessage,
    Guid? StudentId,
    string? RawData,
    IReadOnlyList<SisImportRowEntityDto> Entities);

/// <summary>
/// One entity a row touched. The answer to "row 214 says Skipped — against what?", which §4.12's single
/// nullable <c>StudentId</c> could not give for a source whose grain spans six entities.
/// </summary>
public record SisImportRowEntityDto(string EntityType, Guid EntityId, string Action);

/// <summary>
/// What an upload found, before anything is written to the academic tables.
///
/// <para>
/// The distinct counts are the preview's whole purpose: they are what an operator compares against what
/// they expect the file to contain, and they are cheap because they come from the staged rows rather
/// than from a trial run. A file with one course in it is a wrong file, and this is where that is
/// visible — before the run, not after.
/// </para>
/// </summary>
public record SisImportPreviewDto(
    SisImportBatchDto Batch,
    IReadOnlyList<string> Columns,
    int DistinctStudents,
    int DistinctColleges,
    int DistinctPrograms,
    int DistinctCourses,
    int DistinctSections,
    int DistinctInstructors,
    int BlankSectionRows,
    int PlaceholderInstructorRows,
    IReadOnlyList<SisImportRowDto> SampleRows);

/// <summary>
/// The body of <c>POST /sis/import/{batchId}/run</c>.
///
/// <para>
/// <b>The term is required here even though the batch already carries one.</b> It is a confirmation,
/// and it must match or the run is refused. ADR-001 D-5 names the failure this guards: a wrong term
/// selection misfiles an entire batch, is invisible afterwards (every downstream query is term-scoped,
/// so the data looks fine — it is simply in the wrong year), and is only recoverable by hand. Asking
/// twice, at upload and at the moment of writing, costs one field and turns the mistake into a 409.
/// </para>
/// </summary>
public record SisImportRunRequest(Guid TermId);
