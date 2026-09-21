using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EAMS.Application.Abstractions;
using EAMS.Domain;
using EAMS.Tests.Integration.Infrastructure;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace EAMS.Tests.Integration;

/// <summary>
/// P5 (client QA Q20): <c>Events.IssuesCertificates</c>, the per-event "issues certificates" setting.
/// Only the flag — certificates themselves are not built.
///
/// <para>
/// <b>The test that matters most is the PUT one.</b> <c>PUT /events/{id}</c> is a full replacement,
/// and every client written before this field existed sends a body without it. Had the request field
/// been a plain <c>bool</c>, each of those edits would reset a stored <c>true</c> to <c>false</c> and
/// answer 200 — an administrator renames an event and its certificates are silently switched off.
/// The field is <c>bool?</c>, and null keeps the stored value.
/// </para>
///
/// <para>
/// The schema is asserted from <c>sys.columns</c> and <c>sys.default_constraints</c>, never from the EF
/// model (project memory: verify schema against the database, not the model) — the migration writes
/// this column in raw SQL precisely because the model cannot express the constraint name.
/// </para>
/// </summary>
[Collection(DatabaseCollection.Name)]
public class EventIssuesCertificatesTests : IntegrationTest
{
    public EventIssuesCertificatesTests(SqlServerFixture sql) : base(sql) { }

    private const string Route = "/api/v1/events";

    /// <summary>The migration immediately before <c>IssuesCertificatesFlag</c>.</summary>
    private const string PreviousMigration = "GrantReportsReadToAdminRoles";

    private const string DefaultConstraintName = "DF_Events_IssuesCertificates";

    // ------------------------------------------------------------------------------------ plumbing

    private async Task<Guid> ArrangeSchoolAsync()
    {
        await using var db = NewDbContext();
        var school = TestData.NewSchool();
        db.Schools.Add(school);
        await db.SaveChangesAsync();
        return school.Id;
    }

    /// <summary>A create body with a fixed window, and the flag only when the caller names it.</summary>
    private static Dictionary<string, object?> CreateBody(bool? issuesCertificates = null, bool send = false)
    {
        var body = new Dictionary<string, object?>
        {
            ["name"] = "University Convocation 2026",
            ["description"] = "Annual convocation.",
            ["location"] = "USA Gymnasium",
            ["startAt"] = "2026-08-01T01:00:00Z",
            ["endAt"] = "2026-08-01T04:00:00Z",
            ["attendanceMode"] = "Single",
            ["graceMinutes"] = 15,
            ["requireRegistration"] = false,
        };
        if (send) body["issuesCertificates"] = issuesCertificates;
        return body;
    }

