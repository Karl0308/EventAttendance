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
/// The Pre-Registration surface over real HTTP — open a session against an audience, register attendees by
/// manual choice and by tap, and the duplicate/capacity/closed/unrecognized guards that make the counter
/// trustworthy. HTTP tests, and every count assertion reads the session back rather than trusting a body.
/// </summary>
[Collection(DatabaseCollection.Name)]
public class PreRegistrationApiTests : IntegrationTest
{
    public PreRegistrationApiTests(SqlServerFixture sql) : base(sql) { }

    private const string Base = "/api/v1/pre-registration";

    private async Task<Guid> ArrangeAudienceAsync(bool audienceActive = true)
    {
        await using var db = NewDbContext();
        var school = TestData.NewSchool();
        db.Schools.Add(school);

        var classification = TestData.NewEventClassification(school.Id, "Institutional Events");
        db.EventClassifications.Add(classification);

        var audience = new AudienceDefinition
        {
            SchoolId = school.Id,
            EventClassificationId = classification.Id,
            Name = "Everyone",
            NameKey = AudienceText.KeyFor("Everyone"),
            AudienceType = AudienceType.UniversityWide,
            CriteriaJson = "{\"scope\":\"Both\"}",
            IsActive = audienceActive,
        };
        db.AudienceDefinitions.Add(audience);
        await db.SaveChangesAsync();
        return audience.Id;
    }

    private async Task<(Guid StudentId, string CardUid, Guid PersonnelId)> ArrangePeopleAsync()
    {
        await using var db = NewDbContext();
        var schoolId = await db.Schools.Select(s => s.Id).FirstAsync();

        var student = TestData.NewStudent(schoolId, "2023-0001", lastName: "Santos");
        db.Students.Add(student);
        const string cardUid = "04A1B2C3";
        db.RfidCards.Add(TestData.NewCard(schoolId, student.Id, cardUid));

        var personnel = TestData.NewPersonnel(schoolId, "EMP-0001", lastName: "Rizal", rfidUid: "05D4E5F6");
        db.Personnel.Add(personnel);

        await db.SaveChangesAsync();
        return (student.Id, cardUid, personnel.Id);
    }

    private static async Task<string?> ErrorCodeAsync(HttpResponseMessage response)
    {
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty(PreRegistrationController.ErrorCodeProperty).GetString();
    }

    private static async Task<Guid> CreateSessionAsync(
        HttpClient client, Guid audienceId, int capacity = 100, string name = "Freshman Orientation")
    {
        var created = await client.PostAsJsonAsync(
            $"{Base}/sessions", new { name, audienceDefinitionId = audienceId, capacity });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var body = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("id").GetGuid();
    }

    [Fact]
    public async Task A_session_can_be_opened_and_carries_a_live_counter()
    {
        var audienceId = await ArrangeAudienceAsync();

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory);

        var created = await client.PostAsJsonAsync(
            $"{Base}/sessions",
            new { name = "Freshman Orientation", audienceDefinitionId = audienceId, capacity = 50 });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        using var body = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        Assert.Equal("Everyone", body.RootElement.GetProperty("audienceName").GetString());
        Assert.Equal(50, body.RootElement.GetProperty("capacity").GetInt32());
        Assert.Equal(0, body.RootElement.GetProperty("registeredCount").GetInt32());
        Assert.False(body.RootElement.GetProperty("isFull").GetBoolean());

