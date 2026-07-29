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
/// The HTTP surface of Technical Plan §6.2's write endpoints: which outcome becomes which status
/// code, and what the RFC 7807 body carries.
///
/// <para>
/// The service returns an outcome enum and the controller translates it; that translation is what the
/// admin SPA and Phase 4's mobile developer code against, and it is invisible to every test that calls
/// <c>IStudentService</c> directly. A 409 quietly becoming a 500 would break a client's error handling
/// without failing a single service-level test.
/// </para>
/// </summary>
[Collection(DatabaseCollection.Name)]
public class StudentsApiTests : IntegrationTest
{
    public StudentsApiTests(SqlServerFixture sql) : base(sql) { }

    private const string Route = "/api/v1/students";

    private async Task<Guid> ArrangeSchoolAsync()
    {
        await using var db = NewDbContext();
        var school = TestData.NewSchool();
        db.Schools.Add(school);
        await db.SaveChangesAsync();
        return school.Id;
    }

    private async Task<Guid> ArrangeStudentAsync(Guid schoolId, string studentNumber = "2023-0001")
    {
        await using var db = NewDbContext();
        var student = TestData.NewStudent(schoolId, studentNumber);
        db.Students.Add(student);
        await db.SaveChangesAsync();
        return student.Id;
    }

    private static object Body(
        string studentNumber = "2023-0100",
        string firstName = "Juan",
        string lastName = "Dela Cruz") =>
        new { studentNumber, firstName, middleName = (string?)null, lastName, email = (string?)null };

    // ------------------------------------------------------------------------------ happy paths

    [Fact]
    public async Task Creating_a_student_is_201_with_a_location()
    {
        await ArrangeSchoolAsync();
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(Route, Body());

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.NotNull(response.Headers.Location);

        using var created = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var id = created.RootElement.GetProperty("id").GetGuid();

        // The Location has to resolve, or the 201 is a lie a client cannot follow.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"{Route}/{id}")).StatusCode);
    }

