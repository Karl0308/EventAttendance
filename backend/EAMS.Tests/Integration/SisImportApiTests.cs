using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EAMS.Api.Authorization;
using EAMS.Domain;
using EAMS.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EAMS.Tests.Integration;

/// <summary>
/// The HTTP surface of the §10 import: which outcome becomes which status code, and what the
/// multipart contract actually is.
///
/// <para>
/// The service-level tests call <see cref="EAMS.Application.Abstractions.ISisImportService"/> directly
/// and cannot see any of this. A term mismatch quietly returning 200-with-zero-rows, or the upload
/// silently ignoring the <c>termId</c> form field because the binder never saw it, would break an
/// operator's workflow without failing one of them.
/// </para>
/// </summary>
[Collection(DatabaseCollection.Name)]
public class SisImportApiTests : IntegrationTest
{
    public SisImportApiTests(SqlServerFixture sql) : base(sql) { }

    private const string UploadPath = "/api/v1/sis/import/upload";

    private sealed record World(Guid SchoolId, Guid TermId);

    private async Task<World> ArrangeAsync()
    {
        await using var db = NewDbContext();
        var school = TestData.NewSchool();
        db.Schools.Add(school);
        var term = TestData.NewTerm(school.Id);
        db.Terms.Add(term);
        await db.SaveChangesAsync();
        return new World(school.Id, term.Id);
    }

    private static async Task<HttpResponseMessage> UploadAsync(
        HttpClient client, Guid termId, Stream? file = null, string fileName = "Copy-of-CCJ.xlsx")
    {
        using var form = new MultipartFormDataContent();

        if (file is not null)
        {
            var content = new StreamContent(file);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
            form.Add(content, "file", fileName);
        }

        form.Add(new StringContent(termId.ToString()), "termId");

        return await client.PostAsync(UploadPath, form);
    }

    private static async Task<Guid> BatchIdOfAsync(HttpResponseMessage response)
    {
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("batch").GetProperty("id").GetGuid();
    }

    // ------------------------------------------------------------------------- upload

    /// <summary>
    /// The happy path, and the shape a back-office screen codes against: 201 with a <c>Location</c>
    /// header pointing at the batch, and a preview body describing what is in the file.
    /// </summary>
    [Fact]
    public async Task A_valid_upload_is_201_with_a_location_header_and_a_preview()
    {
        var world = await ArrangeAsync();
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient();

        await using var file = SyntheticRoster.Build();
        var response = await UploadAsync(client, world.TermId, file);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.NotNull(response.Headers.Location);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var batch = body.RootElement.GetProperty("batch");

        Assert.Equal(SisImportStatus.Pending, batch.GetProperty("status").GetString());
        Assert.Equal(SyntheticRoster.DataRowCount, batch.GetProperty("totalRows").GetInt32());
        Assert.Equal(SyntheticRoster.SheetName, batch.GetProperty("sourceSheetName").GetString());
        Assert.Equal(world.TermId, batch.GetProperty("termId").GetGuid());
        // The preview survives camelCase serialization intact — a .NET `DistinctStudents` reaching the
        // client as `distinctStudents` is the whole chain a back-office grid binds to.
        Assert.Equal(8, body.RootElement.GetProperty("distinctStudents").GetInt32());
        Assert.Equal(5, body.RootElement.GetProperty("distinctCourses").GetInt32());
        Assert.Equal(2, body.RootElement.GetProperty("placeholderInstructorRows").GetInt32());

        // Nothing was written to the academic tables by an upload.
        await using var read = NewDbContext();
        Assert.Equal(0, await read.Students.CountAsync());
    }

