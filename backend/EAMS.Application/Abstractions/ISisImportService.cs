using EAMS.Application.Dtos;

namespace EAMS.Application.Abstractions;

/// <summary>
/// Technical Plan §10 — the school-year roster import. Implemented in EAMS.Infrastructure; controllers
/// see only this.
///
/// <para>
/// <b>Upload and run are two calls on purpose.</b> Uploading stages the file and reports what is in it;
/// running writes it to the academic tables. Splitting them is what makes the preview real — an
/// operator gets to see "52 students, 21 courses, 38 sections" and stop before anything is written,
/// which is the only cheap moment to catch the wrong file or the wrong term. A single call would put
/// that check after the write.
/// </para>
///
/// <para>
/// <b>The whole pipeline is upsert-only. It never deletes and never marks anything dropped.</b>
/// Re-importing into a term updates what changed; importing into a new term creates a parallel set and
/// leaves the previous one exactly as it was. A student who disappears from the file is <em>not</em>
/// unenrolled — the roster is a snapshot of who is in a class, not an instruction to remove anyone, and
/// an export truncated by a registrar's filter would otherwise silently empty a term. Removing a
/// student is a deliberate back-office action with a person behind it.
/// </para>
/// </summary>
public interface ISisImportService
{
    /// <summary>
    /// Reads the workbook, stages every row of its roster sheet, and returns a preview. Writes nothing
    /// to the academic tables.
    /// </summary>
    /// <param name="request">The file, its name, and the operator-declared term.</param>
    /// <exception cref="SisImportException">
    /// The stream is not a workbook this pipeline can read — no worksheet carries the required columns,
    /// or the term does not exist. Thrown rather than reported as a batch, because there is nothing to
    /// stage: a batch row describing a file that could not be parsed would be a permanent artifact of a
    /// typo.
    /// </exception>
    Task<SisImportPreviewDto> UploadAsync(SisImportUploadRequest request, CancellationToken ct = default);

    /// <summary>
    /// Runs a staged batch: resolves dimensions, writes facts, refreshes the derived student cache, and
    /// projects the term's student groups.
    ///
    /// <para>
    /// Idempotent. Running the same batch twice, or uploading and running the same file twice, leaves
    /// the database identical and reports every row as <c>Skipped</c>.
    /// </para>
    /// </summary>
    /// <param name="termId">
    /// Must equal the batch's declared term. See <see cref="SisImportRunRequest"/> for why it is asked
    /// for twice.
    /// </param>
    /// <exception cref="SisImportBatchNotFoundException">There is no such batch.</exception>
    /// <exception cref="SisImportException">
    /// The term does not match, or the batch has already been run.
    /// </exception>
    Task<SisImportBatchDto> RunAsync(Guid batchId, Guid termId, CancellationToken ct = default);

    /// <summary>The batch summary, or <c>null</c> if there is no such batch.</summary>
    Task<SisImportBatchDto?> GetAsync(Guid batchId, CancellationToken ct = default);

    /// <summary>
    /// The batch's staged rows, optionally narrowed to one <c>Result</c>.
    /// </summary>
    /// <param name="result">
    /// A <c>SisImportRowResult</c> value. Matched case-insensitively; an unrecognised value returns
    /// nothing rather than everything, because a filter typo that silently widens the result set is how
    /// an operator concludes a clean import had 536 failures.
    /// </param>
    Task<IReadOnlyList<SisImportRowDto>> GetRowsAsync(
        Guid batchId, string? result, CancellationToken ct = default);
}

/// <summary>
/// An upload. Carries a stream rather than a framework file type so the Application layer stays free of
/// ASP.NET Core (Technical Plan §3).
/// </summary>
/// <param name="Content">
/// The workbook. Read to the end twice — once to hash, once to parse — so it must be seekable; a
/// controller buffers a multipart section before calling.
/// </param>
/// <param name="FileName">As uploaded. Recorded, never parsed — see <c>SisImportBatch.TermId</c>.</param>
/// <param name="TermId">The operator-declared term. Required.</param>
public record SisImportUploadRequest(Stream Content, string? FileName, Guid TermId);

/// <summary>
/// A refusal at the import boundary: something about the request or the file makes the operation
/// impossible, and the caller can act on it.
///
/// <para>
/// Typed rather than <c>InvalidOperationException</c> so the API layer can map it to a 4xx without
/// catching everything — an unexpected fault must still surface as a 500 with a trace id rather than
/// being reported to the operator as their mistake.
/// </para>
/// </summary>
public class SisImportException : Exception
{
    public SisImportException(string message) : base(message) { }

    public SisImportException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// The batch named by the request does not exist.
///
/// <para>
/// <b>Split out of <see cref="SisImportException"/> so "never existed" and "already ran" stop being the
/// same answer.</b> Both used to surface as 409, which is wrong for the first and actively misleading:
/// a client that mistypes a batch id, or polls one that was cleaned up, is told the batch is in a state
/// that forbids running — so the obvious next move is to go and look at a batch that is not there. The
/// two have different fixes (correct the id vs. upload the file again), so they get different codes.
/// </para>
///
/// <para>
/// Deliberately narrow: only the batch lookup throws it. An unknown <em>term</em> is still the base type
/// — that is a 422 on upload, because the request named something the caller supplied about content
/// rather than a missing resource at the URL.
/// </para>
/// </summary>
public sealed class SisImportBatchNotFoundException : SisImportException
{
    public SisImportBatchNotFoundException(string message) : base(message) { }
}
