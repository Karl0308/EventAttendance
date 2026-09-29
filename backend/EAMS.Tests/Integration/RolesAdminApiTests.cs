using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EAMS.Api.Authorization;
using EAMS.Api.Controllers;
using EAMS.Application.Abstractions;
using EAMS.Domain;
using EAMS.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EAMS.Tests.Integration;

/// <summary>
/// §11's Role Management surface over real HTTP — creating custom roles, editing them, configuring their
/// permissions, and the guarded delete, plus the protection of the four built-in roles. HTTP tests for
/// the reason the sibling admin suites record: the guarantees are statements about the wire.
/// </summary>
[Collection(DatabaseCollection.Name)]
public class RolesAdminApiTests : IntegrationTest
{
    public RolesAdminApiTests(SqlServerFixture sql) : base(sql) { }

    private const string Route = "/api/v1/roles";

    private async Task<Guid> ArrangeSchoolAsync(string code = "USA")
    {
        await using var db = NewDbContext();
        var school = TestData.NewSchool(code);
        db.Schools.Add(school);
        await db.SaveChangesAsync();
        return school.Id;
    }

    private static async Task<string?> ErrorCodeAsync(HttpResponseMessage response)
    {
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.False(string.IsNullOrWhiteSpace(body.RootElement.GetProperty("traceId").GetString()));
        return body.RootElement.GetProperty(RolesController.ErrorCodeProperty).GetString();
    }

    private async Task<Guid> SystemRoleIdAsync(string name)
    {
        await using var db = NewDbContext();
        return await db.Roles.AsNoTracking().Where(r => r.Name == name).Select(r => r.Id).SingleAsync();
    }

    [Fact]
    public async Task An_administrator_can_create_read_update_permission_and_delete_a_custom_role()
    {
        var schoolId = await ArrangeSchoolAsync();

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory, schoolId);

        var created = await client.PostAsJsonAsync(Route, new { name = "Front Desk", description = "Reception" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var createdBody = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var id = createdBody.RootElement.GetProperty("id").GetGuid();
        Assert.False(createdBody.RootElement.GetProperty("isSystem").GetBoolean());
        Assert.Empty(createdBody.RootElement.GetProperty("permissionCodes").EnumerateArray());

        Assert.NotNull(created.Headers.Location);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(created.Headers.Location)).StatusCode);

