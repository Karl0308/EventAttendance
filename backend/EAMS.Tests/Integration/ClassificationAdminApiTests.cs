using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EAMS.Api.Controllers;
using EAMS.Application.Abstractions;
using EAMS.Domain;
using EAMS.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EAMS.Tests.Integration;

/// <summary>
/// The classification vocabulary over real HTTP — create, rename, retire, and the guarded delete.
///
/// <para>
/// <b>Why these are HTTP tests rather than service tests.</b> Everything this phase promises is a
/// statement about the wire: a duplicate name is a <em>409, not a 500</em>; a referenced row is a
/// <em>409, and the row is still there afterwards</em>; an omitted <c>isActive</c> is a <em>400</em>;
/// every failure carries a <c>code</c> extension a client branches on. A service-level suite asserting
/// <see cref="ClassificationWriteOutcome"/> values passes unchanged if the controller maps
/// <c>InUse</c> to a 400, or drops the extension, or lets an unmapped outcome through as a success —
/// which is the drift <c>ClassificationsController.StatusCodeFor</c> enumerates every member to
/// prevent.
/// </para>
///
/// <para>
/// <b>Every destructive assertion counts rows in the table, never the response body.</b> The response
/// is composed by the code under test; the point of the delete guard is what is left in the database
/// afterwards. A <c>DeleteAsync</c> that returned a perfectly correct 409 while having already removed
/// the row would satisfy any body-shaped assertion.
/// </para>
///
/// <para>
/// The host is <see cref="EamsApiFactory"/>, which runs as Production and therefore <b>does not
/// seed</b> — so every row these tests reason about is one they wrote. The seeded vocabulary is
/// <c>DevelopmentSeedClassificationTests</c>' subject, not this file's.
/// </para>
/// </summary>
[Collection(DatabaseCollection.Name)]
public class ClassificationAdminApiTests : IntegrationTest
{
    public ClassificationAdminApiTests(SqlServerFixture sql) : base(sql) { }

    private const string Route = "/api/v1/classifications";

    /// <summary>
    /// The value with the slash, named once. Every test that round-trips it refers to this rather than
    /// re-typing the literal — the drift <c>SeedData.DevelopmentCardUid</c> exists to record.
    /// </summary>
    private const string Slashed = "SUPERVISORY/MANAGERIAL";

    private async Task<Guid> ArrangeSchoolAsync(string code = "USA")
    {
        await using var db = NewDbContext();
        var school = TestData.NewSchool(code);
        db.Schools.Add(school);
        await db.SaveChangesAsync();
        return school.Id;
    }

    /// <summary>
    /// Defaults to the <c>Personnel</c> axis because the merge cases below need two classifications
    /// that can legally be merged, and same-axis is that precondition. Tests about axes name theirs.
    /// </summary>
    private async Task<Guid> ArrangeClassificationAsync(
        Guid schoolId, string name, string axis = ClassificationAxis.Personnel, bool isActive = true)
    {
        await using var db = NewDbContext();
        var row = TestData.NewClassification(schoolId, name, axis, isActive);
        db.Classifications.Add(row);
        await db.SaveChangesAsync();
        return row.Id;
    }

    /// <summary>
    /// The <c>code</c> extension out of an RFC 7807 body, asserting the <c>traceId</c> on the way
    /// through. Both matter and only one is ever the subject: the extension is what a client branches
    /// on, and <c>TracedProblemDetailsFactory</c> exists so an operator has a handle to quote.
    /// </summary>
    private static async Task<string?> ErrorCodeAsync(HttpResponseMessage response)
    {
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.False(
            string.IsNullOrWhiteSpace(body.RootElement.GetProperty("traceId").GetString()),
            "The failure body carried no traceId. It is a correct status code and an unsupportable one.");

        return body.RootElement.GetProperty(ClassificationsController.ErrorCodeProperty).GetString();
    }

    // -------------------------------------------------------------------------------- Q2: create

