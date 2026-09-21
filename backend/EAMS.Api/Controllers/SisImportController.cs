using EAMS.Api.Authorization;
using EAMS.Application.Abstractions;
using EAMS.Application.Dtos;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;

namespace EAMS.Api.Controllers;

/// <summary>
/// Technical Plan §10 — the school-year roster import.
///
/// <para>
/// <b>Every endpoint demands a Bearer token carrying <c>sis.import</c>.</b> That matters more here
/// than anywhere else in this API: these endpoints read and write the full roster of every
/// student in the institution, including their names and institutional e-mail addresses, and the
/// upload endpoint writes to the academic tables.
/// </para>
/// </summary>
[ApiController]
[Route("api/v1/sis/import")]
public class SisImportController : ControllerBase
{
    private readonly ISisImportService _import;
    private readonly ISisImportTemplateService _template;

    public SisImportController(ISisImportService import, ISisImportTemplateService template)
    {
        _import = import;
        _template = template;
    }

    /// <summary>The workbook media type, for the template download and nothing else.</summary>
    private const string XlsxContentType =
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    /// <summary>The file name the template downloads as.</summary>
    private const string TemplateFileName = "EAMS-roster-template.xlsx";

    /// <summary>
    /// Maximum upload size. The real roster is 51 KB; ten megabytes is room for a decade of growth and
    /// still small enough that a mistaken upload of something else is rejected before it is buffered.
    ///
    /// <para>
    /// Stated explicitly rather than left to Kestrel's 30 MB default, because an import endpoint is the
    /// one place in an API where an unbounded body is both plausible and expensive: the file is read
    /// into memory twice (hash, then parse).
    /// </para>
    /// </summary>
    private const int MaxUploadBytes = 10 * 1024 * 1024;

