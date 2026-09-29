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
/// The event-classification vocabulary over real HTTP — create, update, deactivate, and the guarded
/// delete. HTTP tests rather than service tests for the reason <c>ClassificationAdminApiTests</c>
/// records: everything the module promises is a statement about the wire (a duplicate is a 409 not a
/// 500, a seeded row is a 409 not a delete, every failure carries a <c>code</c> extension), and every
/// destructive assertion counts rows in the table rather than trusting the response body.
///
/// <para>
/// The host is <see cref="EamsApiFactory"/>, which runs as Production and therefore does not seed — so
/// every row these tests reason about is one they wrote, including the "seeded-name" row in the
/// seed-protection test.
/// </para>
/// </summary>
[Collection(DatabaseCollection.Name)]
public class EventClassificationAdminApiTests : IntegrationTest
{
    public EventClassificationAdminApiTests(SqlServerFixture sql) : base(sql) { }

    private const string Route = "/api/v1/event-classifications";

    private async Task ArrangeSchoolAsync(string code = "USA")
    {
        await using var db = NewDbContext();
        db.Schools.Add(TestData.NewSchool(code));
        await db.SaveChangesAsync();
    }

    private async Task<Guid> ArrangeClassificationAsync(string name, bool isActive = true)
    {
        await using var db = NewDbContext();
        var schoolId = await db.Schools.Select(s => s.Id).FirstAsync();
        var row = TestData.NewEventClassification(schoolId, name, isActive: isActive);
        db.EventClassifications.Add(row);
        await db.SaveChangesAsync();
        return row.Id;
    }

    private static async Task<string?> ErrorCodeAsync(HttpResponseMessage response)
    {
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.False(
            string.IsNullOrWhiteSpace(body.RootElement.GetProperty("traceId").GetString()),
            "The failure body carried no traceId. It is a correct status code and an unsupportable one.");

        return body.RootElement.GetProperty(EventClassificationsController.ErrorCodeProperty).GetString();
    }

    [Fact]
    public async Task An_administrator_can_create_update_and_deactivate_a_classification()
    {
        await ArrangeSchoolAsync();

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory);

        var created = await client.PostAsJsonAsync(
            Route, new { name = "Sports Fest", description = "Annual sports meet." });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        using var createdBody = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var id = createdBody.RootElement.GetProperty("id").GetGuid();
        Assert.True(createdBody.RootElement.GetProperty("isActive").GetBoolean());
        Assert.Equal("Annual sports meet.", createdBody.RootElement.GetProperty("description").GetString());