    /// <summary>
    /// <b>Create, rename, retire — the three operations QA's Q2 answer asks for, end to end.</b>
    /// </summary>
    [Fact]
    public async Task An_administrator_can_create_rename_and_retire_a_classification()
    {
        await ArrangeSchoolAsync();

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory);

        var created = await client.PostAsJsonAsync(Route, new { name = "VISITOR", axis = "Personnel" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        using var createdBody = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var id = createdBody.RootElement.GetProperty("id").GetGuid();

        Assert.True(createdBody.RootElement.GetProperty("isActive").GetBoolean());

        // The Location header resolves — unlike POST /academic/terms, this family has a by-id read.
        // A 201 whose Location 404s is worse than one naming the collection.
        Assert.NotNull(created.Headers.Location);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(created.Headers.Location)).StatusCode);

        var renamed = await client.PutAsJsonAsync($"{Route}/{id}", new { name = "Visiting Staff" });
        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);

        using var renamedBody = JsonDocument.Parse(await renamed.Content.ReadAsStringAsync());
        Assert.Equal("Visiting Staff", renamedBody.RootElement.GetProperty("name").GetString());

        // The id did not move. This is the property the whole table exists for: a rename is one row,
        // and whoever is filed under it stays filed under it.
        Assert.Equal(id, renamedBody.RootElement.GetProperty("id").GetGuid());

        var retired = await client.PatchAsJsonAsync($"{Route}/{id}/active", new { isActive = false });
        Assert.Equal(HttpStatusCode.OK, retired.StatusCode);

        using var retiredBody = JsonDocument.Parse(await retired.Content.ReadAsStringAsync());
        Assert.False(retiredBody.RootElement.GetProperty("isActive").GetBoolean());
        Assert.NotNull(retiredBody.RootElement.GetProperty("retiredAt").GetString());
    }

    /// <summary>
    /// <b><c>SUPERVISORY/MANAGERIAL</c> survives a full round trip through the API.</b>
    ///
    /// <para>
    /// It is the value most likely to break a naive slug, route segment or URL assumption, so it is
    /// pushed through every verb rather than merely stored: created, read back by id, listed, and
    /// renamed. A build that routed by name, or that sanitised punctuation anywhere in that chain,
    /// fails here.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_name_containing_a_slash_survives_a_round_trip_through_every_verb()
    {
        await ArrangeSchoolAsync();

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory);

        var created = await client.PostAsJsonAsync(Route, new { name = Slashed, axis = "Personnel" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        using var createdBody = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var id = createdBody.RootElement.GetProperty("id").GetGuid();

        Assert.Equal(Slashed, createdBody.RootElement.GetProperty("name").GetString());
        Assert.Equal("SUPERVISORYMANAGERIAL", createdBody.RootElement.GetProperty("nameKey").GetString());

        // Read back by id. The route takes a GUID, so the slash never reaches a path segment — which
        // is the assumption this test exists to keep true.
        var read = await client.GetAsync($"{Route}/{id}");
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);

        using var readBody = JsonDocument.Parse(await read.Content.ReadAsStringAsync());
        Assert.Equal(Slashed, readBody.RootElement.GetProperty("name").GetString());

        // And in the list.
        var listed = await client.GetAsync(Route);
        using var listBody = JsonDocument.Parse(await listed.Content.ReadAsStringAsync());

        Assert.Contains(
            listBody.RootElement.GetProperty("items").EnumerateArray(),
            c => c.GetProperty("name").GetString() == Slashed);

        // Renaming it to a differently punctuated spelling of the same thing is allowed — the key does
        // not move, so it is not a duplicate of itself. A rename check written against the display
        // name rather than the key would refuse this with a 409 that named the row itself.
        var renamed = await client.PutAsJsonAsync(
            $"{Route}/{id}", new { name = "Supervisory / Managerial" });

        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);
    }

    /// <summary>
    /// Two spellings of one category cannot both exist: the second is a 409, decided on the normalized
    /// key rather than on the text.
    /// </summary>
    [Theory]
    [InlineData("USA FRIARS", "USA-Friars")]
    [InlineData("USA FRIARS", "usafriars")]
    [InlineData(Slashed, "Supervisory / Managerial")]
    public async Task A_second_spelling_of_one_category_is_refused_as_a_conflict(
        string first, string second)
    {
        await ArrangeSchoolAsync();

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory);

        Assert.Equal(
            HttpStatusCode.Created,
            (await client.PostAsJsonAsync(Route, new { name = first, axis = "Personnel" })).StatusCode);

        var clash = await client.PostAsJsonAsync(Route, new { name = second, axis = "Personnel" });

        Assert.Equal(HttpStatusCode.Conflict, clash.StatusCode);
        Assert.Equal(
            nameof(ClassificationWriteOutcome.NameExists),
            await ErrorCodeAsync(clash));
    }

    /// <summary>
    /// A name that breaks a column rule is a 400, and the body says which rule.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(" NAP")]
    [InlineData("///")]
    public async Task A_name_that_breaks_a_column_rule_is_refused(string name)
    {
        await ArrangeSchoolAsync();

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory);

        var response = await client.PostAsJsonAsync(Route, new { name, axis = "Personnel" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            nameof(ClassificationWriteOutcome.ValidationFailed),
            await ErrorCodeAsync(response));
    }

    // ------------------------------------------------------------------------------ the axis contract

    /// <summary>
    /// <b>A create without a usable axis is a 400.</b>
    ///
    /// <para>
    /// <b>This test exists because the whole axis contract was unguarded at the wire.</b> Replacing
    /// the <c>TryNormalize</c> block in <c>CreateAsync</c> with
    /// <c>var axis = ClassificationAxis.Student;</c> left the entire suite green — every classification
    /// silently landing on the student axis, where it competes for the one slot a person has there, and
    /// nothing anywhere saying so.
    /// </para>
    ///
    /// <para>
    /// There is deliberately no default: <c>Student</c> is the obvious one (20,861 of 21,497 sampled
    /// rows) and would be wrong silently, which is the worst of both.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Staff")]
    [InlineData("STUDENTTEMP")]
    public async Task A_create_without_a_documented_axis_is_refused(string? axis)
    {
        await ArrangeSchoolAsync();

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory);

        var response = await client.PostAsJsonAsync(Route, new { name = "VISITOR", axis });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            nameof(ClassificationWriteOutcome.ValidationFailed),
            await ErrorCodeAsync(response));

        await using var read = NewDbContext();
        Assert.False(
            await read.Classifications.AsNoTracking().AnyAsync(c => c.Name == "VISITOR"),
            "A classification was created despite an unusable axis. A row that reached the table with " +
            "a defaulted axis is one nobody can correct afterwards — the axis is immutable.");
    }

    /// <summary>
    /// The axis is matched case-insensitively and stored canonically, so a client sending
    /// <c>"personnel"</c> gets a row the <c>CHECK</c> constraint and every C# comparison agree about.
    /// </summary>
    [Theory]
    [InlineData("personnel", "Personnel")]
    [InlineData("PERSONNEL", "Personnel")]
    [InlineData("Friars", "Friars")]
    [InlineData("sPeCiAl", "Special")]
    public async Task An_axis_is_accepted_in_any_casing_and_published_canonically(
        string sent, string expected)
    {
        await ArrangeSchoolAsync();

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory);

        var created = await client.PostAsJsonAsync(Route, new { name = "VISITOR", axis = sent });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        using var body = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        Assert.Equal(expected, body.RootElement.GetProperty("axis").GetString());

        // And canonically in the table, not merely in the response — the CHECK constraint compares
        // the stored value, so a row that echoed the caller's casing back while storing theirs would
        // pass a body-shaped assertion and fail on the next write.
        await using var read = NewDbContext();
        Assert.Equal(
            expected,
            (await read.Classifications.AsNoTracking()
                .SingleAsync(c => c.Name == "VISITOR")).Axis);
    }

    /// <summary>
    /// <b>The axis cannot be changed by a rename — the invariant the whole junction design rests on,
    /// and one that had no regression test at all.</b>
    ///
    /// <para>
    /// <c>ClassificationRenameRequest</c> carries no axis, so the value below is an unknown member the
    /// binder discards. That is the point: the test pins the <em>behaviour</em>, so adding an
    /// <c>Axis</c> to that request and honouring it in <c>RenameAsync</c> — two small, plausible edits
    /// — goes red here rather than shipping. Moving a classification between axes would strand every
    /// assignment row's denormalized axis and could collide with something the person already holds on
    /// the destination axis: a rename that half-succeeds, per person.
    /// </para>
    ///
    /// <para>
    /// <b>What the negative control revealed, recorded because it is stronger than the claim above.</b>
    /// Making those two edits does not produce a silently moved axis — it produces a <b>500</b>.
    /// <c>AK_Classifications_Id_Axis</c> makes <c>Axis</c> part of a key, and EF refuses to modify a
    /// key property on a tracked entity at all. So immutability is enforced by the model and not merely
    /// by the absence of a field on a request DTO. That throw is on a path no caller can reach, which
    /// is where a loud refusal belongs — the same placement as the ADR-001 D-2 cache guard. This test
    /// still goes red either way, which is what it is for.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_rename_cannot_move_a_classification_to_another_axis()
    {
        var schoolId = await ArrangeSchoolAsync();
        var id = await ArrangeClassificationAsync(schoolId, "NAP", ClassificationAxis.Personnel);

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory);

        var renamed = await client.PutAsJsonAsync(
            $"{Route}/{id}",
            new { name = "Non-Academic Personnel", axis = ClassificationAxis.Student });

        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);

        using var body = JsonDocument.Parse(await renamed.Content.ReadAsStringAsync());
        Assert.Equal("Non-Academic Personnel", body.RootElement.GetProperty("name").GetString());

        Assert.Equal(
            ClassificationAxis.Personnel,
            body.RootElement.GetProperty("axis").GetString());

        await using var read = NewDbContext();
        var row = await read.Classifications.AsNoTracking().SingleAsync(c => c.Id == id);

        Assert.Equal("Non-Academic Personnel", row.Name);
        Assert.True(
            row.Axis == ClassificationAxis.Personnel,
            $"A rename moved the classification from Personnel to {row.Axis}. The axis is immutable: " +
            "every StudentClassifications row denormalizes it so that one-per-person-per-axis can be " +
            "an index, and moving the parent would leave all of them describing an axis their " +
            "classification no longer has.");
    }

    /// <summary>
    /// <b>An omitted <c>isActive</c> is a 400, not a silent retirement.</b>
    ///
    /// <para>
    /// A missing JSON member binds to <c>false</c>, and on this route <c>false</c> withdraws a category
    /// from every picker. The same refusal, for the same reason, as
    /// <c>PATCH /academic/terms/{id}/current</c> — where it was argued once and is worth re-asserting
    /// here because the failure is silent and looks like a successful no-op.
    /// </para>
    /// </summary>
    [Fact]
    public async Task An_omitted_isActive_is_refused_rather_than_binding_to_false()
    {
        var schoolId = await ArrangeSchoolAsync();
        var id = await ArrangeClassificationAsync(schoolId, "NAP");

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory);

        var response = await client.PatchAsJsonAsync($"{Route}/{id}/active", new { });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            nameof(ClassificationWriteOutcome.ValidationFailed),
            await ErrorCodeAsync(response));

        await using var read = NewDbContext();
        Assert.True(
            (await read.Classifications.AsNoTracking().SingleAsync(c => c.Id == id)).IsActive,
            "An omitted isActive left the classification retired. That is the whole hazard: the " +
            "request looks like a no-op and quietly withdraws a category.");
    }

    // ------------------------------------------------------- the no-data-loss AC: retire vs delete

    /// <summary>
    /// <b>Retiring keeps the row and every assignment; it only stops the category being offered.</b>
    ///
    /// <para>
    /// Asserted on the default of <c>includeRetired</c> as well as on the row, because both halves are
    /// the acceptance criterion and they fail independently: a retire that deleted the row would lose
    /// the description of everyone carrying it, and a retire that left the row in the picker's default
    /// list would not have retired anything.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_retired_classification_leaves_the_picker_and_stays_in_the_table()
    {
        var schoolId = await ArrangeSchoolAsync();
        var id = await ArrangeClassificationAsync(schoolId, "CFI");
        await ArrangeClassificationAsync(schoolId, "STUDENT");

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory);

        Assert.Equal(
            HttpStatusCode.OK,
            (await client.PatchAsJsonAsync($"{Route}/{id}/active", new { isActive = false })).StatusCode);

        // The picker's default: retired entries are gone.
        using var picker = JsonDocument.Parse(
            await (await client.GetAsync(Route)).Content.ReadAsStringAsync());

        Assert.DoesNotContain(
            picker.RootElement.GetProperty("items").EnumerateArray(),
            c => c.GetProperty("name").GetString() == "CFI");

        // The administration screen and every historical report: still there.
        using var all = JsonDocument.Parse(
            await (await client.GetAsync($"{Route}?includeRetired=true")).Content.ReadAsStringAsync());

        Assert.Contains(
            all.RootElement.GetProperty("items").EnumerateArray(),
            c => c.GetProperty("name").GetString() == "CFI");

        await using var read = NewDbContext();
        Assert.True(
            await read.Classifications.AsNoTracking().AnyAsync(c => c.Id == id),
            "Retiring removed the row. A retired classification still describes everyone filed under " +
            "it, so the row has to survive — that is the entire difference between retire and delete.");
    }

    /// <summary>
    /// Retiring is idempotent, and reactivation brings it back.
    ///
    /// <para>
    /// Reactivation is not a convenience: the uniqueness index is unfiltered, so a retired <c>NAP</c>
    /// still occupies the key <c>NAP</c> and an administrator who retired one by mistake cannot simply
    /// re-create it. Without this route that is a dead end, which is why it is asserted rather than
    /// assumed.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Retiring_is_idempotent_and_reversible()
    {
        var schoolId = await ArrangeSchoolAsync();
        var id = await ArrangeClassificationAsync(schoolId, "NAP");

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory);

        for (var i = 0; i < 2; i++)
        {
            Assert.Equal(
                HttpStatusCode.OK,
                (await client.PatchAsJsonAsync(
                    $"{Route}/{id}/active", new { isActive = false })).StatusCode);
        }

        // Re-creating it while retired is refused, and the message has to point at the retired row —
        // otherwise the administrator is told a name is taken by something invisible in every picker.
        var recreate = await client.PostAsJsonAsync(Route, new { name = "NAP", axis = "Personnel" });
        Assert.Equal(HttpStatusCode.Conflict, recreate.StatusCode);
        Assert.Equal(nameof(ClassificationWriteOutcome.NameExists), await ErrorCodeAsync(recreate));

        var restored = await client.PatchAsJsonAsync($"{Route}/{id}/active", new { isActive = true });
        Assert.Equal(HttpStatusCode.OK, restored.StatusCode);

        using var body = JsonDocument.Parse(await restored.Content.ReadAsStringAsync());
        Assert.True(body.RootElement.GetProperty("isActive").GetBoolean());
        Assert.Null(body.RootElement.GetProperty("retiredAt").GetString());
    }

    /// <summary>
    /// <b>An unreferenced classification can be deleted, and that is the only case where deleting
    /// destroys nothing.</b>
    /// </summary>
    [Fact]
    public async Task An_unreferenced_classification_can_be_deleted()
    {
        var schoolId = await ArrangeSchoolAsync();
        var id = await ArrangeClassificationAsync(schoolId, "TYPOO");

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory);

        var response = await client.DeleteAsync($"{Route}/{id}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // The body is the last description of something that no longer exists — an administrator who
        // deleted the wrong row can re-create it from this without going to a backup.
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("TYPOO", body.RootElement.GetProperty("name").GetString());

        await using var read = NewDbContext();
        Assert.False(await read.Classifications.AsNoTracking().AnyAsync(c => c.Id == id));
    }

    /// <summary>
    /// <b>THE no-data-loss acceptance criterion: a referenced classification is a 409 and is still
    /// there afterwards.</b>
    ///
    /// <para>
    /// The referent here is a merge tombstone — another classification whose
    /// <c>MergedIntoClassificationId</c> names this row. That is the only kind of reference that exists
    /// today, because person-to-classification assignment is a separately ruled change; when it lands
    /// it becomes the second clause of the same guard and this test gains a sibling. <b>The guard, the
    /// status code and the advice the refusal gives are already final</b>, which is the point of
    /// shipping the guard with the vocabulary rather than with the assignment.
    /// </para>
    ///
    /// <para>
    /// <b>The row count is asserted, not the response.</b> A <c>DeleteAsync</c> that answered a
    /// perfectly correct 409 after having already removed the row would satisfy any body-shaped
    /// assertion, and that is precisely the failure this criterion is about.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_referenced_classification_is_refused_and_survives_the_refusal()
    {
        var schoolId = await ArrangeSchoolAsync();
        var survivorId = await ArrangeClassificationAsync(schoolId, "ACAD");
        var loserId = await ArrangeClassificationAsync(schoolId, "ACADEMIC STAFF");

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory);

        // Make the survivor referenced, by merging the other row into it.
        Assert.Equal(
            HttpStatusCode.OK,
            (await client.PostAsJsonAsync(
                $"{Route}/{loserId}/merge",
                new { intoClassificationId = survivorId })).StatusCode);

        var refused = await client.DeleteAsync($"{Route}/{survivorId}");

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal(nameof(ClassificationWriteOutcome.InUse), await ErrorCodeAsync(refused));

        await using var read = NewDbContext();

        Assert.True(
            await read.Classifications.AsNoTracking().AnyAsync(c => c.Id == survivorId),
            "The delete was answered 409 and removed the row anyway. A refusal that has already " +
            "destroyed the thing it refused to destroy is worse than no guard at all, because the " +
            "status code says the data is safe.");

        Assert.True(
            await read.Classifications.AsNoTracking()
                .AnyAsync(c => c.Id == loserId && c.MergedIntoClassificationId == survivorId),
            "The tombstone pointing at the refused row is gone. Either the delete cascaded — which is " +
            "the data-loss migration this guard exists to prevent — or the reference was silently " +
            "nulled, which is the same thing with a tidier name.");
    }

    /// <summary>
    /// The refusal names what to do instead. Asserted because an unactionable refusal is how an
    /// administrator ends up asking for a database script.
    /// </summary>
    [Fact]
    public async Task The_delete_refusal_names_the_two_operations_that_would_work()
    {
        var schoolId = await ArrangeSchoolAsync();
        var survivorId = await ArrangeClassificationAsync(schoolId, "ACAD");
        var loserId = await ArrangeClassificationAsync(schoolId, "ACADEMIC STAFF");

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory);

        await client.PostAsJsonAsync(
            $"{Route}/{loserId}/merge", new { intoClassificationId = survivorId });

        var refused = await client.DeleteAsync($"{Route}/{survivorId}");

        using var body = JsonDocument.Parse(await refused.Content.ReadAsStringAsync());
        var detail = body.RootElement.GetProperty("detail").GetString() ?? "";

        Assert.Contains("Retire it", detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("merge it", detail, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A delete, a rename, a retire or a merge against an id this tenant does not have is a 404 rather
    /// than a 500 or a silent success.
    /// </summary>
    [Fact]
    public async Task An_unknown_id_is_a_404_on_every_verb()
    {
        await ArrangeSchoolAsync();
        var missing = Guid.NewGuid();

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory);

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"{Route}/{missing}")).StatusCode);
        Assert.Equal(
            HttpStatusCode.NotFound, (await client.DeleteAsync($"{Route}/{missing}")).StatusCode);
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await client.PutAsJsonAsync($"{Route}/{missing}", new { name = "X" })).StatusCode);
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await client.PatchAsJsonAsync(
                $"{Route}/{missing}/active", new { isActive = false })).StatusCode);
    }
}
