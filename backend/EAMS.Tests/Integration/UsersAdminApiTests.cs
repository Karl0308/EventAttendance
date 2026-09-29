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
/// §11's User Management surface over real HTTP — create, edit, activate/deactivate, and role assignment.
/// HTTP tests rather than service tests for the reason <c>ClassificationAdminApiTests</c> records:
/// everything the module promises is a statement about the wire (a duplicate is a 409 not a 500, a
/// self-deactivation is a 409, every failure carries a <c>code</c>), and the self-lockout guards are only
/// meaningful against a real authenticated principal.
///
/// <para>
/// The host is <see cref="EamsApiFactory"/> (Production, no dev seed), so the RBAC reference data is
/// present (it seeds in every environment) but no school or user is — every school, and the signed-in
/// administrator, is arranged by the test.
/// </para>
/// </summary>
[Collection(DatabaseCollection.Name)]
public class UsersAdminApiTests : IntegrationTest
{
    public UsersAdminApiTests(SqlServerFixture sql) : base(sql) { }

    private const string Route = "/api/v1/users";

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
        return body.RootElement.GetProperty(UsersController.ErrorCodeProperty).GetString();
    }

    private async Task<Guid> RoleIdAsync(string name)
    {
        await using var db = NewDbContext();
        return await db.Roles.AsNoTracking().Where(r => r.Name == name).Select(r => r.Id).SingleAsync();
    }

    private static object NewUserBody(string email, string role = EamsRoleNames.Viewer) => new
    {
        email,
        fullName = "Test Person",
        phone = "0917-000-0000",
        roleName = role,
        password = "correct-horse-battery-staple",
    };

    [Fact]
    public async Task An_administrator_can_create_a_user_read_it_back_and_list_it()
    {
        var schoolId = await ArrangeSchoolAsync();

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory, schoolId);

        var created = await client.PostAsJsonAsync(Route, NewUserBody("newbie@usa.edu.ph"));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        using var createdBody = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var id = createdBody.RootElement.GetProperty("id").GetGuid();
        Assert.Equal("newbie@usa.edu.ph", createdBody.RootElement.GetProperty("email").GetString());
        Assert.True(createdBody.RootElement.GetProperty("isActive").GetBoolean());
        Assert.Equal(
            EamsRoleNames.Viewer,
            createdBody.RootElement.GetProperty("roles")[0].GetProperty("name").GetString());

        Assert.NotNull(created.Headers.Location);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(created.Headers.Location)).StatusCode);

        // The phone set at creation was persisted through the follow-up write.
        using var read = JsonDocument.Parse(
            await (await client.GetAsync($"{Route}/{id}")).Content.ReadAsStringAsync());
        Assert.Equal("0917-000-0000", read.RootElement.GetProperty("phone").GetString());

        var listed = await client.GetAsync(Route);
        using var listBody = JsonDocument.Parse(await listed.Content.ReadAsStringAsync());
        Assert.Contains(
            listBody.RootElement.GetProperty("items").EnumerateArray(),
            u => u.GetProperty("email").GetString() == "newbie@usa.edu.ph");
    }

    [Fact]
    public async Task A_duplicate_email_is_a_conflict()
    {
        var schoolId = await ArrangeSchoolAsync();

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory, schoolId);

        Assert.Equal(
            HttpStatusCode.Created,
            (await client.PostAsJsonAsync(Route, NewUserBody("dup@usa.edu.ph"))).StatusCode);

        var clash = await client.PostAsJsonAsync(Route, NewUserBody("dup@usa.edu.ph"));
        Assert.Equal(HttpStatusCode.Conflict, clash.StatusCode);
        Assert.Equal(nameof(UserAdminOutcome.EmailInUse), await ErrorCodeAsync(clash));
    }

    [Fact]
    public async Task A_short_password_is_a_validation_failure()
    {
        var schoolId = await ArrangeSchoolAsync();

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory, schoolId);

        var response = await client.PostAsJsonAsync(Route, new
        {
            email = "shortpw@usa.edu.ph",
            fullName = "Test Person",
            roleName = EamsRoleNames.Viewer,
            password = "short",
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(nameof(UserAdminOutcome.ValidationFailed), await ErrorCodeAsync(response));
    }

    [Fact]
    public async Task An_unknown_role_is_a_conflict()
    {
        var schoolId = await ArrangeSchoolAsync();

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory, schoolId);

        var response = await client.PostAsJsonAsync(Route, NewUserBody("x@usa.edu.ph", role: "Wizard"));
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(nameof(UserAdminOutcome.UnknownRole), await ErrorCodeAsync(response));
    }

    [Fact]
    public async Task A_user_can_be_edited_deactivated_and_reactivated()
    {
        var schoolId = await ArrangeSchoolAsync();

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory, schoolId);

        var created = await client.PostAsJsonAsync(Route, NewUserBody("editme@usa.edu.ph"));
        using var body = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var id = body.RootElement.GetProperty("id").GetGuid();

        var updated = await client.PutAsJsonAsync(
            $"{Route}/{id}", new { fullName = "Edited Name", phone = "0999-111-2222" });
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        using var updatedBody = JsonDocument.Parse(await updated.Content.ReadAsStringAsync());
        Assert.Equal("Edited Name", updatedBody.RootElement.GetProperty("fullName").GetString());

        var deactivated = await client.PatchAsJsonAsync($"{Route}/{id}/active", new { isActive = false });
        Assert.Equal(HttpStatusCode.OK, deactivated.StatusCode);
        using var deBody = JsonDocument.Parse(await deactivated.Content.ReadAsStringAsync());
        Assert.False(deBody.RootElement.GetProperty("isActive").GetBoolean());

        var reactivated = await client.PatchAsJsonAsync($"{Route}/{id}/active", new { isActive = true });
        Assert.Equal(HttpStatusCode.OK, reactivated.StatusCode);
    }

    [Fact]
    public async Task An_omitted_isActive_is_refused()
    {
        var schoolId = await ArrangeSchoolAsync();

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory, schoolId);

        var created = await client.PostAsJsonAsync(Route, NewUserBody("z@usa.edu.ph"));
        using var body = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var id = body.RootElement.GetProperty("id").GetGuid();

        var response = await client.PatchAsJsonAsync($"{Route}/{id}/active", new { });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(nameof(UserAdminOutcome.ValidationFailed), await ErrorCodeAsync(response));
    }

    [Fact]
    public async Task An_administrator_cannot_deactivate_their_own_account()
    {
        var schoolId = await ArrangeSchoolAsync();

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory, schoolId);

        // Find the signed-in administrator in the list — SignedInClientAsync created and signed in one
        // SchoolAdmin operator, and every user is listed.
        using var listBody = JsonDocument.Parse(
            await (await client.GetAsync(Route)).Content.ReadAsStringAsync());
        var self = listBody.RootElement.GetProperty("items").EnumerateArray()
            .Single(u => u.GetProperty("email").GetString()!.StartsWith("operator-", StringComparison.Ordinal));
        var selfId = self.GetProperty("id").GetGuid();

        var response = await client.PatchAsJsonAsync($"{Route}/{selfId}/active", new { isActive = false });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(nameof(UserAdminOutcome.SelfLockout), await ErrorCodeAsync(response));

        // And they are still active.
        await using var db = NewDbContext();
        Assert.True((await db.Users.AsNoTracking().SingleAsync(u => u.Id == selfId)).IsActive);
    }

    [Fact]
    public async Task Roles_can_be_replaced_on_another_user()
    {
        var schoolId = await ArrangeSchoolAsync();

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory, schoolId);

        // Read role ids after the host has booted and seeded the RBAC reference data.
        var organizerId = await RoleIdAsync(EamsRoleNames.Organizer);
        var viewerId = await RoleIdAsync(EamsRoleNames.Viewer);

        var created = await client.PostAsJsonAsync(
            Route, NewUserBody("roles@usa.edu.ph", role: EamsRoleNames.Viewer));
        using var body = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var id = body.RootElement.GetProperty("id").GetGuid();

        var set = await client.PutAsJsonAsync(
            $"{Route}/{id}/roles", new { roleIds = new[] { organizerId, viewerId } });
        Assert.Equal(HttpStatusCode.OK, set.StatusCode);

        using var setBody = JsonDocument.Parse(await set.Content.ReadAsStringAsync());
        var names = setBody.RootElement.GetProperty("roles").EnumerateArray()
            .Select(r => r.GetProperty("name").GetString()).OrderBy(n => n).ToList();
        Assert.Equal(new[] { EamsRoleNames.Organizer, EamsRoleNames.Viewer }.OrderBy(n => n), names);

        // An empty set is allowed and leaves the user access-less.
        var cleared = await client.PutAsJsonAsync($"{Route}/{id}/roles", new { roleIds = Array.Empty<Guid>() });
        Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);
        using var clearedBody = JsonDocument.Parse(await cleared.Content.ReadAsStringAsync());
        Assert.Empty(clearedBody.RootElement.GetProperty("roles").EnumerateArray());
    }

    [Fact]
    public async Task An_unknown_role_id_on_set_roles_is_refused_and_changes_nothing()
    {
        var schoolId = await ArrangeSchoolAsync();

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory, schoolId);

        var created = await client.PostAsJsonAsync(Route, NewUserBody("k@usa.edu.ph"));
        using var body = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var id = body.RootElement.GetProperty("id").GetGuid();

        var response = await client.PutAsJsonAsync(
            $"{Route}/{id}/roles", new { roleIds = new[] { Guid.NewGuid() } });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(nameof(UserAdminOutcome.UnknownRole), await ErrorCodeAsync(response));
    }

    [Fact]
    public async Task Set_roles_with_no_roleIds_member_is_refused()
    {
        var schoolId = await ArrangeSchoolAsync();

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory, schoolId);

        var created = await client.PostAsJsonAsync(Route, NewUserBody("m@usa.edu.ph"));
        using var body = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var id = body.RootElement.GetProperty("id").GetGuid();

        var response = await client.PutAsJsonAsync($"{Route}/{id}/roles", new { });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(nameof(UserAdminOutcome.ValidationFailed), await ErrorCodeAsync(response));
    }

    [Fact]
    public async Task An_administrator_cannot_change_their_own_roles()
    {
        var schoolId = await ArrangeSchoolAsync();

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory, schoolId);

        var viewerId = await RoleIdAsync(EamsRoleNames.Viewer);

        using var listBody = JsonDocument.Parse(
            await (await client.GetAsync(Route)).Content.ReadAsStringAsync());
        var selfId = listBody.RootElement.GetProperty("items").EnumerateArray()
            .Single(u => u.GetProperty("email").GetString()!.StartsWith("operator-", StringComparison.Ordinal))
            .GetProperty("id").GetGuid();

        var response = await client.PutAsJsonAsync(
            $"{Route}/{selfId}/roles", new { roleIds = new[] { viewerId } });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(nameof(UserAdminOutcome.SelfLockout), await ErrorCodeAsync(response));
    }

    [Fact]
    public async Task An_unknown_id_is_a_404_on_every_verb()
    {
        var schoolId = await ArrangeSchoolAsync();
        var missing = Guid.NewGuid();

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory, schoolId);

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"{Route}/{missing}")).StatusCode);
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await client.PutAsJsonAsync($"{Route}/{missing}", new { fullName = "X", phone = (string?)null })).StatusCode);
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await client.PatchAsJsonAsync($"{Route}/{missing}/active", new { isActive = false })).StatusCode);
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await client.PutAsJsonAsync($"{Route}/{missing}/roles", new { roleIds = Array.Empty<Guid>() })).StatusCode);
    }

    [Fact]
    public async Task The_role_list_publishes_the_four_seeded_roles_with_their_codes()
    {
        var schoolId = await ArrangeSchoolAsync();

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory, schoolId);

        var response = await client.GetAsync("/api/v1/roles");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var names = body.RootElement.EnumerateArray()
            .Select(r => r.GetProperty("name").GetString()).OrderBy(n => n).ToList();

        Assert.Equal(EamsRoleNames.All.OrderBy(n => n), names);

        // Every seeded role is a system role and carries its grants.
        foreach (var role in body.RootElement.EnumerateArray())
        {
            Assert.True(role.GetProperty("isSystem").GetBoolean());
            Assert.NotEmpty(role.GetProperty("permissionCodes").EnumerateArray());
        }
    }

    [Fact]
    public async Task A_user_from_another_school_is_not_visible()
    {
        var mySchool = await ArrangeSchoolAsync("USA");
        var otherSchool = await ArrangeSchoolAsync("XYZ");

        // A user in the other school, created directly.
        Guid otherUserId = await CreateUserAsync(otherSchool, "other@xyz.edu.ph", "correct-horse-battery-staple");

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory, mySchool);

        // Not in my list, and a by-id read 404s — the tenant query filter bounds both.
        using var listBody = JsonDocument.Parse(
            await (await client.GetAsync(Route)).Content.ReadAsStringAsync());
        Assert.DoesNotContain(
            listBody.RootElement.GetProperty("items").EnumerateArray(),
            u => u.GetProperty("email").GetString() == "other@xyz.edu.ph");

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"{Route}/{otherUserId}")).StatusCode);
    }
}
