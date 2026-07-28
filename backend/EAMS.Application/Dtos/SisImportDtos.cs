namespace EAMS.Application.Dtos;

/// <summary>
/// A batch's summary. The five outcome counters are the reconciliation ADR-001 D-5 exists to make
/// possible: <c>InsertedRows + UpdatedRows + FailedRows + SkippedRows == TotalRows</c>, always.
/// <see cref="WarningRows"/> is orthogonal — a warned row is already counted in one of the four.
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
    DateTime? FinishedAt)
{
    /// <summary>
    /// Whether the counters add up. Exposed rather than left for the caller to compute because it is
    /// the one check an operator runs, and a UI that has to derive it will eventually derive it wrong.
    /// </summary>
    public bool CountersReconcile =>
        InsertedRows + UpdatedRows + FailedRows + SkippedRows == TotalRows;
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
