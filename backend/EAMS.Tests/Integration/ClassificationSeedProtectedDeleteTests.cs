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
/// <b>The seed-protection guard in <c>ClassificationService.DeleteAsync</c>.</b> An unreferenced
/// classification whose <c>NameKey</c> is one of the eight <see cref="ClassificationSeedValues"/> keys
/// can no longer be deleted — the startup seed would put it straight back, silently, on the next
/// restart. It now answers <b>409 SeedProtected</b> and points at the retire route, and the row survives.
///
/// <para>
/// Runs against <see cref="EamsApiFactory"/>, which is Production and does not seed — every row here is
/// one the test wrote, matching a name (and therefore a <c>NameKey</c>) the seed also uses. That is
/// enough for <c>IsSeededKey</c> to protect it without a booted seed ever having run; the durability half
/// — a delete that is refused AND a retire that survives the next boot — lives in
/// <c>DevelopmentSeedClassificationTests</c>, which already boots twice against one database.
/// </para>
///
/// <para>
/// Every destructive assertion counts rows in the table, the same discipline
/// <c>ClassificationAdminApiTests</c> uses: a 409 that had already removed the row would satisfy any
/// body-shaped assertion, and that is exactly the failure this guard exists to prevent.
/// </para>
/// </summary>
[Collection(DatabaseCollection.Name)]
public class ClassificationSeedProtectedDeleteTests : IntegrationTest
{
    public ClassificationSeedProtectedDeleteTests(SqlServerFixture sql) : base(sql) { }

    private const string Route = "/api/v1/classifications";

    private async Task<Guid> ArrangeSchoolAsync(string code = "USA")
    {
        await using var db = NewDbContext();
        var school = TestData.NewSchool(code);
        db.Schools.Add(school);
        await db.SaveChangesAsync();
        return school.Id;
    }

    private async Task<Guid> ArrangeClassificationAsync(
        Guid schoolId, string name, string axis = ClassificationAxis.Personnel, bool isActive = true)
    {
        await using var db = NewDbContext();
        var row = TestData.NewClassification(schoolId, name, axis, isActive);
        db.Classifications.Add(row);
        await db.SaveChangesAsync();
        return row.Id;
    }

    private static async Task<string?> ErrorCodeAsync(HttpResponseMessage response)
    {
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty(ClassificationsController.ErrorCodeProperty).GetString();
    }

    private async Task<bool> RowExistsAsync(Guid id)
    {
        await using var read = NewDbContext();
        return await read.Classifications.AsNoTracking().AnyAsync(c => c.Id == id);
    }

    // ---------------------------------------------------------------------------------- the main case

    /// <summary>
    /// <b>THE case.</b> An unreferenced classification named exactly one of the eight seeded values is
    /// refused with 409, <c>errorCode = "SeedProtected"</c>, a detail naming the retire route, and the
    /// row is still there afterwards.
    /// </summary>
    [Theory]
    [InlineData("STUDENT", ClassificationAxis.Student)]
    [InlineData("NAP", ClassificationAxis.Personnel)]
    [InlineData("ACAD", ClassificationAxis.Personnel)]
    [InlineData("ANT", ClassificationAxis.Personnel)]
    [InlineData("SUPERVISORY/MANAGERIAL", ClassificationAxis.Personnel)]
    [InlineData("USA FRIARS", ClassificationAxis.Friars)]
    [InlineData("C2B2", ClassificationAxis.Special)]
    [InlineData("CFI", ClassificationAxis.Special)]
    public async Task An_unreferenced_seeded_classification_is_refused_with_409_and_survives(
        string seededName, string axis)
    {
        var schoolId = await ArrangeSchoolAsync();
        var id = await ArrangeClassificationAsync(schoolId, seededName, axis);

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient();

        var response = await client.DeleteAsync($"{Route}/{id}");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(nameof(ClassificationWriteOutcome.SeedProtected), await ErrorCodeAsync(response));

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var detail = body.RootElement.GetProperty("detail").GetString() ?? "";
        Assert.Contains($"PATCH /classifications/{id}/active", detail, StringComparison.Ordinal);

        Assert.True(
            await RowExistsAsync(id),
            $"'{seededName}' was answered 409 SeedProtected but the row is gone. A refusal that has " +
            "already destroyed the thing it refused to destroy is worse than no guard at all.");
    }

    // -------------------------------------------------------------------------------------- boundaries

    /// <summary>
    /// <b>Boundary 1: an unreferenced NON-seeded row still deletes.</b> The guard is keyed on
    /// <c>NameKey</c> membership in the eight, not on "is this row unreferenced" — a row outside the
    /// eight must still take the ordinary, successful path.
    /// </summary>
    [Fact]
    public async Task An_unreferenced_non_seeded_classification_still_deletes()
    {
        var schoolId = await ArrangeSchoolAsync();
        var id = await ArrangeClassificationAsync(schoolId, "TYPOO");

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient();

        var response = await client.DeleteAsync($"{Route}/{id}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(await RowExistsAsync(id));
    }

    /// <summary>
    /// <b>Boundary 2: a REFERENCED seeded row still answers <c>InUse</c>, not <c>SeedProtected</c>.</b>
    /// The in-use check runs first, deliberately — it is the more informative refusal, and the seed
    /// guard is the LAST guard so an operator told to retire a seeded row is not also holding a
    /// reference they were never told about.
    /// </summary>
    [Fact]
    public async Task A_referenced_seeded_classification_answers_InUse_not_SeedProtected()
    {
        var schoolId = await ArrangeSchoolAsync();
        var survivorId = await ArrangeClassificationAsync(schoolId, "ACAD");
        var loserId = await ArrangeClassificationAsync(schoolId, "SOME OTHER PERSONNEL CATEGORY");

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient();

        Assert.Equal(
            HttpStatusCode.OK,
            (await client.PostAsJsonAsync(
                $"{Route}/{loserId}/merge",
                new { intoClassificationId = survivorId })).StatusCode);

        var response = await client.DeleteAsync($"{Route}/{survivorId}");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(nameof(ClassificationWriteOutcome.InUse), await ErrorCodeAsync(response));
        Assert.True(await RowExistsAsync(survivorId));
    }

    /// <summary>
    /// <b>Boundary 3: a tombstone (a seeded row merged away) still answers whatever the tombstone guard
    /// answers — unchanged, and NOT SeedProtected.</b> The merged-away branch runs before the seed
    /// guard in source order and is reached first, so a seeded name that is also a tombstone is refused
    /// as the merge record it is, not relabelled by the newer guard.
    /// </summary>
    [Fact]
    public async Task A_seeded_tombstone_still_answers_the_tombstone_guard_not_SeedProtected()
    {
        var schoolId = await ArrangeSchoolAsync();
        var survivorId = await ArrangeClassificationAsync(schoolId, "ANT");
        var loserId = await ArrangeClassificationAsync(schoolId, "ACAD"); // also a seeded key

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient();

        Assert.Equal(
            HttpStatusCode.OK,
            (await client.PostAsJsonAsync(
                $"{Route}/{loserId}/merge",
                new { intoClassificationId = survivorId })).StatusCode);

        var response = await client.DeleteAsync($"{Route}/{loserId}");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(nameof(ClassificationWriteOutcome.InUse), await ErrorCodeAsync(response));
        Assert.True(await RowExistsAsync(loserId));
    }
}
