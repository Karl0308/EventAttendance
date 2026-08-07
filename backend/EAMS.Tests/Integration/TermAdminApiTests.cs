using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EAMS.Api.Controllers;
using EAMS.Application.Abstractions;
using EAMS.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EAMS.Tests.Integration;

/// <summary>
/// D-53's term administration surface over real HTTP — <c>POST /academic/terms</c>,
/// <c>PUT /academic/terms/{id}</c> and <c>PATCH /academic/terms/{id}/current</c>.
///
/// <para>
/// <b>Why these are HTTP tests rather than service tests.</b> Everything D-53 actually promises is a
/// statement about the wire: a duplicate code is a <em>409, not a 500</em>; an omitted
/// <c>isCurrent</c> is a <em>400</em>; every failure carries a <c>code</c> extension a client branches
/// on. A service-level suite asserting <see cref="TermWriteOutcome"/> values passes unchanged if the
/// controller maps <c>TermCodeExists</c> to a 400, or drops the extension, or lets an unmapped outcome
/// through as a success — which is the drift <c>AcademicController.StatusCodeFor</c> enumerates every
/// member to prevent, and the drift <c>StudentsApiTests</c> pins for §6.2 for the same reason.
/// </para>
///
/// <para>
/// <b>Every current-flag assertion counts rows, never the response body.</b> The response is composed
/// by the code under test; the point of <c>UX_Terms_SchoolId_Current</c> is what is left in the table
/// afterwards. A <c>SetCurrentAsync</c> that returned a perfectly correct DTO while leaving two rows
/// flagged — or while clearing another school's — would satisfy any body-shaped assertion and would
/// make every term-defaulting query in the system pick a semester at random.
/// </para>
///
/// <para>
/// The concurrent-create race against <c>UX_Terms_SchoolId_Code</c> lives in
/// <see cref="TermAdminRaceTests"/>: the sequential duplicate below is resolved by the service's
/// pre-check and cannot reach the unique-violation handler behind it.
/// </para>
/// </summary>
[Collection(DatabaseCollection.Name)]
public class TermAdminApiTests : IntegrationTest
{
    public TermAdminApiTests(SqlServerFixture sql) : base(sql) { }

    private const string Route = "/api/v1/academic/terms";

    private const string SchoolYear = "2025-2026";
    private const string FirstSemester = "1st Semester";

    private async Task<Guid> ArrangeSchoolAsync(string code = "USA")
    {
        await using var db = NewDbContext();
        var school = TestData.NewSchool(code);
        db.Schools.Add(school);
        await db.SaveChangesAsync();
        return school.Id;
    }

    private async Task<Guid> ArrangeTermAsync(Guid schoolId, string code, bool isCurrent)
    {
        await using var db = NewDbContext();
        var term = TestData.NewTerm(schoolId, code, isCurrent);
        db.Terms.Add(term);
        await db.SaveChangesAsync();
        return term.Id;
    }

    private static object Body(
        string code = "2025-2026-2",
        string schoolYear = SchoolYear,
        string semester = "2nd Semester",
        DateOnly? startsOn = null,
        DateOnly? endsOn = null) =>
        new { code, schoolYear, semester, startsOn, endsOn };

    /// <summary>
    /// The <c>code</c> extension out of an RFC 7807 body, asserting the <c>traceId</c> on the way
    /// through. Both matter and only one is ever the subject: the extension is what a client branches
    /// on, and <c>TracedProblemDetailsFactory</c> exists so an operator has a handle to quote — a
    /// failure body that lost it is still a correct status code and an unsupportable one.
    /// </summary>
    private static async Task<string?> ErrorCodeAsync(HttpResponseMessage response)
    {
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.False(string.IsNullOrWhiteSpace(
            body.RootElement.GetProperty("traceId").GetString()));

        return body.RootElement.GetProperty(AcademicController.ErrorCodeProperty).GetString();
    }

    /// <summary>The ids of every term flagged current in one school, read straight from the table.</summary>
    private async Task<List<Guid>> CurrentTermIdsAsync(Guid schoolId)
    {
        // Unpinned context: this has to be able to see a school the host is not filtered to, which is
        // the whole of the cross-tenant assertion below.
        await using var read = NewDbContext(new TestSchoolContext());
        return await read.Terms.AsNoTracking()
            .Where(t => t.SchoolId == schoolId && t.IsCurrent)
            .Select(t => t.Id)
            .ToListAsync();
    }

