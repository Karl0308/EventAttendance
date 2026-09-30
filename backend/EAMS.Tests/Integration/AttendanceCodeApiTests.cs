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
/// The Live Attendance attendance-code surface over real HTTP — generate (idempotent, and the deliberate
/// regenerate), list, and email with its per-recipient tally. The email transport is the logging no-op this
/// build ships, so a send that "succeeds" means the flow ran, the tally is right, and the sent rows recorded
/// their <c>LastEmailedAt</c>/<c>EmailCount</c> — not that a mail server was involved.
/// </summary>
[Collection(DatabaseCollection.Name)]
public class AttendanceCodeApiTests : IntegrationTest
{
    public AttendanceCodeApiTests(SqlServerFixture sql) : base(sql) { }

    private static string Route(Guid eventId) => $"/api/v1/events/{eventId}/attendance-codes";

    /// <summary>
    /// A school, an event, and three attendees (each with an attendance record). The third has no email
    /// address, for the skip tally.
    /// </summary>
    private async Task<(Guid EventId, Guid[] StudentIds)> ArrangeAsync()
    {
        await using var db = NewDbContext();
        var school = TestData.NewSchool();
        db.Schools.Add(school);

        var ev = TestData.NewEvent(school.Id);
        db.Events.Add(ev);

        var ids = new Guid[3];
        for (var i = 0; i < 3; i++)
        {
            var student = TestData.NewStudent(school.Id, $"2023-{i:0000}", lastName: $"Attendee{i}");
            if (i == 2) student.Email = null; // the no-email case
            db.Students.Add(student);
            db.AttendanceRecords.Add(new AttendanceRecord
            {
                SchoolId = school.Id,
                EventId = ev.Id,
                StudentId = student.Id,
                CheckInAt = TestData.Now,
                Status = "Present",
            });
            ids[i] = student.Id;
        }

        await db.SaveChangesAsync();
        return (ev.Id, ids);
    }

    private static async Task<string?> ErrorCodeAsync(HttpResponseMessage response)
    {
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty(AttendanceCodesController.ErrorCodeProperty).GetString();
    }

    [Fact]
    public async Task Generate_issues_one_unique_code_per_attendee_and_is_idempotent()
    {
        var (eventId, _) = await ArrangeAsync();

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory);

        var first = await client.PostAsync($"{Route(eventId)}/generate", null);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        using var firstBody = JsonDocument.Parse(await first.Content.ReadAsStringAsync());
        Assert.Equal(3, firstBody.RootElement.GetProperty("created").GetInt32());
        Assert.Equal(0, firstBody.RootElement.GetProperty("alreadyHad").GetInt32());

        var codes = firstBody.RootElement.GetProperty("codes").EnumerateArray()
            .Select(c => c.GetProperty("code").GetString()!)
            .ToList();
        Assert.Equal(3, codes.Count);
        Assert.All(codes, c => Assert.False(string.IsNullOrWhiteSpace(c)));
        Assert.Equal(3, codes.Distinct().Count()); // unique within the event

        // A second generate adds nothing and keeps every existing code.
        var second = await client.PostAsync($"{Route(eventId)}/generate", null);
        using var secondBody = JsonDocument.Parse(await second.Content.ReadAsStringAsync());
        Assert.Equal(0, secondBody.RootElement.GetProperty("created").GetInt32());
        Assert.Equal(3, secondBody.RootElement.GetProperty("alreadyHad").GetInt32());