        var updated = await client.PutAsJsonAsync(
            $"{Route}/{id}", new { name = "Front Desk Staff", description = "Reception desk" });
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);

        var setPerms = await client.PutAsJsonAsync(
            $"{Route}/{id}/permissions",
            new { permissionCodes = new[] { EamsPermissions.StudentsRead, EamsPermissions.EventsRead } });
        Assert.Equal(HttpStatusCode.OK, setPerms.StatusCode);
        using var permsBody = JsonDocument.Parse(await setPerms.Content.ReadAsStringAsync());
        var codes = permsBody.RootElement.GetProperty("permissionCodes").EnumerateArray()
            .Select(c => c.GetString()).OrderBy(c => c).ToList();
        Assert.Equal(new[] { EamsPermissions.EventsRead, EamsPermissions.StudentsRead }.OrderBy(c => c), codes);

        var deleted = await client.DeleteAsync($"{Route}/{id}");
        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);

        await using var read = NewDbContext();
        Assert.False(await read.Roles.AsNoTracking().AnyAsync(r => r.Id == id));
    }

    [Fact]
    public async Task A_duplicate_role_name_is_a_conflict()
    {
        var schoolId = await ArrangeSchoolAsync();

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory, schoolId);

        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync(Route, new { name = "Auditor" })).StatusCode);
        var clash = await client.PostAsJsonAsync(Route, new { name = "Auditor" });
        Assert.Equal(HttpStatusCode.Conflict, clash.StatusCode);
        Assert.Equal(nameof(RoleWriteOutcome.NameExists), await ErrorCodeAsync(clash));

        // And a built-in name collides too.
        var clashSystem = await client.PostAsJsonAsync(Route, new { name = EamsRoleNames.Organizer });
        Assert.Equal(HttpStatusCode.Conflict, clashSystem.StatusCode);
        Assert.Equal(nameof(RoleWriteOutcome.NameExists), await ErrorCodeAsync(clashSystem));
    }

    [Fact]
    public async Task A_blank_name_is_a_validation_failure()
    {
        var schoolId = await ArrangeSchoolAsync();

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory, schoolId);

        var response = await client.PostAsJsonAsync(Route, new { name = "   " });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(nameof(RoleWriteOutcome.ValidationFailed), await ErrorCodeAsync(response));
    }

    [Fact]
    public async Task A_built_in_role_cannot_be_edited_re_permissioned_or_deleted()
    {
        var schoolId = await ArrangeSchoolAsync();

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory, schoolId);

        var organizerId = await SystemRoleIdAsync(EamsRoleNames.Organizer);

        var renamed = await client.PutAsJsonAsync($"{Route}/{organizerId}", new { name = "Event Runner" });
        Assert.Equal(HttpStatusCode.Conflict, renamed.StatusCode);
        Assert.Equal(nameof(RoleWriteOutcome.SystemRoleProtected), await ErrorCodeAsync(renamed));

        var reperm = await client.PutAsJsonAsync(
            $"{Route}/{organizerId}/permissions", new { permissionCodes = new[] { EamsPermissions.StudentsRead } });
        Assert.Equal(HttpStatusCode.Conflict, reperm.StatusCode);
        Assert.Equal(nameof(RoleWriteOutcome.SystemRoleProtected), await ErrorCodeAsync(reperm));

        var deleted = await client.DeleteAsync($"{Route}/{organizerId}");
        Assert.Equal(HttpStatusCode.Conflict, deleted.StatusCode);
        Assert.Equal(nameof(RoleWriteOutcome.SystemRoleProtected), await ErrorCodeAsync(deleted));

        // Untouched.
        await using var db = NewDbContext();
        Assert.Equal(EamsRoleNames.Organizer, (await db.Roles.AsNoTracking().SingleAsync(r => r.Id == organizerId)).Name);
    }

    [Fact]
    public async Task Setting_a_non_human_assignable_or_unknown_permission_is_refused()
    {
        var schoolId = await ArrangeSchoolAsync();

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory, schoolId);

        var id = JsonDocument.Parse(
            await (await client.PostAsJsonAsync(Route, new { name = "Custom" })).Content.ReadAsStringAsync())
            .RootElement.GetProperty("id").GetGuid();

        // attendance.capture is the device's alone — the controller refuses it (400).
        var capture = await client.PutAsJsonAsync(
            $"{Route}/{id}/permissions", new { permissionCodes = new[] { EamsPermissions.AttendanceCapture } });
        Assert.Equal(HttpStatusCode.BadRequest, capture.StatusCode);
        Assert.Equal(nameof(RoleWriteOutcome.ValidationFailed), await ErrorCodeAsync(capture));

        // A string that is no code at all — also refused at the controller.
        var bogus = await client.PutAsJsonAsync(
            $"{Route}/{id}/permissions", new { permissionCodes = new[] { "wizardry.cast" } });
        Assert.Equal(HttpStatusCode.BadRequest, bogus.StatusCode);

        // An omitted member is a 400, not "grant nothing".
        var omitted = await client.PutAsJsonAsync($"{Route}/{id}/permissions", new { });
        Assert.Equal(HttpStatusCode.BadRequest, omitted.StatusCode);
        Assert.Equal(nameof(RoleWriteOutcome.ValidationFailed), await ErrorCodeAsync(omitted));

        // An empty array is allowed — grant nothing.
        var emptied = await client.PutAsJsonAsync(
            $"{Route}/{id}/permissions", new { permissionCodes = Array.Empty<string>() });
        Assert.Equal(HttpStatusCode.OK, emptied.StatusCode);
    }

    [Fact]
    public async Task A_role_that_users_hold_cannot_be_deleted()
    {
        var schoolId = await ArrangeSchoolAsync();

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory, schoolId);

        var roleId = JsonDocument.Parse(
            await (await client.PostAsJsonAsync(Route, new { name = "Assigned Role" })).Content.ReadAsStringAsync())
            .RootElement.GetProperty("id").GetGuid();

        // Create a user and give them the custom role.
        var userId = JsonDocument.Parse(
            await (await client.PostAsJsonAsync("/api/v1/users", new
            {
                email = "holder@usa.edu.ph",
                fullName = "Holder",
                roleName = EamsRoleNames.Viewer,
                password = "correct-horse-battery-staple",
            })).Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();

        Assert.Equal(
            HttpStatusCode.OK,
            (await client.PutAsJsonAsync($"/api/v1/users/{userId}/roles", new { roleIds = new[] { roleId } })).StatusCode);

        var refused = await client.DeleteAsync($"{Route}/{roleId}");
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal(nameof(RoleWriteOutcome.InUse), await ErrorCodeAsync(refused));

        await using var read = NewDbContext();
        Assert.True(await read.Roles.AsNoTracking().AnyAsync(r => r.Id == roleId));
    }

    [Fact]
    public async Task The_permission_catalogue_lists_the_human_assignable_codes_only()
    {
        var schoolId = await ArrangeSchoolAsync();

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory, schoolId);

        var response = await client.GetAsync("/api/v1/permissions");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var codes = body.RootElement.EnumerateArray().Select(c => c.GetString()!).ToList();

        Assert.DoesNotContain(EamsPermissions.AttendanceCapture, codes);
        Assert.Contains(EamsPermissions.EventsRead, codes);
        Assert.Contains(EamsPermissions.RolesWrite, codes);
        // The whole registry except the one device code.
        Assert.Equal(EamsPermissions.All.Count - 1, codes.Count);
    }

    [Fact]
    public async Task An_unknown_id_is_a_404_on_every_verb()
    {
        var schoolId = await ArrangeSchoolAsync();
        var missing = Guid.NewGuid();

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory, schoolId);

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"{Route}/{missing}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"{Route}/{missing}")).StatusCode);
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await client.PutAsJsonAsync($"{Route}/{missing}", new { name = "X" })).StatusCode);
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await client.PutAsJsonAsync($"{Route}/{missing}/permissions", new { permissionCodes = Array.Empty<string>() })).StatusCode);
    }
}
