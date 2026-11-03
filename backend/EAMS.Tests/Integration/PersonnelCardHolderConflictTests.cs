using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EAMS.Application.Abstractions;
using EAMS.Application.Dtos;
using EAMS.Domain;
using EAMS.Tests.Integration.Infrastructure;
using Xunit;

namespace EAMS.Tests.Integration;

/// <summary>
/// #706 F2 (JJ, 2026-11-02): the duplicate-card 409 names its holder as a structured ProblemDetails extension,
/// <c>holder: { personType, number, name }</c>, so the SPA can say "This card is already assigned to
/// &lt;name / number&gt;." without parsing the detail. Both routes, both directions, both populations.
/// </summary>
[Collection(DatabaseCollection.Name)]
public class PersonnelCardHolderConflictTests : IntegrationTest
{
    public PersonnelCardHolderConflictTests(SqlServerFixture sql) : base(sql) { }

    private const string Uid = "04A7B8C9";
    private const string HolderStudentNumber = "2023-0001";
    private const string HolderStudentName = "Maria Reyes Santos";
    private const string HolderPersonnelNumber = "EMP-0001";
    private const string HolderPersonnelName = "Jose P Rizal";

    private async Task<Guid> ArrangeSchoolAsync()
    {
        await using var db = NewDbContext();
        var school = TestData.NewSchool();
        db.Schools.Add(school);
        await db.SaveChangesAsync();
        return school.Id;
    }

    /// <summary>A student whose active card holds <see cref="Uid"/>.</summary>
    private async Task ArrangeStudentHolderAsync(Guid schoolId)
    {
        await using var db = NewDbContext();
        var student = TestData.NewStudent(schoolId, HolderStudentNumber);
        db.Students.Add(student);
        db.RfidCards.Add(TestData.NewCard(schoolId, student.Id, Uid));
        await db.SaveChangesAsync();
    }

    /// <summary>A personnel record whose <c>RfidUid</c> is <see cref="Uid"/>.</summary>
    private async Task ArrangePersonnelHolderAsync(Guid schoolId)
    {
        await using var db = NewDbContext();
        db.Personnel.Add(TestData.NewPersonnel(schoolId, HolderPersonnelNumber, rfidUid: Uid));
        await db.SaveChangesAsync();
    }

    private async Task<Guid> ArrangeOtherStudentAsync(Guid schoolId)
    {
        await using var db = NewDbContext();
        var other = TestData.NewStudent(schoolId, "2023-0002", firstName: "Ana", middleName: null, lastName: "Cruz");
        db.Students.Add(other);
        await db.SaveChangesAsync();
        return other.Id;
    }

    private async Task<JsonElement> StudentCardConflictAsync(Guid schoolId, Guid studentId)
    {
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory, schoolId);

        var response = await client.PostAsJsonAsync(
            $"/api/v1/students/{studentId}/cards", new { cardUid = "04-a7-b8-c9", label = "Replacement" });