        // The Location header resolves — this family has a by-id read.
        Assert.NotNull(created.Headers.Location);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(created.Headers.Location)).StatusCode);

        var updated = await client.PutAsJsonAsync(
            $"{Route}/{id}", new { name = "Sports Festival", description = "Renamed." });
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);

        using var updatedBody = JsonDocument.Parse(await updated.Content.ReadAsStringAsync());
        Assert.Equal("Sports Festival", updatedBody.RootElement.GetProperty("name").GetString());
        Assert.Equal("Renamed.", updatedBody.RootElement.GetProperty("description").GetString());
        Assert.Equal(id, updatedBody.RootElement.GetProperty("id").GetGuid());

        var deactivated = await client.PatchAsJsonAsync($"{Route}/{id}/active", new { isActive = false });
        Assert.Equal(HttpStatusCode.OK, deactivated.StatusCode);

        using var deactivatedBody = JsonDocument.Parse(await deactivated.Content.ReadAsStringAsync());
        Assert.False(deactivatedBody.RootElement.GetProperty("isActive").GetBoolean());
        Assert.NotNull(deactivatedBody.RootElement.GetProperty("retiredAt").GetString());
    }

    [Fact]
    public async Task A_PUT_that_omits_the_description_clears_it()
    {
        await ArrangeSchoolAsync();
        var id = await ArrangeClassificationAsync("Sports Fest");

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory);

        // Give it a description first.
        await client.PutAsJsonAsync($"{Route}/{id}", new { name = "Sports Fest", description = "X" });

        // A full-replacement PUT with no description clears the stored one.
        var cleared = await client.PutAsJsonAsync($"{Route}/{id}", new { name = "Sports Fest" });
        Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);

        await using var read = NewDbContext();
        Assert.Null((await read.EventClassifications.AsNoTracking().SingleAsync(c => c.Id == id)).Description);
    }

    [Theory]
    [InlineData("Departmental Events", "departmental-events")]
    [InlineData("Departmental Events", "DEPARTMENTALEVENTS")]
    public async Task A_second_spelling_of_one_classification_is_refused_as_a_conflict(
        string first, string second)
    {
        await ArrangeSchoolAsync();

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory);

        Assert.Equal(
            HttpStatusCode.Created,
            (await client.PostAsJsonAsync(Route, new { name = first })).StatusCode);

        var clash = await client.PostAsJsonAsync(Route, new { name = second });

        Assert.Equal(HttpStatusCode.Conflict, clash.StatusCode);
        Assert.Equal(nameof(EventClassificationWriteOutcome.NameExists), await ErrorCodeAsync(clash));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(" Institutional")]
    [InlineData("///")]
    public async Task A_name_that_breaks_a_column_rule_is_refused(string name)
    {
        await ArrangeSchoolAsync();

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory);

        var response = await client.PostAsJsonAsync(Route, new { name });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(nameof(EventClassificationWriteOutcome.ValidationFailed), await ErrorCodeAsync(response));
    }

    [Fact]
    public async Task An_omitted_isActive_is_refused_rather_than_binding_to_false()
    {
        await ArrangeSchoolAsync();
        var id = await ArrangeClassificationAsync("Sports Fest");

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory);

        var response = await client.PatchAsJsonAsync($"{Route}/{id}/active", new { });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(nameof(EventClassificationWriteOutcome.ValidationFailed), await ErrorCodeAsync(response));

        await using var read = NewDbContext();
        Assert.True(
            (await read.EventClassifications.AsNoTracking().SingleAsync(c => c.Id == id)).IsActive,
            "An omitted isActive left the classification deactivated — the whole hazard the 400 prevents.");
    }

    [Fact]
    public async Task A_deactivated_classification_leaves_the_picker_and_stays_in_the_table()
    {
        await ArrangeSchoolAsync();
        var id = await ArrangeClassificationAsync("Ad-hoc Event");
        await ArrangeClassificationAsync("Institutional Events");

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory);

        Assert.Equal(
            HttpStatusCode.OK,
            (await client.PatchAsJsonAsync($"{Route}/{id}/active", new { isActive = false })).StatusCode);

        using var picker = JsonDocument.Parse(
            await (await client.GetAsync(Route)).Content.ReadAsStringAsync());
        Assert.DoesNotContain(
            picker.RootElement.GetProperty("items").EnumerateArray(),
            c => c.GetProperty("name").GetString() == "Ad-hoc Event");

        using var all = JsonDocument.Parse(
            await (await client.GetAsync($"{Route}?includeInactive=true")).Content.ReadAsStringAsync());
        Assert.Contains(
            all.RootElement.GetProperty("items").EnumerateArray(),
            c => c.GetProperty("name").GetString() == "Ad-hoc Event");

        await using var read = NewDbContext();
        Assert.True(await read.EventClassifications.AsNoTracking().AnyAsync(c => c.Id == id));
    }

    [Fact]
    public async Task Deactivating_is_idempotent_and_reversible_and_a_deactivated_name_is_still_taken()
    {
        await ArrangeSchoolAsync();
        var id = await ArrangeClassificationAsync("Ad-hoc Event");

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory);

        for (var i = 0; i < 2; i++)
        {
            Assert.Equal(
                HttpStatusCode.OK,
                (await client.PatchAsJsonAsync($"{Route}/{id}/active", new { isActive = false })).StatusCode);
        }

        // Re-creating it while deactivated is refused, and the message points at the deactivated row.
        var recreate = await client.PostAsJsonAsync(Route, new { name = "Ad-hoc Event" });
        Assert.Equal(HttpStatusCode.Conflict, recreate.StatusCode);
        Assert.Equal(nameof(EventClassificationWriteOutcome.NameExists), await ErrorCodeAsync(recreate));

        var restored = await client.PatchAsJsonAsync($"{Route}/{id}/active", new { isActive = true });
        Assert.Equal(HttpStatusCode.OK, restored.StatusCode);

        using var body = JsonDocument.Parse(await restored.Content.ReadAsStringAsync());
        Assert.True(body.RootElement.GetProperty("isActive").GetBoolean());
        Assert.Null(body.RootElement.GetProperty("retiredAt").GetString());
    }

    [Fact]
    public async Task An_unreferenced_classification_can_be_deleted()
    {
        await ArrangeSchoolAsync();
        var id = await ArrangeClassificationAsync("Typo Event");

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory);

        var response = await client.DeleteAsync($"{Route}/{id}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("Typo Event", body.RootElement.GetProperty("name").GetString());

        await using var read = NewDbContext();
        Assert.False(await read.EventClassifications.AsNoTracking().AnyAsync(c => c.Id == id));
    }

    [Fact]
    public async Task A_seeded_classification_cannot_be_deleted_and_survives_the_refusal()
    {
        await ArrangeSchoolAsync();
        // A row whose name is one of the seeded three — the delete guard reads the name key, not who
        // wrote the row, so a test-arranged seeded name is protected exactly as a startup-seeded one is.
        var id = await ArrangeClassificationAsync(EventClassificationSeedValues.Names[0]);

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory);

        var refused = await client.DeleteAsync($"{Route}/{id}");
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal(nameof(EventClassificationWriteOutcome.SeedProtected), await ErrorCodeAsync(refused));

        using var body = JsonDocument.Parse(await refused.Content.ReadAsStringAsync());
        Assert.Contains("Deactivate it", body.RootElement.GetProperty("detail").GetString() ?? "",
            StringComparison.OrdinalIgnoreCase);

        await using var read = NewDbContext();
        Assert.True(
            await read.EventClassifications.AsNoTracking().AnyAsync(c => c.Id == id),
            "The delete was answered 409 and removed the seeded row anyway.");
    }

    [Fact]
    public async Task An_unknown_id_is_a_404_on_every_verb()
    {
        await ArrangeSchoolAsync();
        var missing = Guid.NewGuid();

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory);

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"{Route}/{missing}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"{Route}/{missing}")).StatusCode);
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await client.PutAsJsonAsync($"{Route}/{missing}", new { name = "X" })).StatusCode);
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await client.PatchAsJsonAsync($"{Route}/{missing}/active", new { isActive = false })).StatusCode);
    }
}