    [Fact]
    public async Task Updating_a_student_is_200()
    {
        var schoolId = await ArrangeSchoolAsync();
        var studentId = await ArrangeStudentAsync(schoolId);
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient();

        var response = await client.PutAsJsonAsync($"{Route}/{studentId}", Body(
            studentNumber: "2023-0001", firstName: "Maricel"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("Maricel", body.RootElement.GetProperty("firstName").GetString());
    }

    /// <summary>
    /// The read DTO has to carry the name parts, or an edit form cannot populate itself and a
    /// full-replacement PUT blanks whatever it could not read.
    /// </summary>
    [Fact]
    public async Task A_student_detail_round_trips_through_a_put_without_losing_a_field()
    {
        var schoolId = await ArrangeSchoolAsync();
        var studentId = await ArrangeStudentAsync(schoolId);
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient();

        using var read = JsonDocument.Parse(
            await (await client.GetAsync($"{Route}/{studentId}")).Content.ReadAsStringAsync());

        var student = read.RootElement;
        var response = await client.PutAsJsonAsync($"{Route}/{studentId}", new
        {
            studentNumber = student.GetProperty("studentNumber").GetString(),
            firstName = student.GetProperty("firstName").GetString(),
            middleName = student.GetProperty("middleName").GetString(),
            lastName = student.GetProperty("lastName").GetString(),
            email = student.GetProperty("email").GetString(),
            gender = student.GetProperty("gender").GetString(),
            photoUrl = student.GetProperty("photoUrl").GetString(),
            status = student.GetProperty("status").GetString(),
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var after = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(student.GetProperty("fullName").GetString(),
            after.RootElement.GetProperty("fullName").GetString());
        Assert.Equal(student.GetProperty("email").GetString(),
            after.RootElement.GetProperty("email").GetString());
    }

    [Fact]
    public async Task Deleting_a_student_is_204_and_the_student_then_404s()
    {
        var schoolId = await ArrangeSchoolAsync();
        var studentId = await ArrangeStudentAsync(schoolId);
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"{Route}/{studentId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"{Route}/{studentId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"{Route}/{studentId}")).StatusCode);
    }

    [Fact]
    public async Task Assigning_a_card_is_201_and_deactivating_it_is_204()
    {
        var schoolId = await ArrangeSchoolAsync();
        var studentId = await ArrangeStudentAsync(schoolId);
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient();

        var created = await client.PostAsJsonAsync(
            $"{Route}/{studentId}/cards", new { cardUid = "04:a7:b8:c9", label = "Primary ID" });

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        using var card = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        Assert.Equal("04A7B8C9", card.RootElement.GetProperty("cardUid").GetString());
        var cardId = card.RootElement.GetProperty("id").GetGuid();

        var deactivated = await client.DeleteAsync($"{Route}/{studentId}/cards/{cardId}");
        Assert.Equal(HttpStatusCode.NoContent, deactivated.StatusCode);

        // Deactivated, not deleted (ADR-001 D-3) — the row is still there, flagged.
        await using var read = NewDbContext();
        Assert.False((await read.RfidCards.AsNoTracking().SingleAsync(c => c.Id == cardId)).IsActive);
    }

    // ----------------------------------------------------------------------------- error shape

    /// <summary>
    /// The named requirement: a supplied ADR-001 D-2 cache column is a <b>400 carrying
    /// <c>FieldIsDerived</c></b>, never a 500 and never a silent 200.
    ///
    /// <para>
    /// This is the exact request an edit form produces if nobody strips the fields — <c>section</c> is
    /// in the body the GET handed it — so it has to be a code a client can branch on rather than prose.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Sending_a_derived_field_is_400_with_a_machine_readable_code()
    {
        var schoolId = await ArrangeSchoolAsync();
        var studentId = await ArrangeStudentAsync(schoolId);
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient();

        var response = await client.PutAsJsonAsync($"{Route}/{studentId}", new
        {
            studentNumber = "2023-0001",
            firstName = "Maria",
            lastName = "Santos",
            section = "BSFS 2-A",
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(
            nameof(StudentWriteOutcome.FieldIsDerived),
            body.RootElement.GetProperty(StudentsController.ErrorCodeProperty).GetString());

        // Still an RFC 7807 body with the correlation handle every other error carries.
        Assert.False(string.IsNullOrWhiteSpace(
            body.RootElement.GetProperty("traceId").GetString()));
    }

    [Fact]
    public async Task A_duplicate_student_number_is_409()
    {
        var schoolId = await ArrangeSchoolAsync();
        await ArrangeStudentAsync(schoolId, "2023-0001");
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(Route, Body(studentNumber: "2023-0001"));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(
            nameof(StudentWriteOutcome.DuplicateStudentNumber),
            body.RootElement.GetProperty(StudentsController.ErrorCodeProperty).GetString());
    }

    [Fact]
    public async Task A_card_uid_active_on_another_student_is_409()
    {
        var schoolId = await ArrangeSchoolAsync();
        var ownerId = await ArrangeStudentAsync(schoolId, "2023-0001");
        var claimantId = await ArrangeStudentAsync(schoolId, "2023-0002");

        await using (var db = NewDbContext())
        {
            db.RfidCards.Add(TestData.NewCard(schoolId, ownerId, "04A7B8C9"));
            await db.SaveChangesAsync();
        }

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            $"{Route}/{claimantId}/cards", new { cardUid = "04-A7-B8-C9" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(
            nameof(StudentWriteOutcome.CardUidInUse),
            body.RootElement.GetProperty(StudentsController.ErrorCodeProperty).GetString());
    }

    /// <summary>
    /// A card body that omits <c>cardUid</c> entirely is a 400, not a 500.
    ///
    /// <para>
    /// The property is non-nullable by annotation, which binds nothing — the value arrives null.
    /// MVC's implicit-required validation refuses it before the service is reached, and this pins that
    /// rather than assuming it; <c>StudentWriteTests.A_null_card_uid_is_refused_rather_than_dereferenced</c>
    /// covers the service-level half, because the two layers must both hold and only one of them is
    /// the contract Phase 4 publishes.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_card_body_with_no_uid_is_400()
    {
        var schoolId = await ArrangeSchoolAsync();
        var studentId = await ArrangeStudentAsync(schoolId);
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            $"{Route}/{studentId}/cards", new { label = "Primary ID" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        await using var read = NewDbContext();
        Assert.Empty(await read.RfidCards.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task A_blank_name_is_400()
    {
        await ArrangeSchoolAsync();
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(Route, Body(firstName: "   "));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Writing_to_an_unknown_student_is_404()
    {
        await ArrangeSchoolAsync();
        var unknown = Guid.NewGuid();
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.NotFound,
            (await client.PutAsJsonAsync($"{Route}/{unknown}", Body())).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.DeleteAsync($"{Route}/{unknown}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.PostAsJsonAsync($"{Route}/{unknown}/cards", new { cardUid = "04A7B8C9" })).StatusCode);
    }
}