    /// <summary>
    /// A PUT body that re-sends an event's stored fields exactly — what a well-behaved full-replace client
    /// sends — so the only thing it can change is what the test adds to it.
    /// </summary>
    private static Dictionary<string, object?> SameFieldsAs(Event stored) => new()
    {
        ["name"] = stored.Name,
        ["description"] = stored.Description,
        ["location"] = stored.Location,
        ["startAt"] = stored.StartAt,
        ["endAt"] = stored.EndAt,
        ["attendanceMode"] = stored.AttendanceMode,
        ["graceMinutes"] = stored.GraceMinutes,
        ["requireRegistration"] = stored.RequireRegistration,
    };

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response)
    {
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.Clone();
    }

    private static bool IssuesCertificatesOf(JsonElement dto) =>
        dto.GetProperty("issuesCertificates").GetBoolean();

    /// <summary>The stored value, read with raw SQL rather than through the model.</summary>
    private static async Task<bool> StoredFlagAsync(string connectionString, Guid eventId)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            "SELECT [IssuesCertificates] FROM [Events] WHERE [Id] = @id;", connection);
        command.Parameters.AddWithValue("@id", eventId);
        return (bool)(await command.ExecuteScalarAsync())!;
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<int> CountAsync(string connectionString, string sql)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        return (int)(await command.ExecuteScalarAsync())!;
    }

    private static Task<int> ColumnCountAsync(string connectionString) =>
        CountAsync(connectionString,
            "SELECT COUNT(*) FROM sys.columns " +
            "WHERE object_id = OBJECT_ID(N'dbo.Events') AND name = N'IssuesCertificates';");

    private static async Task MigrateToAsync(string connectionString, string? target)
    {
        await using var db = SqlServerFixture.NewDbContextOn(connectionString, new TestSchoolContext());
        await db.GetService<IMigrator>().MigrateAsync(target);
    }

    // ------------------------------------------------------------------------------------ the API

    [Fact]
    public async Task Event_created_without_issuesCertificates_defaults_to_false()
    {
        var schoolId = await ArrangeSchoolAsync();
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory, schoolId);

        // Neither omitted nor an explicit null may produce anything but false on a create.
        foreach (var body in new[] { CreateBody(), CreateBody(null, send: true) })
        {
            var created = await client.PostAsJsonAsync(Route, body);
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);

            var dto = await JsonAsync(created);
            Assert.False(IssuesCertificatesOf(dto));
            Assert.False(await StoredFlagAsync(Sql.ConnectionString, dto.GetProperty("id").GetGuid()));
        }
    }

    [Fact]
    public async Task IssuesCertificates_round_trips_through_post_and_put()
    {
        var schoolId = await ArrangeSchoolAsync();
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory, schoolId);

        var created = await client.PostAsJsonAsync(Route, CreateBody(true, send: true));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await JsonAsync(created)).GetProperty("id").GetGuid();
        Assert.True(IssuesCertificatesOf(await JsonAsync(created)));
        Assert.True(IssuesCertificatesOf(await JsonAsync(await client.GetAsync($"{Route}/{id}"))));
        Assert.True(await StoredFlagAsync(Sql.ConnectionString, id));

        foreach (var value in new[] { false, true, false })
        {
            var put = await client.PutAsJsonAsync($"{Route}/{id}", CreateBody(value, send: true));
            Assert.Equal(HttpStatusCode.OK, put.StatusCode);
            Assert.Equal(value, IssuesCertificatesOf(await JsonAsync(put)));
            Assert.Equal(value, IssuesCertificatesOf(await JsonAsync(await client.GetAsync($"{Route}/{id}"))));
            Assert.Equal(value, await StoredFlagAsync(Sql.ConnectionString, id));
        }
    }

    /// <summary>
    /// <b>The PUT trap.</b> A client that predates the field — the SPA until its next release — edits an
    /// event whose certificates are on. The edit must land and the flag must survive it, both when the
    /// property is absent from the body and when it is sent as an explicit <c>null</c>.
    /// </summary>
    [Fact]
    public async Task A_put_that_omits_issuesCertificates_does_not_clear_a_stored_true()
    {
        var schoolId = await ArrangeSchoolAsync();
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory, schoolId);

        var created = await client.PostAsJsonAsync(Route, CreateBody(true, send: true));
        var id = (await JsonAsync(created)).GetProperty("id").GetGuid();
        Assert.True(await StoredFlagAsync(Sql.ConnectionString, id));

        var omitted = CreateBody();
        omitted["name"] = "Renamed by an old client";
        Assert.False(omitted.ContainsKey("issuesCertificates"));

        var explicitNull = CreateBody(null, send: true);
        explicitNull["name"] = "Renamed with an explicit null";

        foreach (var body in new[] { omitted, explicitNull })
        {
            var put = await client.PutAsJsonAsync($"{Route}/{id}", body);
            Assert.Equal(HttpStatusCode.OK, put.StatusCode);

            var dto = await JsonAsync(put);
            // The edit itself landed — so a kept flag is not a request that was quietly ignored.
            Assert.Equal((string)body["name"]!, dto.GetProperty("name").GetString());
            Assert.True(IssuesCertificatesOf(dto),
                "A PUT that did not mention issuesCertificates cleared it. Every client written before " +
                "the field existed would switch certificates off on every edit, with a 200.");
            Assert.True(await StoredFlagAsync(Sql.ConnectionString, id));
        }
    }

    /// <summary>
    /// JJ's P5 call: the flag does not change what an attendance row means, so it is editable in every
    /// status — <c>Closed</c> included, where every other field stays locked. On a closed event the flag
    /// must be the <em>only</em> change; a PUT that also renames is still refused and writes nothing.
    /// </summary>
    [Fact]
    public async Task IssuesCertificates_can_change_in_every_event_status()
    {
        var schoolId = await ArrangeSchoolAsync();
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory, schoolId);

        foreach (var status in EventStatus.All)
        {
            Event stored;
            await using (var db = NewDbContext())
            {
                var ev = TestData.NewEvent(schoolId, status);
                db.Events.Add(ev);
                await db.SaveChangesAsync();
            }
            await using (var read = NewDbContext())
                stored = await read.Events.AsNoTracking().SingleAsync(e => e.Status == status);

            foreach (var value in new[] { true, false })
            {
                var body = SameFieldsAs(stored);
                body["issuesCertificates"] = value;

                var put = await client.PutAsJsonAsync($"{Route}/{stored.Id}", body);
                Assert.True(put.StatusCode == HttpStatusCode.OK,
                    $"{status}: setting issuesCertificates to {value} answered {(int)put.StatusCode}.");
                Assert.Equal(value, IssuesCertificatesOf(await JsonAsync(put)));
                Assert.Equal(value, await StoredFlagAsync(Sql.ConnectionString, stored.Id));
            }

            if (status == EventStatus.Closed)
            {
                var alsoRenames = SameFieldsAs(stored);
                alsoRenames["name"] = "Renamed after closing";
                alsoRenames["issuesCertificates"] = true;

                var refused = await client.PutAsJsonAsync($"{Route}/{stored.Id}", alsoRenames);
                Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);

                await using var read = NewDbContext();
                var after = await read.Events.AsNoTracking().SingleAsync(e => e.Id == stored.Id);
                Assert.Equal(stored.Name, after.Name);
                Assert.False(after.IssuesCertificates);
            }
        }
    }

    /// <summary>
    /// <c>GET /events</c> is also the capture app's event picker, and <c>EventDto</c> is a closed schema —
    /// so the new field reaches devices. It must be there, correct, and change nothing about which events
    /// a device is shown.
    /// </summary>
    [Fact]
    public async Task Device_event_list_carries_issuesCertificates_and_still_returns_open_events_only()
    {
        var schoolId = await ArrangeSchoolAsync();

        await using (var db = NewDbContext())
        {
            Event Named(string status, string name, bool issues)
            {
                var e = TestData.NewEvent(schoolId, status);
                e.Name = name;
                e.IssuesCertificates = issues;
                return e;
            }

            db.Events.AddRange(
                Named(EventStatus.Open, "Open With Certificates", true),
                Named(EventStatus.Open, "Open Without Certificates", false),
                Named(EventStatus.Draft, "Draft With Certificates", true),
                Named(EventStatus.Closed, "Closed With Certificates", true));
            await db.SaveChangesAsync();
        }

        var key = await IssueDeviceKeyAsync(schoolId);
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var device = factory.CreateClient().WithDeviceKey(key);

        var response = await device.GetAsync(Route);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var items = (await JsonAsync(response)).GetProperty("items").EnumerateArray()
            .ToDictionary(i => i.GetProperty("name").GetString()!, IssuesCertificatesOf);

        Assert.Equal(
            new Dictionary<string, bool>
            {
                ["Open With Certificates"] = true,
                ["Open Without Certificates"] = false,
            },
            items);
    }

    // ------------------------------------------------------------------ the Closed lock, clause by clause

    /// <summary>A <c>Closed</c> event written directly, and read back exactly as stored.</summary>
    private async Task<Event> ArrangeClosedEventAsync(Guid schoolId, bool issuesCertificates = false)
    {
        var ev = TestData.NewEvent(schoolId, EventStatus.Closed);
        ev.IssuesCertificates = issuesCertificates;
        await using (var db = NewDbContext())
        {
            db.Events.Add(ev);
            await db.SaveChangesAsync();
        }

        await using var read = NewDbContext();
        return await read.Events.AsNoTracking().SingleAsync(e => e.Id == ev.Id);
    }

    /// <summary>Everything a PUT could have written, so "nothing was written" is one comparison.</summary>
    private async Task AssertUnchangedAsync(Event before)
    {
        await using var read = NewDbContext();
        var after = await read.Events.AsNoTracking().SingleAsync(e => e.Id == before.Id);

        Assert.Equal(before.Name, after.Name);
        Assert.Equal(before.Description, after.Description);
        Assert.Equal(before.Location, after.Location);
        Assert.Equal(before.StartAt, after.StartAt);
        Assert.Equal(before.EndAt, after.EndAt);
        Assert.Equal(before.AttendanceMode, after.AttendanceMode);
        Assert.Equal(before.GraceMinutes, after.GraceMinutes);
        Assert.Equal(before.RequireRegistration, after.RequireRegistration);
        Assert.Equal(before.IssuesCertificates, after.IssuesCertificates);
        Assert.Equal(before.UpdatedAt, after.UpdatedAt);
    }

    /// <summary>
    /// <c>IsCertificatesOnlyEdit</c> requires <em>every</em> other field of <c>EventWriteRequest</c> to
    /// arrive as stored. One case per field — all eight, plus description null to empty — each changing
    /// exactly that field alongside the flag; every one is 409, writes nothing, and the refusal names the
    /// field it tripped on and says a flag-only edit is allowed.
    /// </summary>
    [Theory]
    [InlineData("name", "Name")]
    [InlineData("description", "Description")]
    [InlineData("descriptionNullToEmpty", "Description")]
    [InlineData("location", "Location")]
    [InlineData("startAt", "StartAt")]
    [InlineData("endAt", "EndAt")]
    [InlineData("graceMinutes", "GraceMinutes")]
    [InlineData("attendanceMode", "AttendanceMode")]
    [InlineData("requireRegistration", "RequireRegistration")]
    public async Task A_closed_event_refuses_the_flag_alongside_any_other_change(string field, string reported)
    {
        var schoolId = await ArrangeSchoolAsync();
        var stored = await ArrangeClosedEventAsync(schoolId);
        Assert.Null(stored.Description); // the null -> "" case depends on it

        var body = SameFieldsAs(stored);
        body["issuesCertificates"] = true;
        switch (field)
        {
            case "name": body["name"] = "Renamed after closing"; break;
            case "description": body["description"] = "Added after closing"; break;
            case "descriptionNullToEmpty": body["description"] = ""; break;
            case "location": body["location"] = "Quadrangle"; break;
            case "startAt": body["startAt"] = stored.StartAt.AddMinutes(1); break;
            case "endAt": body["endAt"] = stored.EndAt.AddHours(1); break;
            case "graceMinutes": body["graceMinutes"] = stored.GraceMinutes + 15; break;
            case "attendanceMode": body["attendanceMode"] = AttendanceMode.TimeInOut; break;
            case "requireRegistration": body["requireRegistration"] = !stored.RequireRegistration; break;
            default: throw new ArgumentOutOfRangeException(nameof(field), field, "Unmapped field.");
        }

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory, schoolId);
        var response = await client.PutAsJsonAsync($"{Route}/{stored.Id}", body);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        await AssertUnchangedAsync(stored);

        var detail = (await JsonAsync(response)).GetProperty("detail").GetString()!;
        Assert.Contains("IssuesCertificates", detail, StringComparison.Ordinal);
        Assert.Contains(reported, detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// A body that re-sends everything and omits the flag changes nothing — and a closed event answers it
    /// exactly as it did before the field existed: 409.
    /// </summary>
    [Fact]
    public async Task A_closed_event_refuses_an_identical_body_that_omits_the_flag()
    {
        var schoolId = await ArrangeSchoolAsync();
        var stored = await ArrangeClosedEventAsync(schoolId, issuesCertificates: true);

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory, schoolId);
        var response = await client.PutAsJsonAsync($"{Route}/{stored.Id}", SameFieldsAs(stored));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        await AssertUnchangedAsync(stored);
    }

    /// <summary>
    /// The name comparison is ordinal on purpose: a casing-only rename is a rename, and a closed event
    /// does not accept renames. Pinned so a later case-insensitive compare is a visible change.
    /// </summary>
    [Fact]
    public async Task A_closed_event_PUT_with_a_casing_only_name_change_alongside_the_flag_is_refused()
    {
        var schoolId = await ArrangeSchoolAsync();
        var stored = await ArrangeClosedEventAsync(schoolId);

        var body = SameFieldsAs(stored);
        body["name"] = stored.Name.ToUpperInvariant();
        body["issuesCertificates"] = true;
        Assert.NotEqual(stored.Name, (string)body["name"]!);

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory, schoolId);
        var response = await client.PutAsJsonAsync($"{Route}/{stored.Id}", body);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        await AssertUnchangedAsync(stored);
    }

    /// <summary>
    /// <c>Apply</c> trims the name before storing it, so surrounding whitespace is not a change — the
    /// predicate compares the same trimmed value, and the toggle goes through.
    /// </summary>
    [Fact]
    public async Task A_closed_event_PUT_with_only_surrounding_whitespace_on_the_name_still_changes_the_flag()
    {
        var schoolId = await ArrangeSchoolAsync();
        var stored = await ArrangeClosedEventAsync(schoolId);

        var body = SameFieldsAs(stored);
        body["name"] = $"  {stored.Name}  ";
        body["issuesCertificates"] = true;

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory, schoolId);
        var response = await client.PutAsJsonAsync($"{Route}/{stored.Id}", body);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await using var read = NewDbContext();
        var after = await read.Events.AsNoTracking().SingleAsync(e => e.Id == stored.Id);
        Assert.True(after.IssuesCertificates);
        Assert.Equal(stored.Name, after.Name);
    }

    /// <summary>
    /// A retried toggle — the value it already holds, sent again — must succeed, not turn into a 409 on
    /// the second attempt.
    /// </summary>
    [Fact]
    public async Task A_closed_event_PUT_that_repeats_the_current_flag_value_is_accepted()
    {
        var schoolId = await ArrangeSchoolAsync();

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory, schoolId);

        foreach (var value in new[] { true, false })
        {
            var stored = await ArrangeClosedEventAsync(schoolId, issuesCertificates: value);
            var body = SameFieldsAs(stored);
            body["issuesCertificates"] = value;

            var response = await client.PutAsJsonAsync($"{Route}/{stored.Id}", body);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(value, await StoredFlagAsync(Sql.ConnectionString, stored.Id));
        }
    }

    [Fact]
    public async Task A_string_value_for_issuesCertificates_is_rejected_with_400()
    {
        var schoolId = await ArrangeSchoolAsync();
        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory, schoolId);

        var created = await client.PostAsJsonAsync(Route, CreateBody());
        var id = (await JsonAsync(created)).GetProperty("id").GetGuid();

        var body = CreateBody();
        body["issuesCertificates"] = "true";

        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(Route, body)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync($"{Route}/{id}", body)).StatusCode);
        Assert.False(await StoredFlagAsync(Sql.ConnectionString, id));
    }

    /// <summary>
    /// Writes need <c>events.write</c>, which a Viewer does not hold. (An Organizer does, by the seeded
    /// matrix, and may set the flag — deliberately not asserted otherwise.)
    /// </summary>
    [Fact]
    public async Task A_viewer_cannot_set_issuesCertificates()
    {
        var schoolId = await ArrangeSchoolAsync();
        Event stored;
        await using (var db = NewDbContext())
        {
            db.Events.Add(TestData.NewEvent(schoolId, EventStatus.Draft));
            await db.SaveChangesAsync();
        }
        await using (var read = NewDbContext())
            stored = await read.Events.AsNoTracking().SingleAsync();

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var viewer = await SignedInClientAsync(factory, schoolId, EamsRoleNames.Viewer);

        var body = SameFieldsAs(stored);
        body["issuesCertificates"] = true;

        Assert.Equal(HttpStatusCode.Forbidden,
            (await viewer.PutAsJsonAsync($"{Route}/{stored.Id}", body)).StatusCode);
        await AssertUnchangedAsync(stored);
    }

    // ------------------------------------------------------------------------------------ the schema

    [Fact]
    public async Task Events_IssuesCertificates_column_is_bit_not_null_default_0_in_the_database()
    {
        await using var connection = new SqlConnection(Sql.ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            """
            SELECT t.name, c.is_nullable, dc.name, dc.definition
            FROM sys.columns AS c
            JOIN sys.types AS t ON t.user_type_id = c.user_type_id
            LEFT JOIN sys.default_constraints AS dc ON dc.object_id = c.default_object_id
            WHERE c.object_id = OBJECT_ID(N'dbo.Events') AND c.name = N'IssuesCertificates';
            """, connection);

        await using (var reader = await command.ExecuteReaderAsync())
        {
            Assert.True(await reader.ReadAsync(), "Events.IssuesCertificates does not exist in the database.");
            Assert.Equal("bit", reader.GetString(0));
            Assert.False(reader.GetBoolean(1), "Events.IssuesCertificates is nullable.");
            Assert.False(reader.IsDBNull(2), "Events.IssuesCertificates has no default constraint.");
            Assert.Equal(DefaultConstraintName, reader.GetString(2));
            // How SQL Server stores CAST(0 AS bit) — the value, not only the presence of a default.
            Assert.Equal("(CONVERT([bit],(0)))", reader.GetString(3));
            Assert.False(await reader.ReadAsync());
        }

        // And it behaves as one: a row written without the column reads false.
        var schoolId = await ArrangeSchoolAsync();
        var eventId = Guid.NewGuid();
        await ExecuteAsync(Sql.ConnectionString, $"""
            INSERT INTO [Events] ([Id], [SchoolId], [Name], [StartAt], [EndAt], [CreatedAt], [UpdatedAt])
            VALUES ('{eventId}', '{schoolId}', N'Written without the column',
                    SYSUTCDATETIME(), DATEADD(HOUR, 3, SYSUTCDATETIME()), SYSUTCDATETIME(), SYSUTCDATETIME());
            """);
        Assert.False(await StoredFlagAsync(Sql.ConnectionString, eventId));
    }

    /// <summary>
    /// The migration against a database that already holds events — the developer's <c>EAMS</c> and the
    /// deployed one. Rows written before the column existed come out of it reading false, and nothing
    /// else about them moves. <c>Down</c> then removes the column and keeps the rows.
    /// </summary>
    [Fact]
    public async Task Existing_events_read_issuesCertificates_false_after_the_migration()
    {
        var databaseName = $"EAMS_Cert_{Guid.NewGuid():N}";
        var connectionString = await Sql.CreateScratchDatabaseAsync(databaseName);

        try
        {
            await MigrateToAsync(connectionString, PreviousMigration);
            Assert.Equal(0, await ColumnCountAsync(connectionString));

            // Raw INSERTs, written the way the previous build wrote them: the head model knows a column
            // this database does not have yet.
            await ExecuteAsync(connectionString, """
                DECLARE @school UNIQUEIDENTIFIER = '11111111-1111-1111-1111-111111111111';
                DECLARE @now DATETIME2 = SYSUTCDATETIME();

                INSERT INTO Schools (Id, Name, Code, TimeZone, IsActive, CreatedAt, UpdatedAt)
                VALUES (@school, N'University of San Agustin', N'USA', N'Asia/Manila', 1, @now, @now);

                INSERT INTO Events
                    (Id, SchoolId, Name, Description, Location, StartAt, EndAt, AttendanceMode,
                     GraceMinutes, RequireRegistration, Status, IsDeleted, CreatedAt, UpdatedAt)
                VALUES
                    ('33333333-3333-3333-3333-333333333331', @school, N'Convocation', NULL, N'Gym',
                     @now, DATEADD(HOUR, 3, @now), N'Single', 15, 0, N'Open', 0, @now, @now),
                    ('33333333-3333-3333-3333-333333333332', @school, N'Foundation Day', N'Closed one', NULL,
                     @now, DATEADD(HOUR, 3, @now), N'TimeInOut', 0, 1, N'Closed', 0, @now, @now);
                """);

            await MigrateToAsync(connectionString, null);

            Assert.Equal(1, await ColumnCountAsync(connectionString));
            Assert.Equal(2, await CountAsync(connectionString, "SELECT COUNT(*) FROM [Events];"));
            Assert.Equal(2, await CountAsync(connectionString,
                "SELECT COUNT(*) FROM [Events] WHERE [IssuesCertificates] = 0;"));
            // Nothing else about the existing rows moved.
            Assert.Equal(1, await CountAsync(connectionString,
                "SELECT COUNT(*) FROM [Events] WHERE [Name] = N'Foundation Day' AND [Status] = N'Closed' " +
                "AND [AttendanceMode] = N'TimeInOut' AND [RequireRegistration] = 1;"));

            await MigrateToAsync(connectionString, PreviousMigration);

            Assert.Equal(0, await ColumnCountAsync(connectionString));
            Assert.Equal(0, await CountAsync(connectionString,
                $"SELECT COUNT(*) FROM sys.default_constraints WHERE name = N'{DefaultConstraintName}';"));
            Assert.Equal(2, await CountAsync(connectionString, "SELECT COUNT(*) FROM [Events];"));
        }
        finally
        {
            await Sql.DropScratchDatabaseAsync(databaseName);
        }
    }

    /// <summary>
    /// <b>Hard rule: no data-loss migrations.</b> Rolling back while any event has the flag set would
    /// erase an administrator's setting, so <c>Down</c> refuses with 51003 and changes nothing — the
    /// column, the value and the history row all survive, because the refusal rolls back the whole
    /// migration transaction.
    /// </summary>
    [Fact]
    public async Task Migration_down_refuses_while_any_event_issues_certificates()
    {
        var databaseName = $"EAMS_Cert_{Guid.NewGuid():N}";
        var connectionString = await Sql.CreateScratchDatabaseAsync(databaseName);
        var eventId = new Guid("33333333-3333-3333-3333-333333333331");

        try
        {
            await MigrateToAsync(connectionString, null);
            await ExecuteAsync(connectionString, $"""
                DECLARE @school UNIQUEIDENTIFIER = '11111111-1111-1111-1111-111111111111';
                DECLARE @now DATETIME2 = SYSUTCDATETIME();

                INSERT INTO Schools (Id, Name, Code, TimeZone, IsActive, CreatedAt, UpdatedAt)
                VALUES (@school, N'University of San Agustin', N'USA', N'Asia/Manila', 1, @now, @now);

                INSERT INTO Events (Id, SchoolId, Name, StartAt, EndAt, IssuesCertificates, CreatedAt, UpdatedAt)
                VALUES ('{eventId}', @school, N'Convocation', @now, DATEADD(HOUR, 3, @now), 1, @now, @now);
                """);

            var refusal = await Assert.ThrowsAsync<SqlException>(
                () => MigrateToAsync(connectionString, PreviousMigration));

            Assert.Equal(51003, refusal.Number);
            Assert.Contains("1 event(s)", refusal.Message, StringComparison.Ordinal);

            Assert.Equal(1, await ColumnCountAsync(connectionString));
            Assert.True(await StoredFlagAsync(connectionString, eventId));
            Assert.Equal(1, await CountAsync(connectionString,
                "SELECT COUNT(*) FROM [__EFMigrationsHistory] " +
                "WHERE [MigrationId] = N'20260921060959_IssuesCertificatesFlag';"));
        }
        finally
        {
            await Sql.DropScratchDatabaseAsync(databaseName);
        }
    }
}