        Assert.NotNull(created.Headers.Location);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(created.Headers.Location)).StatusCode);
    }

    [Theory]
    [InlineData("", 50)]
    [InlineData("   ", 50)]
    [InlineData("Valid", 0)]
    [InlineData("Valid", -5)]
    public async Task A_session_with_a_bad_name_or_capacity_is_refused(string name, int capacity)
    {
        var audienceId = await ArrangeAudienceAsync();

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory);

        var response = await client.PostAsJsonAsync(
            $"{Base}/sessions", new { name, audienceDefinitionId = audienceId, capacity });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(nameof(PreRegistrationSessionOutcome.ValidationFailed), await ErrorCodeAsync(response));
    }

    [Fact]
    public async Task A_session_against_an_inactive_audience_is_refused()
    {
        var audienceId = await ArrangeAudienceAsync(audienceActive: false);

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory);

        var response = await client.PostAsJsonAsync(
            $"{Base}/sessions", new { name = "X", audienceDefinitionId = audienceId, capacity = 10 });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(nameof(PreRegistrationSessionOutcome.AudienceUnavailable), await ErrorCodeAsync(response));
    }

    [Fact]
    public async Task An_attendee_can_be_registered_manually_and_duplicates_are_refused()
    {
        var audienceId = await ArrangeAudienceAsync();
        var (studentId, _, _) = await ArrangePeopleAsync();

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory);
        var sessionId = await CreateSessionAsync(client, audienceId);

        var register = await client.PostAsJsonAsync(
            $"{Base}/sessions/{sessionId}/register",
            new { method = "Manual", type = "Student", attendeeId = studentId });
        Assert.Equal(HttpStatusCode.OK, register.StatusCode);

        using var body = JsonDocument.Parse(await register.Content.ReadAsStringAsync());
        Assert.Equal(1, body.RootElement.GetProperty("registeredCount").GetInt32());
        Assert.Equal("Student", body.RootElement.GetProperty("registrant").GetProperty("type").GetString());

        // Same student again → duplicate.
        var again = await client.PostAsJsonAsync(
            $"{Base}/sessions/{sessionId}/register",
            new { method = "Manual", type = "Student", attendeeId = studentId });
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal(nameof(PreRegisterOutcome.Duplicate), await ErrorCodeAsync(again));

        await using var read = NewDbContext();
        Assert.Equal(1, await read.PreRegistrations.CountAsync(p => p.SessionId == sessionId));
    }

    [Fact]
    public async Task An_attendee_can_be_registered_by_card_and_an_unknown_card_is_unrecognized()
    {
        var audienceId = await ArrangeAudienceAsync();
        var (_, cardUid, _) = await ArrangePeopleAsync();

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory);
        var sessionId = await CreateSessionAsync(client, audienceId);

        // A separator and lower case — normalized before lookup (CLAUDE.md).
        var tap = await client.PostAsJsonAsync(
            $"{Base}/sessions/{sessionId}/register",
            new { method = "Tapped", cardUid = $"{cardUid[..4]}-{cardUid[4..]}".ToLowerInvariant() });
        Assert.Equal(HttpStatusCode.OK, tap.StatusCode);
        using var body = JsonDocument.Parse(await tap.Content.ReadAsStringAsync());
        Assert.Equal("Tapped", body.RootElement.GetProperty("registrant").GetProperty("method").GetString());

        var unknown = await client.PostAsJsonAsync(
            $"{Base}/sessions/{sessionId}/register", new { method = "Tapped", cardUid = "DEADBEEF" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, unknown.StatusCode);
        Assert.Equal(nameof(PreRegisterOutcome.UnrecognizedCard), await ErrorCodeAsync(unknown));
    }

    [Fact]
    public async Task Personnel_can_be_registered_and_capacity_is_enforced()
    {
        var audienceId = await ArrangeAudienceAsync();
        var (studentId, _, personnelId) = await ArrangePeopleAsync();

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory);
        var sessionId = await CreateSessionAsync(client, audienceId, capacity: 1);

        var first = await client.PostAsJsonAsync(
            $"{Base}/sessions/{sessionId}/register",
            new { method = "Manual", type = "Personnel", attendeeId = personnelId });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        // Capacity is 1 and it is now full — a second, different attendee is refused.
        var full = await client.PostAsJsonAsync(
            $"{Base}/sessions/{sessionId}/register",
            new { method = "Manual", type = "Student", attendeeId = studentId });
        Assert.Equal(HttpStatusCode.Conflict, full.StatusCode);
        Assert.Equal(nameof(PreRegisterOutcome.CapacityReached), await ErrorCodeAsync(full));
    }

    [Fact]
    public async Task A_closed_session_refuses_registration()
    {
        var audienceId = await ArrangeAudienceAsync();
        var (studentId, _, _) = await ArrangePeopleAsync();

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory);
        var sessionId = await CreateSessionAsync(client, audienceId);

        Assert.Equal(HttpStatusCode.OK,
            (await client.PatchAsJsonAsync($"{Base}/sessions/{sessionId}/close", new { isClosed = true }))
                .StatusCode);

        var register = await client.PostAsJsonAsync(
            $"{Base}/sessions/{sessionId}/register",
            new { method = "Manual", type = "Student", attendeeId = studentId });
        Assert.Equal(HttpStatusCode.Conflict, register.StatusCode);
        Assert.Equal(nameof(PreRegisterOutcome.SessionClosed), await ErrorCodeAsync(register));
    }

    [Fact]
    public async Task A_registrant_can_be_removed_which_frees_a_slot()
    {
        var audienceId = await ArrangeAudienceAsync();
        var (studentId, _, personnelId) = await ArrangePeopleAsync();

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory);
        var sessionId = await CreateSessionAsync(client, audienceId, capacity: 1);

        var register = await client.PostAsJsonAsync(
            $"{Base}/sessions/{sessionId}/register",
            new { method = "Manual", type = "Student", attendeeId = studentId });
        using var registered = JsonDocument.Parse(await register.Content.ReadAsStringAsync());
        var registrantId = registered.RootElement.GetProperty("registrant").GetProperty("id").GetGuid();

        // Full now — remove the student, and the freed slot lets personnel in.
        var removed = await client.DeleteAsync($"{Base}/sessions/{sessionId}/registrants/{registrantId}");
        Assert.Equal(HttpStatusCode.OK, removed.StatusCode);
        using var removedBody = JsonDocument.Parse(await removed.Content.ReadAsStringAsync());
        Assert.Equal(0, removedBody.RootElement.GetProperty("registeredCount").GetInt32());

        var second = await client.PostAsJsonAsync(
            $"{Base}/sessions/{sessionId}/register",
            new { method = "Manual", type = "Personnel", attendeeId = personnelId });
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        var list = await client.GetAsync($"{Base}/sessions/{sessionId}/registrants");
        using var listBody = JsonDocument.Parse(await list.Content.ReadAsStringAsync());
        Assert.Equal(1, listBody.RootElement.GetArrayLength());
        Assert.Equal("Personnel", listBody.RootElement[0].GetProperty("type").GetString());
    }

    [Theory]
    [InlineData("Nonsense", null, null)]
    [InlineData("Manual", "Student", null)]
    [InlineData("Tapped", null, null)]
    public async Task A_malformed_registration_is_a_400(string method, string? type, string? cardUid)
    {
        var audienceId = await ArrangeAudienceAsync();

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory);
        var sessionId = await CreateSessionAsync(client, audienceId);

        var response = await client.PostAsJsonAsync(
            $"{Base}/sessions/{sessionId}/register",
            new { method, type, cardUid, attendeeId = (Guid?)null });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(nameof(PreRegisterOutcome.ValidationFailed), await ErrorCodeAsync(response));
    }

    [Fact]
    public async Task An_unknown_session_is_a_404()
    {
        await ArrangeAudienceAsync();
        var missing = Guid.NewGuid();

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory);

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"{Base}/sessions/{missing}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.GetAsync($"{Base}/sessions/{missing}/registrants")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.PostAsJsonAsync($"{Base}/sessions/{missing}/register",
                new { method = "Manual", type = "Student", attendeeId = Guid.NewGuid() })).StatusCode);
    }

    [Fact]
    public async Task A_manual_registration_of_an_unknown_attendee_is_a_404()
    {
        var audienceId = await ArrangeAudienceAsync();

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory);
        var sessionId = await CreateSessionAsync(client, audienceId);

        var response = await client.PostAsJsonAsync(
            $"{Base}/sessions/{sessionId}/register",
            new { method = "Manual", type = "Student", attendeeId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(nameof(PreRegisterOutcome.AttendeeNotFound), await ErrorCodeAsync(response));
    }
}