    // ------------------------------------------------------------------------------- create: 201

    /// <summary>
    /// <b>A created term is never current, and the incumbent keeps the flag.</b>
    ///
    /// <para>
    /// This is the contract that makes <c>PATCH /current</c> the only writer of
    /// <c>UX_Terms_SchoolId_Current</c>. If a create could set the flag, the very first term an
    /// operator added after setting up next semester would silently move the institution into it — and
    /// against a school that already had a current term the insert would be a duplicate-key 500 rather
    /// than anything the operator could act on.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Creating_a_term_is_201_and_the_new_term_is_not_current()
    {
        var schoolId = await ArrangeSchoolAsync();
        var incumbent = await ArrangeTermAsync(schoolId, "2025-2026-1", isCurrent: true);

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(Route, Body(code: "2025-2026-2"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        using var created = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var id = created.RootElement.GetProperty("id").GetGuid();

        Assert.Equal("2025-2026-2", created.RootElement.GetProperty("code").GetString());
        Assert.False(created.RootElement.GetProperty("isCurrent").GetBoolean());

        // And in the table, not only in the body it was handed.
        Assert.Equal([incumbent], await CurrentTermIdsAsync(schoolId));

        await using var read = NewDbContext();
        Assert.False((await read.Terms.AsNoTracking().SingleAsync(t => t.Id == id)).IsCurrent);
    }

    // ------------------------------------------------------------------- create: the D-53 promise

    /// <summary>
    /// <b>§8's first required test: a duplicate <c>Code</c> is a 409, not a 500.</b>
    ///
    /// <para>
    /// "Cannot be duplicate" is the operator's requirement and <c>UX_Terms_SchoolId_Code</c> is what
    /// enforces it, so the only question this route can get wrong is how the refusal reaches the SPA.
    /// An unhandled unique violation is a 500 with no <c>code</c> extension: the form cannot say "that
    /// term code is taken", it can only say something went wrong.
    /// </para>
    ///
    /// <para>
    /// This is the <em>sequential</em> half and it is resolved by the service's pre-check — it never
    /// reaches the unique-violation handler. <see cref="TermAdminRaceTests"/> owns the half that does.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_duplicate_term_code_is_409_TermCodeExists()
    {
        var schoolId = await ArrangeSchoolAsync();
        await ArrangeTermAsync(schoolId, "2025-2026-1", isCurrent: true);

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(Route, Body(code: "2025-2026-1"));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(nameof(TermWriteOutcome.TermCodeExists), await ErrorCodeAsync(response));

        await using var read = NewDbContext();
        Assert.Single(await read.Terms.AsNoTracking().Where(t => t.Code == "2025-2026-1").ToListAsync());
    }

    // --------------------------------------------------------------------------- create: refusals

    /// <summary>
    /// <b>A whitespace-padded code is refused, not trimmed.</b>
    ///
    /// <para>
    /// D-53 keeps a term code exactly as the operator authored it, so nothing on this path normalizes.
    /// A silent trim would be worse than either alternative here: SQL Server's collation makes
    /// <c>'2025-2026-1 '</c> and <c>'2025-2026-1'</c> one value to the unique index but two different
    /// strings to every equality comparison in C#, so a trimmed-on-write code would round-trip
    /// differently from the one that was posted and a picker would show two entries that read
    /// identically.
    /// </para>
    ///
    /// <para>
    /// Asserted with the leading form as well as the trailing one: trailing whitespace is what the
    /// database collation would forgive, and leading whitespace is what it would not, so a rule that
    /// only ever saw one of them could be half-implemented and still look green.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(" 2025-2026-2")]
    [InlineData("2025-2026-2 ")]
    public async Task A_whitespace_padded_code_is_400_and_writes_nothing(string code)
    {
        await ArrangeSchoolAsync();

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(Route, Body(code: code));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(nameof(TermWriteOutcome.ValidationFailed), await ErrorCodeAsync(response));

        // Not "no row with that exact string" — no row at all. A trimmed write would satisfy the
        // narrower assertion while doing precisely the thing D-53 forbids.
        await using var read = NewDbContext();
        Assert.Empty(await read.Terms.AsNoTracking().ToListAsync());
    }

    /// <summary>
    /// A term that ends before it starts contains no days at all. Both dates are optional — the SIS
    /// export has no term-date columns, so a real term usually carries neither — which is why this is
    /// the one thing about them worth refusing.
    /// </summary>
    [Fact]
    public async Task A_term_ending_before_it_starts_is_400()
    {
        await ArrangeSchoolAsync();

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(Route, Body(
            startsOn: new DateOnly(2025, 12, 20), endsOn: new DateOnly(2025, 8, 11)));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(nameof(TermWriteOutcome.ValidationFailed), await ErrorCodeAsync(response));

        await using var read = NewDbContext();
        Assert.Empty(await read.Terms.AsNoTracking().ToListAsync());
    }

    /// <summary>
    /// <b>A create against a database with no resolvable school is a 409 carrying
    /// <c>NoSchoolResolved</c> — and that token is a cross-layer contract, not an implementation
    /// detail.</b>
    ///
    /// <para>
    /// <c>web-admin/src/termDraft.ts</c> hardcodes the string <c>"NoSchoolResolved"</c> and
    /// <c>NewTermDialog</c> renders a dedicated alert for it, because it is the one failure on this form
    /// an operator cannot act on — the generic "check your input" text is actively misleading when the
    /// answer is "this needs somebody with database access". Every other test on this route pins
    /// <c>TermCodeExists</c>, <c>ValidationFailed</c> or <c>NotFound</c>, so renaming this enum member
    /// left the whole suite green while the SPA silently fell back to the generic alert on the single
    /// error whose bespoke text matters most.
    /// </para>
    ///
    /// <para>
    /// <b>No arrange step, and that is the arrangement.</b> <see cref="IntegrationTest"/> empties every
    /// table before each test, so zero <c>Schools</c> rows is where this one starts;
    /// <c>SchoolResolution</c> answers null for zero candidates exactly as it does for several, which is
    /// the pre-auth build's whole tenancy story (ADR-001 D-6).
    /// </para>
    /// </summary>
    [Fact]
    public async Task Posting_a_term_with_no_school_resolvable_is_409_NoSchoolResolved()
    {
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(Route, Body());

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(nameof(TermWriteOutcome.NoSchoolResolved), await ErrorCodeAsync(response));

        // Refused before anything was staged: a term filed under no school at all would be a row the
        // §11 query filter can never return and no tenant can ever see.
        await using var read = NewDbContext();
        Assert.Empty(await read.Terms.AsNoTracking().ToListAsync());
    }

    // ----------------------------------------------------------------------------- update: 200

    /// <summary>
    /// A PUT replaces the term's authored fields — including <c>code</c>, which is the single most
    /// likely edit anyone makes here — and <b>does not move the current-term flag</b>.
    ///
    /// <para>
    /// The flag assertion is the load-bearing one. <c>IsCurrent</c> is absent from
    /// <c>TermWriteRequest</c>, so a full-replacement update that reset every column it did not receive
    /// would silently retire the institution's semester on a typo fix, and the response body would
    /// still look entirely correct.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Updating_a_term_is_200_renames_the_code_and_leaves_the_current_flag_alone()
    {
        var schoolId = await ArrangeSchoolAsync();
        var termId = await ArrangeTermAsync(schoolId, "2025-2026-1", isCurrent: true);

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient();

        var response = await client.PutAsJsonAsync($"{Route}/{termId}", Body(
            code: "2025-2026-1st", semester: FirstSemester, startsOn: new DateOnly(2025, 8, 11),
            endsOn: new DateOnly(2025, 12, 20)));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("2025-2026-1st", body.RootElement.GetProperty("code").GetString());
        Assert.Equal("2025-08-11", body.RootElement.GetProperty("startsOn").GetString());
        Assert.True(body.RootElement.GetProperty("isCurrent").GetBoolean());

        await using var read = NewDbContext();
        var term = await read.Terms.AsNoTracking().SingleAsync(t => t.Id == termId);
        Assert.Equal("2025-2026-1st", term.Code);
        Assert.True(term.IsCurrent, "A display edit moved the school's current-term flag.");
    }

    /// <summary>
    /// Renaming a term onto a code another term in the school already holds is the same conflict the
    /// create path answers, reached down a different code path — <c>UpdateAsync</c> has its own
    /// pre-check, with an <c>excluding</c> clause the create path has no need for.
    ///
    /// <para>
    /// That clause is why the round-trip case above matters as much as this one: an <c>excluding</c>
    /// that was forgotten would report a term as its own duplicate and make every ordinary PUT a 409,
    /// while an <c>excluding</c> applied too widely would let this rename through and break the index.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Renaming_a_term_onto_an_existing_code_is_409_TermCodeExists()
    {
        var schoolId = await ArrangeSchoolAsync();
        await ArrangeTermAsync(schoolId, "2025-2026-1", isCurrent: true);
        var second = await ArrangeTermAsync(schoolId, "2025-2026-2", isCurrent: false);

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient();

        var response = await client.PutAsJsonAsync($"{Route}/{second}", Body(code: "2025-2026-1"));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(nameof(TermWriteOutcome.TermCodeExists), await ErrorCodeAsync(response));

        await using var read = NewDbContext();
        Assert.Equal("2025-2026-2", (await read.Terms.AsNoTracking().SingleAsync(t => t.Id == second)).Code);
    }

    // --------------------------------------------------------------------------- patch: the flag

    /// <summary>
    /// <b>§8's second required test: <c>PATCH /current</c> leaves exactly one current term per
    /// school.</b>
    ///
    /// <para>
    /// Asserted by counting rows rather than by reading the response, because the two failures that
    /// matter are both invisible from the body. Setting the flag without clearing the incumbent leaves
    /// two rows flagged — which <c>UX_Terms_SchoolId_Current</c> would normally refuse, so it surfaces
    /// as a duplicate-key 500 rather than as bad data, unless the two writes are ordered such that it
    /// briefly succeeds. Clearing without setting leaves zero, and every term-defaulting read in the
    /// system then answers empty.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Making_a_term_current_leaves_exactly_one_current_term_in_the_school()
    {
        var schoolId = await ArrangeSchoolAsync();
        var incumbent = await ArrangeTermAsync(schoolId, "2025-2026-1", isCurrent: true);
        var challenger = await ArrangeTermAsync(schoolId, "2025-2026-2", isCurrent: false);

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient();

        var response = await client.PatchAsJsonAsync(
            $"{Route}/{challenger}/current", new { isCurrent = true });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var current = await CurrentTermIdsAsync(schoolId);

        Assert.True(
            current is [var only] && only == challenger,
            $"After making one term current the school has {current.Count} current term(s); expected " +
            "exactly the one that was asked for. Zero empties the roster-import picker; two make every " +
            "term-defaulting query pick a semester at random.");

        await using var read = NewDbContext();
        Assert.False((await read.Terms.AsNoTracking().SingleAsync(t => t.Id == incumbent)).IsCurrent);
    }

    /// <summary>
    /// Idempotent in both directions, and <c>false</c> retires — leaving <b>zero</b> current terms,
    /// which is an ordinary state rather than an error.
    ///
    /// <para>
    /// Retiring is the only retirement D-53 offers, because a term with a batch imported against it
    /// cannot be deleted without taking that batch's enrolments with it. The 404 from
    /// <c>GET /terms/current</c> afterwards is asserted because that is how the rest of the system
    /// observes the state: a build that answered 200-with-null there would let a picker default to
    /// nothing while believing it had defaulted to something.
    /// </para>
    ///
    /// <para>
    /// <b>The repeats assert <c>UpdatedAt</c> as well as the flag, and without that they assert almost
    /// nothing.</b> Re-running the move is <em>naturally</em> idempotent — the statement sets
    /// <c>IsCurrent = (Id = @id)</c> over the same two rows and lands on the same values — so a repeat
    /// checked only on the flag stays green with the short-circuit deleted. What the short-circuit
    /// actually buys is that a no-op writes nothing, which is what keeps an audit column a record of
    /// edits rather than of clicks; <c>UpdatedAt</c> is the only place that is observable.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Setting_current_is_idempotent_both_ways_and_retiring_leaves_none()
    {
        var schoolId = await ArrangeSchoolAsync();
        await ArrangeTermAsync(schoolId, "2025-2026-1", isCurrent: true);
        var challenger = await ArrangeTermAsync(schoolId, "2025-2026-2", isCurrent: false);

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient();

        var route = $"{Route}/{challenger}/current";

        await RepeatedlyAsync(client, route, isCurrent: true, () => AssertOneCurrentAsync(schoolId, challenger));
        await RepeatedlyAsync(client, route, isCurrent: false, async () =>
            Assert.Empty(await CurrentTermIdsAsync(schoolId)));

        // How the rest of the system observes zero current terms: a 404, not a 200 carrying null.
        Assert.Equal(
            HttpStatusCode.NotFound, (await client.GetAsync($"{Route}/current")).StatusCode);
    }

    /// <summary>
    /// Sends the same <c>PATCH /current</c> twice and asserts that the second changed nothing —
    /// neither the answer, nor the flag, nor <c>UpdatedAt</c>.
    /// </summary>
    private async Task RepeatedlyAsync(
        HttpClient client, string route, bool isCurrent, Func<Task> assertPostcondition)
    {
        var seen = new List<DateTime>();

        foreach (var _ in Enumerable.Range(0, 2))
        {
            var response = await client.PatchAsJsonAsync(route, new { isCurrent });
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(isCurrent, body.RootElement.GetProperty("isCurrent").GetBoolean());

            await assertPostcondition();

            await using var read = NewDbContext();
            seen.Add((await read.Terms.AsNoTracking()
                .SingleAsync(t => t.Id == body.RootElement.GetProperty("id").GetGuid())).UpdatedAt);
        }

        Assert.True(
            seen[0] == seen[1],
            $"Repeating PATCH /current with isCurrent={isCurrent} moved UpdatedAt from {seen[0]:O} to " +
            $"{seen[1]:O}. A request asking for the state the row is already in must not write: a " +
            "double-clicked button would otherwise make the audit column a record of clicks.");
    }

    private async Task AssertOneCurrentAsync(Guid schoolId, Guid expected)
    {
        var current = await CurrentTermIdsAsync(schoolId);
        Assert.True(
            current is [var only] && only == expected,
            $"Expected exactly one current term ({expected}); the school has {current.Count}.");
    }

    /// <summary>
    /// <b>An omitted <c>isCurrent</c> is a 400, not a silent retire.</b>
    ///
    /// <para>
    /// This is the reason the DTO's member is <c>bool?</c>. A non-nullable <c>bool</c> deserializes a
    /// missing member to <c>false</c>, and on this route <c>false</c> is a destructive instruction: a
    /// client that forgot the field — or renamed it, or sent it as a string — would clear the
    /// institution's current term and receive a 200 saying so. Nothing downstream can tell that apart
    /// from an operator who meant it.
    /// </para>
    ///
    /// <para>
    /// The row is read back afterwards rather than only the status code, because the status is not the
    /// property under test: a build that returned 400 <em>after</em> performing the write would pass a
    /// status-only assertion and still be the exact defect.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_patch_body_without_isCurrent_is_400_and_retires_nothing()
    {
        var schoolId = await ArrangeSchoolAsync();
        var termId = await ArrangeTermAsync(schoolId, "2025-2026-1", isCurrent: true);

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient();

        var response = await client.PatchAsJsonAsync($"{Route}/{termId}/current", new { });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(nameof(TermWriteOutcome.ValidationFailed), await ErrorCodeAsync(response));

        Assert.Equal([termId], await CurrentTermIdsAsync(schoolId));
    }

    /// <summary>
    /// <b>Moving one school's flag does not touch another's.</b>
    ///
    /// <para>
    /// <c>UX_Terms_SchoolId_Current</c> is <em>per school</em>, so the statement that moves the flag has
    /// to carry a <c>SchoolId</c> predicate of its own — the global query filter is inert whenever no
    /// tenant is pinned, which is most of this suite and any unseeded start. Without that predicate an
    /// update written as "clear whichever row is current, set this one" clears every school in the
    /// database, and the response to the caller is correct in every visible respect.
    /// </para>
    ///
    /// <para>
    /// The host pins the lowest school <c>Code</c> (ADR-001 D-6), so <c>AAA</c> is the tenant every
    /// request below runs as and <c>ZZZ</c> is the bystander.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Moving_the_flag_leaves_another_schools_current_term_alone()
    {
        var pinned = await ArrangeSchoolAsync("AAA");
        var bystander = await ArrangeSchoolAsync("ZZZ");

        await ArrangeTermAsync(pinned, "2025-2026-1", isCurrent: true);
        var challenger = await ArrangeTermAsync(pinned, "2025-2026-2", isCurrent: false);
        var elsewhere = await ArrangeTermAsync(bystander, "2025-2026-1", isCurrent: true);

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient();

        var response = await client.PatchAsJsonAsync(
            $"{Route}/{challenger}/current", new { isCurrent = true });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        Assert.Equal([challenger], await CurrentTermIdsAsync(pinned));
        Assert.Equal([elsewhere], await CurrentTermIdsAsync(bystander));
    }

    /// <summary>
    /// <b>The same fact with no tenant pinned — and it is a different guard, which is why it is a
    /// second test.</b>
    ///
    /// <para>
    /// Over HTTP the host pins a tenant at startup, so <c>_db.Terms</c> is already narrowed by the
    /// global <c>SchoolId</c> query filter and the flag move cannot see another school's rows however
    /// it is written. <b>That filter is inert whenever nothing is pinned</b> — design time, most of
    /// this suite, any start against a database with zero or several schools — and the explicit
    /// <c>SchoolId</c> predicate on the update statement is the only thing standing between that state
    /// and an update that clears the current term of every school in the database. Deleting the
    /// predicate leaves the HTTP test above entirely green.
    /// </para>
    ///
    /// <para>
    /// Called through <see cref="ITermAdminService"/> rather than over the wire for exactly that
    /// reason: <see cref="IntegrationTest.School"/> is unpinned by default, which is the condition
    /// under test and one no request can reproduce.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Moving_the_flag_with_no_tenant_pinned_still_only_touches_one_school()
    {
        var first = await ArrangeSchoolAsync("AAA");
        var second = await ArrangeSchoolAsync("ZZZ");

        await ArrangeTermAsync(first, "2025-2026-1", isCurrent: true);
        var challenger = await ArrangeTermAsync(first, "2025-2026-2", isCurrent: false);
        var elsewhere = await ArrangeTermAsync(second, "2025-2026-1", isCurrent: true);

        await using (var db = NewDbContext())
        {
            var response = await TermsOn(db).SetCurrentAsync(challenger, isCurrent: true);
            Assert.Equal(TermWriteOutcome.Saved, response.Outcome);
        }

        Assert.Equal([challenger], await CurrentTermIdsAsync(first));
        Assert.Equal([elsewhere], await CurrentTermIdsAsync(second));
    }

    // ------------------------------------------------------------------------------------- 404s

    /// <summary>
    /// Both write routes that name a term in the URL answer 404 when it does not exist, with the
    /// <c>code</c> extension rather than only a status — a client distinguishing "gone" from "refused"
    /// reads the extension, and <c>[ApiController]</c>'s synthesized 404 carries none.
    /// </summary>
    [Fact]
    public async Task Writing_to_an_unknown_term_is_404_on_put_and_patch()
    {
        await ArrangeSchoolAsync();
        var unknown = Guid.NewGuid();

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient();

        var put = await client.PutAsJsonAsync($"{Route}/{unknown}", Body());
        Assert.Equal(HttpStatusCode.NotFound, put.StatusCode);
        Assert.Equal(nameof(TermWriteOutcome.NotFound), await ErrorCodeAsync(put));

        var patch = await client.PatchAsJsonAsync(
            $"{Route}/{unknown}/current", new { isCurrent = true });
        Assert.Equal(HttpStatusCode.NotFound, patch.StatusCode);
        Assert.Equal(nameof(TermWriteOutcome.NotFound), await ErrorCodeAsync(patch));
    }
}