    /// <summary>
    /// Stages a workbook and returns what is in it. Writes nothing to the academic tables.
    /// </summary>
    /// <param name="file">The <c>.xlsx</c>, as a multipart form file.</param>
    /// <param name="termId">
    /// The term this roster belongs to. <b>Required, and never inferred</b> — see ADR-001 D-5 and
    /// <c>SisImportBatch.TermId</c>. Guessing it from the filename or the upload date misfiles an entire
    /// batch in a way nothing downstream can detect.
    /// </param>
    [HttpPost("upload")]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Policy = EamsPermissions.SisImport)]
    [HasPermissionNotEnforced(EamsPermissions.SisImport)]
    [RequestSizeLimit(MaxUploadBytes)]
    [ProducesResponseType(typeof(SisImportPreviewDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<ActionResult<SisImportPreviewDto>> Upload(
        IFormFile? file, [FromForm] Guid termId, CancellationToken ct)
    {
        if (file is null || file.Length == 0)
            return BadRequest(ImportProblem(
                StatusCodes.Status400BadRequest,
                "No file was uploaded.",
                "Send the workbook as a multipart/form-data part named 'file', with the term id in a " +
                "part named 'termId'."));

        if (termId == Guid.Empty)
            return BadRequest(ImportProblem(
                StatusCodes.Status400BadRequest,
                "termId is required.",
                "The roster file carries no term. It is operator input (ADR-001 D-5) and inferring it " +
                "from the filename or the upload date would silently misfile the whole batch."));

        // Buffered because the pipeline reads the stream twice — once to fingerprint the bytes, once to
        // parse them — and a multipart section is forward-only.
        await using var buffer = new MemoryStream();
        await file.CopyToAsync(buffer, ct);
        buffer.Position = 0;

        try
        {
            var preview = await _import.UploadAsync(
                new SisImportUploadRequest(buffer, file.FileName, termId), ct);

            return CreatedAtAction(nameof(Get), new { batchId = preview.Batch.Id }, preview);
        }
        catch (SisImportException ex)
        {
            // 422, not 400: the request is well-formed multipart — it is the *content* that cannot be
            // processed. A caller retrying the identical request will get the identical answer, which
            // is what separates the two codes.
            return UnprocessableEntity(ImportProblem(
                StatusCodes.Status422UnprocessableEntity, "The roster could not be read.", ex.Message));
        }
    }

    /// <summary>
    /// <c>GET /sis/import/template</c> — the roster template the school fills in and uploads back.
    /// </summary>
    /// <remarks>
    /// Task 5 (QA MDVault #470 B1, #472 Q1/Q2). An <c>.xlsx</c> with two sheets: <b>Instructions</b>,
    /// and <b>Roster</b>, whose header row is the source columns of the import profile this school's next
    /// upload will be pinned to (ADR-001 D-4), in profile order — the roster's own headers plus the four
    /// category columns <c>STUDENT_CATEGORY</c>, <c>PERSONNEL_CATEGORY</c>, <c>FRIARS_CATEGORY</c> and
    /// <c>SPECIAL_CATEGORY</c>. Any header the reader requires to recognise the roster sheet that the
    /// profile omits is appended, so the template is always accepted as a roster. A newer active profile
    /// version changes the template's columns; it does not change which headers the reader requires or
    /// the fixed headers the importer reads the core values from. Headers whose value is required carry a
    /// comment; the card-serial and student-number columns are formatted as Text so leading zeros
    /// survive. Sent with <c>Cache-Control: no-store</c>, because it embeds the school's current
    /// classification list.
    /// </remarks>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">The workbook, as an attachment.</response>
    /// <response code="409">No school could be resolved for the caller.</response>
    [HttpGet("template")]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Policy = EamsPermissions.SisImport)]
    [HasPermissionNotEnforced(EamsPermissions.SisImport)]
    [ProducesResponseType(typeof(FileStreamResult), StatusCodes.Status200OK, XlsxContentType)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Template(CancellationToken ct)
    {
        var workbook = await _template.BuildAsync(ct);
        if (workbook is null)
            return Conflict(ImportProblem(
                StatusCodes.Status409Conflict,
                "No school could be resolved.",
                "The template is built from this school's import profile and classification list, and " +
                "no school could be resolved for this request."));

        // Never cached: the file lists this school's classification vocabulary as of now, and a cached
        // copy would advertise a category an administrator has since retired.
        Response.Headers.CacheControl = "no-store";

        // FileStreamResult disposes the stream after writing it, and writes it asynchronously.
        return File(workbook, XlsxContentType, TemplateFileName);
    }

    /// <summary>
    /// Runs a staged batch. Idempotent with respect to the database: running the same roster twice
    /// leaves it identical and reports every row <c>Skipped</c>.
    /// </summary>
    [HttpPost("{batchId:guid}/run")]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Policy = EamsPermissions.SisImport)]
    [HasPermissionNotEnforced(EamsPermissions.SisImport)]
    [ProducesResponseType(typeof(SisImportBatchDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<SisImportBatchDto>> Run(
        Guid batchId, [FromBody] SisImportRunRequest request)
    {
        if (request.TermId == Guid.Empty)
            return BadRequest(ImportProblem(
                StatusCodes.Status400BadRequest,
                "termId is required.",
                "The run confirms the term the batch was uploaded for. See SisImportRunRequest."));

        try
        {
            // CancellationToken.None, NOT the request's `ct`, and this is the one line that stops a
            // closed tab corrupting an import.
            //
            // MVC binds `ct` to HttpContext.RequestAborted, so the browser giving up cancels it. That
            // is right for a read and wrong for this: RunAsync has already committed `Running`, and
            // ExecuteAsync commits in passes with no outer transaction, so a cancellation lands
            // mid-write. Worse, RunAsync's recovery handler deliberately does not treat cancellation as
            // a failure, so the batch was left in `Running` with no FinishedAt - a state that is not in
            // (Pending | Failed) and therefore cannot be re-run at all. Two such batches were produced
            // on the deployment VM by a 21,497-row roster and a 15-second client budget, and neither
            // could be retried through the API.
            //
            // The run is now uncancellable by the client. It either finishes or throws, and both of
            // those leave a batch an operator can act on. The parameter is gone from the signature
            // rather than bound and ignored, so there is nothing here for a later edit to pass by
            // reflex. This does NOT cover an app-pool recycle mid-run: that kills the process, no
            // handler runs, and the batch is a zombie again. Only moving the run off the request
            // thread fixes that one.
            return Ok(await _import.RunAsync(batchId, request.TermId, CancellationToken.None));
        }
        catch (SisImportBatchNotFoundException ex)
        {
            // Caught before the base type, and that ordering is the fix: an unknown batch used to fall
            // into the 409 below, so a caller could not tell "this id never existed" from "this batch
            // has already run" — two problems with different fixes reported identically. 404 is also
            // what GET on the same id returns, so the two verbs now agree about what exists.
            return NotFound(ImportProblem(
                StatusCodes.Status404NotFound, "No such import batch.", ex.Message));
        }
        catch (SisImportException ex)
        {
            // 409: the batch exists and the request is well-formed, but the state of the resource
            // forbids the operation — a term mismatch, or a batch that has already run.
            return Conflict(ImportProblem(
                StatusCodes.Status409Conflict, "The batch cannot be run.", ex.Message));
        }
    }

    /// <summary>
    /// <c>GET /sis/import/{batchId}</c> — one import batch and its counts.
    /// </summary>
    /// <remarks>
    /// The batch header, not its rows: totals and status, with the staged rows behind
    /// <c>GET /sis/import/{batchId}/rows</c>. This is the poll target between uploading a batch and
    /// running it, and it is what makes a <c>404</c> from <c>POST .../run</c> unambiguous — both verbs
    /// agree about which batches exist.
    /// </remarks>
    /// <param name="batchId">The batch.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <response code="200">The batch.</response>
    /// <response code="404">No such batch.</response>
    [HttpGet("{batchId:guid}")]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Policy = EamsPermissions.SisImport)]
    [HasPermissionNotEnforced(EamsPermissions.SisImport)]
    [ProducesResponseType(typeof(SisImportBatchDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<SisImportBatchDto>> Get(Guid batchId, CancellationToken ct)
    {
        var batch = await _import.GetAsync(batchId, ct);
        return batch is null ? NotFound() : Ok(batch);
    }

    /// <summary>
    /// A batch's staged rows, optionally narrowed to one outcome —
    /// <c>?result=Failed</c> is the query an operator runs after every import.
    /// </summary>
    [HttpGet("{batchId:guid}/rows")]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Policy = EamsPermissions.SisImport)]
    [HasPermissionNotEnforced(EamsPermissions.SisImport)]
    [ProducesResponseType(typeof(IEnumerable<SisImportRowDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IEnumerable<SisImportRowDto>>> Rows(
        Guid batchId, [FromQuery] string? result, CancellationToken ct)
        => Ok(await _import.GetRowsAsync(batchId, result, ct));

    /// <summary>
    /// §6's declared error shape (RFC 7807), composed here rather than thrown so the operator gets the
    /// pipeline's own sentence instead of a generic status text.
    ///
    /// <para>
    /// <b>Built through <see cref="ProblemDetailsFactory"/> rather than with <c>new</c>.</b> These
    /// bodies used to stamp their own <c>traceId</c>, because <c>Program.cs</c>'s
    /// <c>CustomizeProblemDetails</c> callback belongs to <c>IProblemDetailsService</c> and never sees
    /// a ProblemDetails an action returns as its own result. Stamping it here fixed these four
    /// responses and left every <c>[ApiController]</c> model-validation 400 in the API without one.
    /// The factory is the seam both paths share, so it is now stamped once, there — see
    /// <c>TracedProblemDetailsFactory</c>.
    /// </para>
    ///
    /// <para>
    /// <paramref name="status"/> is passed explicitly because the body used to serialize
    /// <c>"status": null</c> while the response carried a real code, leaving a client to reconcile two
    /// sources for one fact.
    /// </para>
    /// </summary>
    private ProblemDetails ImportProblem(int status, string title, string detail) =>
        ProblemDetailsFactory.CreateProblemDetails(
            HttpContext, statusCode: status, title: title, detail: detail);
}