        return await ConflictBodyAsync(response, nameof(StudentWriteOutcome.CardUidInUse));
    }

    private async Task<JsonElement> PersonnelCreateConflictAsync(Guid schoolId)
    {
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory, schoolId);

        var response = await client.PostAsJsonAsync("/api/v1/personnel", new
        {
            personnelNumber = "EMP-0002", firstName = "Andres", lastName = "Bonifacio", rfidUid = Uid,
        });

        return await ConflictBodyAsync(response, nameof(PersonnelWriteOutcome.DuplicateRfid));
    }

    private static async Task<JsonElement> ConflictBodyAsync(HttpResponseMessage response, string code)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.Conflict, $"{(int)response.StatusCode} {text}");

        using var document = JsonDocument.Parse(text);
        Assert.Equal(code, document.RootElement.GetProperty("code").GetString());
        return document.RootElement.Clone();
    }

    private static void AssertHolder(JsonElement body, string personType, string number, string name)
    {
        var holder = body.GetProperty("holder");
        Assert.Equal(JsonValueKind.Object, holder.ValueKind);

        // Exactly the three members — no id, email or position rides along.
        Assert.Equal(
            ["name", "number", "personType"],
            holder.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(personType, holder.GetProperty("personType").GetString());
        Assert.Equal(number, holder.GetProperty("number").GetString());
        Assert.Equal(name, holder.GetProperty("name").GetString());
    }

    [Fact]
    public async Task Student_card_held_by_personnel_409_names_the_personnel_holder()
    {
        var schoolId = await ArrangeSchoolAsync();
        await ArrangePersonnelHolderAsync(schoolId);
        var studentId = await ArrangeOtherStudentAsync(schoolId);

        var body = await StudentCardConflictAsync(schoolId, studentId);

        AssertHolder(body, AttendeePersonType.Personnel, HolderPersonnelNumber, HolderPersonnelName);
    }

    [Fact]
    public async Task Student_card_held_by_another_student_409_names_the_student_holder()
    {
        var schoolId = await ArrangeSchoolAsync();
        await ArrangeStudentHolderAsync(schoolId);
        var studentId = await ArrangeOtherStudentAsync(schoolId);

        var body = await StudentCardConflictAsync(schoolId, studentId);

        AssertHolder(body, AttendeePersonType.Student, HolderStudentNumber, HolderStudentName);
    }

    [Fact]
    public async Task Personnel_card_held_by_a_student_409_names_the_student_holder()
    {
        var schoolId = await ArrangeSchoolAsync();
        await ArrangeStudentHolderAsync(schoolId);

        var body = await PersonnelCreateConflictAsync(schoolId);

        AssertHolder(body, AttendeePersonType.Student, HolderStudentNumber, HolderStudentName);
    }

    [Fact]
    public async Task Personnel_card_held_by_other_personnel_409_names_the_personnel_holder()
    {
        var schoolId = await ArrangeSchoolAsync();
        await ArrangePersonnelHolderAsync(schoolId);

        var body = await PersonnelCreateConflictAsync(schoolId);

        AssertHolder(body, AttendeePersonType.Personnel, HolderPersonnelNumber, HolderPersonnelName);
    }

    [Fact]
    public async Task Personnel_update_onto_a_held_card_409_names_the_holder()
    {
        var schoolId = await ArrangeSchoolAsync();
        await ArrangeStudentHolderAsync(schoolId);
        Guid personnelId;
        await using (var db = NewDbContext())
        {
            var person = TestData.NewPersonnel(schoolId, "EMP-0002", "Andres", null, "Bonifacio");
            db.Personnel.Add(person);
            await db.SaveChangesAsync();
            personnelId = person.Id;
        }

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory, schoolId);
        var response = await client.PutAsJsonAsync($"/api/v1/personnel/{personnelId}", new
        {
            personnelNumber = "EMP-0002", firstName = "Andres", lastName = "Bonifacio", rfidUid = Uid,
        });

        var body = await ConflictBodyAsync(response, nameof(PersonnelWriteOutcome.DuplicateRfid));
        AssertHolder(body, AttendeePersonType.Student, HolderStudentNumber, HolderStudentName);
    }

    /// <summary>A 409 that is not about a card carries no holder.</summary>
    [Fact]
    public async Task Duplicate_personnel_number_409_has_no_holder()
    {
        var schoolId = await ArrangeSchoolAsync();
        await ArrangePersonnelHolderAsync(schoolId);

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory, schoolId);
        var response = await client.PostAsJsonAsync("/api/v1/personnel", new
        {
            personnelNumber = HolderPersonnelNumber, firstName = "Andres", lastName = "Bonifacio",
        });

        var body = await ConflictBodyAsync(response, nameof(PersonnelWriteOutcome.DuplicateNumber));
        Assert.False(body.TryGetProperty("holder", out _));
    }
}