        var codesAgain = secondBody.RootElement.GetProperty("codes").EnumerateArray()
            .Select(c => c.GetProperty("code").GetString()!)
            .ToHashSet();
        Assert.Equal(codes.ToHashSet(), codesAgain);
    }

    [Fact]
    public async Task Regenerate_rewrites_every_code()
    {
        var (eventId, _) = await ArrangeAsync();

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory);

        var first = await client.PostAsync($"{Route(eventId)}/generate", null);
        using var firstBody = JsonDocument.Parse(await first.Content.ReadAsStringAsync());
        var before = firstBody.RootElement.GetProperty("codes").EnumerateArray()
            .ToDictionary(c => c.GetProperty("studentId").GetGuid(), c => c.GetProperty("code").GetString());

        var regen = await client.PostAsync($"{Route(eventId)}/generate?regenerate=true", null);
        Assert.Equal(HttpStatusCode.OK, regen.StatusCode);
        using var regenBody = JsonDocument.Parse(await regen.Content.ReadAsStringAsync());
        Assert.Equal(3, regenBody.RootElement.GetProperty("regenerated").GetInt32());
        Assert.Equal(0, regenBody.RootElement.GetProperty("created").GetInt32());

        var after = regenBody.RootElement.GetProperty("codes").EnumerateArray()
            .ToDictionary(c => c.GetProperty("studentId").GetGuid(), c => c.GetProperty("code").GetString());

        foreach (var (studentId, code) in before)
        {
            Assert.NotEqual(code, after[studentId]);
        }
    }

    [Fact]
    public async Task Email_all_sends_to_attendees_with_an_email_and_skips_the_rest()
    {
        var (eventId, _) = await ArrangeAsync();

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory);

        await client.PostAsync($"{Route(eventId)}/generate", null);

        var email = await client.PostAsJsonAsync($"{Route(eventId)}/email", new { mode = "All" });
        Assert.Equal(HttpStatusCode.OK, email.StatusCode);

        using var body = JsonDocument.Parse(await email.Content.ReadAsStringAsync());
        Assert.Equal(3, body.RootElement.GetProperty("requested").GetInt32());
        Assert.Equal(2, body.RootElement.GetProperty("sent").GetInt32());
        Assert.Equal(1, body.RootElement.GetProperty("skippedNoEmail").GetInt32());
        Assert.Equal(0, body.RootElement.GetProperty("skippedNoCode").GetInt32());
        Assert.Equal(0, body.RootElement.GetProperty("failed").GetInt32());

        // The two sent rows recorded the send.
        await using var read = NewDbContext();
        var emailed = await read.EventAttendanceCodes.AsNoTracking()
            .Where(c => c.EventId == eventId && c.EmailCount > 0)
            .ToListAsync();
        Assert.Equal(2, emailed.Count);
        Assert.All(emailed, c => Assert.NotNull(c.LastEmailedAt));
    }

    [Fact]
    public async Task Email_all_before_generating_skips_everyone_for_no_code()
    {
        var (eventId, _) = await ArrangeAsync();

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory);

        var email = await client.PostAsJsonAsync($"{Route(eventId)}/email", new { mode = "All" });
        using var body = JsonDocument.Parse(await email.Content.ReadAsStringAsync());
        Assert.Equal(3, body.RootElement.GetProperty("requested").GetInt32());
        Assert.Equal(0, body.RootElement.GetProperty("sent").GetInt32());
        Assert.Equal(3, body.RootElement.GetProperty("skippedNoCode").GetInt32());
    }

    [Fact]
    public async Task Email_selected_targets_only_the_chosen_attendees()
    {
        var (eventId, ids) = await ArrangeAsync();

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory);

        await client.PostAsync($"{Route(eventId)}/generate", null);

        var email = await client.PostAsJsonAsync($"{Route(eventId)}/email",
            new { mode = "Selected", studentIds = new[] { ids[0] } });
        Assert.Equal(HttpStatusCode.OK, email.StatusCode);

        using var body = JsonDocument.Parse(await email.Content.ReadAsStringAsync());
        Assert.Equal(1, body.RootElement.GetProperty("requested").GetInt32());
        Assert.Equal(1, body.RootElement.GetProperty("sent").GetInt32());
    }

    [Fact]
    public async Task Email_selected_with_no_ids_is_a_400()
    {
        var (eventId, _) = await ArrangeAsync();

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory);

        var email = await client.PostAsJsonAsync($"{Route(eventId)}/email",
            new { mode = "Selected", studentIds = Array.Empty<Guid>() });
        Assert.Equal(HttpStatusCode.BadRequest, email.StatusCode);
        Assert.Equal(nameof(AttendanceCodeOutcome.ValidationFailed), await ErrorCodeAsync(email));
    }

    [Fact]
    public async Task An_unknown_mode_is_a_400()
    {
        var (eventId, _) = await ArrangeAsync();

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory);

        var email = await client.PostAsJsonAsync($"{Route(eventId)}/email", new { mode = "Everyone" });
        Assert.Equal(HttpStatusCode.BadRequest, email.StatusCode);
        Assert.Equal(nameof(AttendanceCodeOutcome.ValidationFailed), await ErrorCodeAsync(email));
    }

    [Fact]
    public async Task An_unknown_event_is_a_404_on_every_route()
    {
        await ArrangeAsync();
        var missing = Guid.NewGuid();

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory);

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(Route(missing))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.PostAsync($"{Route(missing)}/generate", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.PostAsJsonAsync($"{Route(missing)}/email", new { mode = "All" })).StatusCode);
    }
}