    [Fact]
    public async Task An_upload_with_no_file_is_400()
    {
        var world = await ArrangeAsync();
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient();

        var response = await UploadAsync(client, world.TermId, file: null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>
    /// ADR-001 D-5's whole point, enforced at the boundary: no term, no import. It is not inferred from
    /// the filename or the upload date, and there is no default.
    /// </summary>
    [Fact]
    public async Task An_upload_with_no_term_is_400_and_says_why()
    {
        await ArrangeAsync();
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient();

        await using var file = SyntheticRoster.Build();
        var response = await UploadAsync(client, Guid.Empty, file);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("termId", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    /// <summary>
    /// 422, not 400: the request is well-formed multipart — it is the <em>content</em> that cannot be
    /// processed, so retrying the identical request gives the identical answer.
    /// </summary>
    [Fact]
    public async Task A_file_that_is_not_a_readable_workbook_is_422()
    {
        var world = await ArrangeAsync();
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient();

        await using var notAWorkbook = new MemoryStream("REGNO,NAME\r\nUSA00001,Maria"u8.ToArray());
        var response = await UploadAsync(client, world.TermId, notAWorkbook, "roster.csv");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains(".xlsx", body, StringComparison.Ordinal);

        // §6's declared error shape, with the trace id Program.cs attaches.
        using var problem = JsonDocument.Parse(body);
        Assert.True(problem.RootElement.TryGetProperty("traceId", out _));
        Assert.True(problem.RootElement.TryGetProperty("detail", out _));
    }

    [Fact]
    public async Task An_upload_against_an_unknown_term_is_422()
    {
        await ArrangeAsync();
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient();

        await using var file = SyntheticRoster.Build();
        var response = await UploadAsync(client, Guid.NewGuid(), file);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    // ---------------------------------------------------------------------------- run

    [Fact]
    public async Task A_run_is_200_and_returns_counters_that_reconcile()
    {
        var world = await ArrangeAsync();
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient();

        await using var file = SyntheticRoster.Build();
        var batchId = await BatchIdOfAsync(await UploadAsync(client, world.TermId, file));

        var response = await client.PostAsJsonAsync(
            $"/api/v1/sis/import/{batchId}/run", new { termId = world.TermId });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = body.RootElement;

        Assert.Equal(SisImportStatus.CompletedWithWarnings, root.GetProperty("status").GetString());
        Assert.Equal(SyntheticRoster.DataRowCount, root.GetProperty("totalRows").GetInt32());
        Assert.True(root.GetProperty("countersReconcile").GetBoolean());

        await using var read = NewDbContext();
        Assert.Equal(8, await read.Students.CountAsync());
    }

    /// <summary>
    /// 409, not 400: the request is well-formed and the batch exists — it is the state of the resource
    /// that forbids the operation. A wrong term is the mistake ADR-001 D-5 warns about, and it is
    /// caught before a single row is written.
    /// </summary>
    [Fact]
    public async Task Running_a_batch_under_the_wrong_term_is_409_and_writes_nothing()
    {
        var world = await ArrangeAsync();

        Guid otherTermId;
        await using (var db = NewDbContext())
        {
            var other = TestData.NewTerm(world.SchoolId, "2025-2026-2", isCurrent: false);
            db.Terms.Add(other);
            await db.SaveChangesAsync();
            otherTermId = other.Id;
        }

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient();

        await using var file = SyntheticRoster.Build();
        var batchId = await BatchIdOfAsync(await UploadAsync(client, world.TermId, file));

        var response = await client.PostAsJsonAsync(
            $"/api/v1/sis/import/{batchId}/run", new { termId = otherTermId });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        await using var read = NewDbContext();
        Assert.Equal(0, await read.Students.CountAsync());
    }

    /// <summary>
    /// <b>404, not 409, and the distinction is the point.</b> Both used to be 409, so a caller could
    /// not tell "this batch id never existed" from "this batch has already run" — two problems with
    /// different fixes (correct the id vs. upload the file again) reported identically. It also makes
    /// <c>POST /run</c> and <c>GET</c> agree about what exists.
    /// </summary>
    [Fact]
    public async Task Running_an_unknown_batch_is_404_and_a_batch_that_already_ran_is_409()
    {
        var world = await ArrangeAsync();
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient();

        var unknown = await client.PostAsJsonAsync(
            $"/api/v1/sis/import/{Guid.NewGuid()}/run", new { termId = world.TermId });

        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);

        // Asserted alongside, because a 404 that swallowed the 409 case as well would satisfy the line
        // above while destroying the guard it is meant to be distinguishable from.
        await using var file = SyntheticRoster.Build();
        var batchId = await BatchIdOfAsync(await UploadAsync(client, world.TermId, file));
        await client.PostAsJsonAsync($"/api/v1/sis/import/{batchId}/run", new { termId = world.TermId });

        var rerun = await client.PostAsJsonAsync(
            $"/api/v1/sis/import/{batchId}/run", new { termId = world.TermId });

        Assert.Equal(HttpStatusCode.Conflict, rerun.StatusCode);
    }

    /// <summary>
    /// <b>Every error body in this API carries a <c>traceId</c>, including the ones no action method
    /// ever sees.</b> <c>[ApiController]</c>'s automatic model-validation 400 is emitted by MVC before
    /// the action runs, so it never passed through <c>Program.cs</c>'s <c>CustomizeProblemDetails</c>
    /// (which belongs to <c>IProblemDetailsService</c>) nor through any controller's own composer.
    /// That made the most common error in the API the one error with nothing to quote back — and it
    /// was invisible, because the body still looked like a ProblemDetails.
    ///
    /// <para>
    /// Asserted on two controllers, and on a body the import controller composes itself, because the
    /// fix is a single <c>ProblemDetailsFactory</c> registration: a per-endpoint patch would satisfy
    /// one of these and not the others.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Every_problem_body_carries_a_trace_id_including_automatic_validation_failures()
    {
        await ArrangeAsync();
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient();

        // Model binding fails before the action: MVC composes this one.
        var badBody = await client.PostAsync(
            $"/api/v1/sis/import/{Guid.NewGuid()}/run",
            new StringContent("{\"termId\":\"not-a-guid\"}", System.Text.Encoding.UTF8, "application/json"));

        // A different controller, to prove the fix is the pipeline's and not this endpoint's.
        var badQuery = await client.PostAsync(
            $"/api/v1/attendance/manual?eventId=not-a-guid&studentId={Guid.NewGuid()}", content: null);

        // And one the controller composes itself, which must not have regressed.
        await using var file = SyntheticRoster.Build();
        var noTerm = await UploadAsync(client, Guid.Empty, file);

        foreach (var (name, response) in new[]
                 {
                     ("automatic model-validation 400", badBody),
                     ("automatic query-binding 400", badQuery),
                     ("controller-composed 400", noTerm),
                 })
        {
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

            using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

            Assert.True(
                problem.RootElement.TryGetProperty("traceId", out var traceId),
                $"The {name} body carries no traceId — it is the only handle a caller can quote back.");
            Assert.False(string.IsNullOrWhiteSpace(traceId.GetString()));

            // "status": null forces a client to reconcile the body and the HTTP status as two sources
            // for one fact.
            Assert.True(problem.RootElement.TryGetProperty("status", out var status));
            Assert.Equal(400, status.GetInt32());
        }
    }

    // ------------------------------------------------------------------------- reads

    [Fact]
    public async Task An_unknown_batch_is_404()
    {
        await ArrangeAsync();
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"/api/v1/sis/import/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>
    /// <c>?result=Failed</c> is the query an operator runs after every import, so it is the one that
    /// has to work through HTTP rather than only through the service.
    /// </summary>
    [Fact]
    public async Task The_rows_endpoint_filters_by_result()
    {
        var world = await ArrangeAsync();
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient();

        await using var file = SyntheticRoster.Build();
        var batchId = await BatchIdOfAsync(await UploadAsync(client, world.TermId, file));
        await client.PostAsJsonAsync($"/api/v1/sis/import/{batchId}/run", new { termId = world.TermId });

        Assert.Equal(SyntheticRoster.DataRowCount, await CountRowsAsync(client, batchId, null));
        Assert.Equal(11, await CountRowsAsync(client, batchId, "Inserted"));
        Assert.Equal(1, await CountRowsAsync(client, batchId, "Skipped"));
        Assert.Equal(0, await CountRowsAsync(client, batchId, "Failed"));

        // An unrecognised filter returns nothing rather than everything — silently widening it is how
        // an operator concludes a clean import failed completely.
        Assert.Equal(0, await CountRowsAsync(client, batchId, "failure"));
    }

    private static async Task<int> CountRowsAsync(HttpClient client, Guid batchId, string? result)
    {
        var query = result is null ? "" : $"?result={result}";
        var response = await client.GetAsync($"/api/v1/sis/import/{batchId}/rows{query}");
        response.EnsureSuccessStatusCode();

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetArrayLength();
    }

    /// <summary>
    /// A round trip through the real host: the batch a run produced is readable through <c>GET</c> with
    /// the same counters the run reported.
    /// </summary>
    [Fact]
    public async Task A_finished_batch_is_readable_and_reports_the_same_counters()
    {
        var world = await ArrangeAsync();
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient();

        await using var file = SyntheticRoster.Build();
        var batchId = await BatchIdOfAsync(await UploadAsync(client, world.TermId, file));
        var ran = await client.PostAsJsonAsync(
            $"/api/v1/sis/import/{batchId}/run", new { termId = world.TermId });

        using var fromRun = JsonDocument.Parse(await ran.Content.ReadAsStringAsync());

        var response = await client.GetAsync($"/api/v1/sis/import/{batchId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var fromGet = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        foreach (var property in new[]
                 {
                     "status", "totalRows", "insertedRows", "updatedRows",
                     "failedRows", "skippedRows", "warningRows",
                 })
        {
            Assert.Equal(
                fromRun.RootElement.GetProperty(property).ToString(),
                fromGet.RootElement.GetProperty(property).ToString());
        }
    }

    // ------------------------------------------------------------------- the authorization seam

    /// <summary>
    /// ADR-001 D-6: every endpoint declares the permission Phase 6 will demand, and <b>none of them
    /// enforces it</b>. That matters more here than anywhere else in this API — these four endpoints
    /// read and write the full roster of every student in the institution, names and institutional
    /// e-mail addresses included.
    ///
    /// <para>
    /// The assertion is deliberately two-sided: the declaration must be present on every action, and
    /// the endpoints must still be reachable unauthenticated. The day one of those changes, this test
    /// says so rather than a reviewer inferring protection from a decoration that enforces nothing.
    /// </para>
    /// </summary>
    [Fact]
    public void Every_import_endpoint_declares_the_sis_import_permission_and_enforces_nothing()
    {
        var actions = typeof(EAMS.Api.Controllers.SisImportController)
            .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance
                        | System.Reflection.BindingFlags.DeclaredOnly)
            .Where(m => !m.IsSpecialName)
            .ToList();

        Assert.Equal(4, actions.Count);

        foreach (var action in actions)
        {
            var declared = action
                .GetCustomAttributes(typeof(HasPermissionNotEnforcedAttribute), inherit: true)
                .Cast<HasPermissionNotEnforcedAttribute>()
                .ToList();

            Assert.Single(declared);
            Assert.Equal("sis.import", declared[0].Permission);
        }

        // Structurally incapable of enforcing anything: it implements no ASP.NET Core filter
        // interface, so the MVC pipeline never sees it.
        Assert.Empty(typeof(HasPermissionNotEnforcedAttribute).GetInterfaces());
    }

    [Fact]
    public async Task The_import_endpoints_are_reachable_without_authentication()
    {
        await ArrangeAsync();
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"/api/v1/sis/import/{Guid.NewGuid()}");

        // 404 rather than 401/403: the endpoint ran and found nothing, which is the ADR-001 D-6 state.
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
