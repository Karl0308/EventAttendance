using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using EAMS.Api.Controllers;
using EAMS.Application.Abstractions;
using EAMS.Application.Dtos;
using EAMS.Domain;
using EAMS.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EAMS.Tests.Integration;

/// <summary>
/// The ADR-001 D-2 tripwire, attacked rather than demonstrated.
///
/// <para>
/// <see cref="StudentWriteTests"/> proves the tripwire fires on the four payloads it was designed
/// for — <c>course</c>, <c>yearLevel</c>, <c>section</c>, and one Pascal spelling — supplied
/// <em>directly</em> as a pre-built <c>UnmappedFields</c> dictionary. That is the mechanism. This file
/// is about the boundary the mechanism actually sits on: <see cref="JsonExtensionData"/> populated by
/// <c>System.Text.Json</c> from bytes a browser sent, where a member's casing, its null-ness, its
/// nesting and its co-occurrence with a validation error are all decided by the deserializer rather
/// than by the test.
/// </para>
///
/// <para>
/// <b>The failure this exists to catch is one-directional and silent.</b> A tripwire that fires too
/// often produces a visible 400 an operator complains about within the hour. A tripwire with a hole in
/// it produces a 200 on a request that wrote no section, and the client believes it did — which is the
/// entire scenario ADR-001 D-2 and this contract were written for. So every hole hypothesis below is
/// posed at the HTTP boundary, in the shape a real client produces it.
/// </para>
/// </summary>
[Collection(DatabaseCollection.Name)]
public class StudentDerivedFieldTripwireTests : IntegrationTest
{
    public StudentDerivedFieldTripwireTests(SqlServerFixture sql) : base(sql) { }

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

    /// <summary>
    /// Raw JSON rather than an anonymous object, because several of these payloads cannot be expressed
    /// as one: a C# property cannot be named <c>COURSE</c> and <c>course</c> in the same type, and an
    /// anonymous object's null is indistinguishable from an omitted member once the serializer has
    /// applied its own ignore policy. The bytes are the contract, so the bytes are what is sent.
    /// </summary>
    private static StringContent Json(string body) =>
        new(body, Encoding.UTF8, "application/json");

    private static string ErrorCode(string responseBody)
    {
        using var document = JsonDocument.Parse(responseBody);
        return document.RootElement.GetProperty(StudentsController.ErrorCodeProperty).GetString() ?? "";
    }

    private static IDictionary<string, JsonElement> Sent(params (string Name, object? Value)[] members) =>
        members.ToDictionary(m => m.Name, m => JsonSerializer.SerializeToElement(m.Value));

    private static StudentWriteRequest Request(
        string studentNumber = "2023-0001",
        string firstName = "Maria",
        string lastName = "Santos",
        IDictionary<string, JsonElement>? unmapped = null) =>
        new(studentNumber, firstName, null, lastName, null, null, null, null)
        {
            UnmappedFields = unmapped,
        };

    // ------------------------------------------------------------------------ the round trip

    /// <summary>
    /// <b>The payload the whole contract was built for, sent verbatim for the first time.</b>
    ///
    /// <para>
    /// <c>StudentsApiTests.A_student_detail_round_trips_through_a_put_without_losing_a_field</c> echoes a
    /// student back through a PUT, but it hand-picks the eight modelled members while doing so — which
    /// is exactly what a careful client does and exactly what a careless one does not. This sends the
    /// GET response back <em>unmodified</em>, which is the one-line implementation every SPA reaches for
    /// first, and asserts that all three cache columns are named in the refusal rather than only the one
    /// that happened to be checked first.
    /// </para>
    ///
    /// <para>
    /// Naming all three matters operationally: a message that reports them one at a time turns a single
    /// fix into three round trips, and the third one arrives after the developer has concluded the API
    /// is flaky.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_student_detail_echoed_back_unmodified_is_refused_naming_every_derived_column()
    {
        var schoolId = await ArrangeSchoolAsync();
        var studentId = await ArrangeStudentAsync(schoolId);

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient();

        var read = await client.GetStringAsync($"{Route}/{studentId}");

        // Not reshaped, not filtered: the bytes the GET produced are the bytes the PUT sends.
        var response = await client.PutAsync($"{Route}/{studentId}", Json(read));
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(nameof(StudentWriteOutcome.FieldIsDerived), ErrorCode(body));

        using var problem = JsonDocument.Parse(body);
        var detail = problem.RootElement.GetProperty("detail").GetString()!;

        Assert.Contains("Students.Course", detail, StringComparison.Ordinal);
        Assert.Contains("Students.YearLevel", detail, StringComparison.Ordinal);
        Assert.Contains("Students.Section", detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// The same round trip once the derived members are stripped — the fix the message above tells the
    /// caller to make — must actually succeed. A refusal that cannot be satisfied by following its own
    /// instructions is worse than no refusal, and nothing else in the suite proves the echo is
    /// recoverable rather than merely refused.
    /// </summary>
    [Fact]
    public async Task Stripping_the_three_named_members_makes_the_same_round_trip_succeed()
    {
        var schoolId = await ArrangeSchoolAsync();
        var studentId = await ArrangeStudentAsync(schoolId);

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient();

        using var read = JsonDocument.Parse(await client.GetStringAsync($"{Route}/{studentId}"));

        var stripped = new Dictionary<string, JsonElement>();
        foreach (var member in read.RootElement.EnumerateObject())
        {
            if (Student.DerivedAcademicPropertyNames.Any(
                    name => string.Equals(name, member.Name, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            stripped[member.Name] = member.Value;
        }

        var response = await client.PutAsync(
            $"{Route}/{studentId}", Json(JsonSerializer.Serialize(stripped)));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ------------------------------------------------------------------------------ casing

    /// <summary>
    /// Every casing a client can produce, over the wire.
    ///
    /// <para>
    /// The check is <c>OrdinalIgnoreCase</c> against <c>Student.DerivedAcademicPropertyNames</c>, and
    /// the reason that is not obviously enough is that two different case rules meet here: MVC's
    /// serializer matches <em>modelled</em> members case-insensitively, so <c>"Status"</c> binds to
    /// <c>Status</c> and never reaches the extension data at all, while an unmodelled member reaches it
    /// under whatever casing it was written in. A rule that agreed with the wire format alone
    /// (<c>camelCase</c>) or with the property names alone (<c>PascalCase</c>) would leave the other
    /// spelling silently accepted — and <c>"YEARLEVEL"</c> from a screaming-snake code generator is not
    /// a hypothetical shape.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("course")]
    [InlineData("Course")]
    [InlineData("COURSE")]
    [InlineData("cOuRsE")]
    [InlineData("yearLevel")]
    [InlineData("yearlevel")]
    [InlineData("YEARLEVEL")]
    [InlineData("YearLevel")]
    [InlineData("section")]
    [InlineData("SECTION")]
    [InlineData("Section")]
    public async Task Every_casing_of_a_derived_member_is_refused_over_http(string member)
    {
        await ArrangeSchoolAsync();

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient();

        var response = await client.PostAsync(Route, Json(
            $$"""
            {"studentNumber":"2023-0500","firstName":"Juan","lastName":"Dela Cruz","{{member}}":"BSFS 2-A"}
            """));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            nameof(StudentWriteOutcome.FieldIsDerived),
            ErrorCode(await response.Content.ReadAsStringAsync()));

        await using var read = NewDbContext();
        Assert.Empty(await read.Students.AsNoTracking().ToListAsync());
    }

    // ------------------------------------------------------------------- null and empty values

    /// <summary>
    /// <b><c>"course": null</c> is refused, and that is the deliberate answer rather than an accident of
    /// the dictionary.</b>
    ///
    /// <para>
    /// It is worth pinning because the opposite is defensible and would be easy to "fix" into place: a
    /// null writes nothing, so refusing it looks like pedantry. It is not. The three columns are null on
    /// every student until a roster import populates them, so <c>"course": null</c> is precisely what an
    /// SPA echoing a freshly created student sends — the most common shape of the exact request the
    /// tripwire exists to catch. Accepting it would mean the tripwire fires only for students who
    /// already have an enrollment, which is the population least likely to be edited by hand.
    /// </para>
    ///
    /// <para>
    /// The cost is real and is the operational consequence to know: <b>a client cannot echo a
    /// GET response back at a PUT even when it changes nothing about the derived columns.</b> Stripping
    /// the three members is mandatory, not merely advisable, and the refusal message says so.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_null_derived_member_is_refused_even_though_it_would_write_nothing()
    {
        await ArrangeSchoolAsync();

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient();

        var response = await client.PostAsync(Route, Json(
            """
            {"studentNumber":"2023-0501","firstName":"Juan","lastName":"Dela Cruz","course":null}
            """));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            nameof(StudentWriteOutcome.FieldIsDerived),
            ErrorCode(await response.Content.ReadAsStringAsync()));
    }

    /// <summary>
    /// The empty string, for the same reason and with one more: it is the shape an HTML form produces
    /// for a text input the user cleared, so it arrives from a different code path than the null does.
    /// </summary>
    [Fact]
    public async Task An_empty_string_derived_member_is_refused()
    {
        await ArrangeSchoolAsync();

        await using var db = NewDbContext();
        var response = await StudentsOn(db).CreateAsync(Request(unmapped: Sent(("section", ""))));

        Assert.Equal(StudentWriteOutcome.FieldIsDerived, response.Outcome);
    }

    /// <summary>
    /// A non-string value is refused on the same terms. The check is over member <em>names</em>, so the
    /// value's JSON type must be irrelevant — and a client that models <c>yearLevel</c> as an integer is
    /// exactly the client most likely to be writing its own payload rather than echoing ours.
    /// </summary>
    [Fact]
    public async Task A_non_string_derived_member_is_refused()
    {
        await ArrangeSchoolAsync();

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient();

        var response = await client.PostAsync(Route, Json(
            """
            {"studentNumber":"2023-0502","firstName":"Juan","lastName":"Dela Cruz","yearLevel":2}
            """));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            nameof(StudentWriteOutcome.FieldIsDerived),
            ErrorCode(await response.Content.ReadAsStringAsync()));
    }

    // ------------------------------------------------------------------------------ nesting

    /// <summary>
    /// <b>A derived name nested inside an unmodelled object is ignored, and that is correct.</b>
    ///
    /// <para>
    /// This is the hypothesis worth stating explicitly because it looks like a hole: the tripwire reads
    /// only top-level extension-data keys, so <c>{"academic":{"course":"BSCRIM"}}</c> passes. It is not a
    /// hole, because the refusal exists to stop a caller <em>believing a write happened</em>, and nothing
    /// in this system has ever read a nested member — there is no contract under which that payload
    /// could have written anything, so there is no false belief to correct.
    /// </para>
    ///
    /// <para>
    /// Pinned rather than left implicit so the reasoning survives: broadening the check to a recursive
    /// scan would start refusing arbitrary client-side metadata that has nothing to do with §4.3, which
    /// is the failure mode <c>Unknown_members_that_are_not_derived_are_still_ignored</c> guards from the
    /// other side.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_derived_name_nested_inside_an_unmodelled_object_is_ignored()
    {
        await ArrangeSchoolAsync();

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient();

        var response = await client.PostAsync(Route, Json(
            """
            {"studentNumber":"2023-0503","firstName":"Juan","lastName":"Dela Cruz",
             "academic":{"course":"BSCRIM","section":"BSFS 2-A"}}
            """));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        await using var read = NewDbContext();
        var stored = await read.Students.AsNoTracking().SingleAsync();

        // The point of the assertion is not the 201 — it is that nothing leaked into the columns.
        Assert.Null(stored.Course);
        Assert.Null(stored.Section);
    }

    // --------------------------------------------------------------------------- interaction

    /// <summary>
    /// A payload that is both derived-bearing <em>and</em> invalid reports the derived refusal.
    ///
    /// <para>
    /// The order is decided by <c>Reject</c> (<c>DerivedFieldsSupplied ?? Validate</c>) and it is the
    /// right way round: a validation error is fixed by editing a value, and the caller then re-sends a
    /// request that is <em>still</em> carrying the derived member — so reporting validation first hands
    /// them a second 400 for a different reason and makes the API look like it is refusing at random.
    /// The structural problem is named first.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_derived_member_is_reported_ahead_of_a_field_that_is_also_invalid()
    {
        await ArrangeSchoolAsync();

        await using var db = NewDbContext();
        var response = await StudentsOn(db).CreateAsync(
            Request(firstName: "   ", unmapped: Sent(("section", "BSFS 2-A"))));

        Assert.Equal(StudentWriteOutcome.FieldIsDerived, response.Outcome);
    }

    /// <summary>
    /// A refused create leaves no row. <c>StudentWriteTests.A_refused_derived_field_writes_nothing</c>
    /// covers the update half — where the risk is a partially applied entity — and this covers the
    /// create half, where the risk is different: a row that exists with a valid-looking student number
    /// now holds it against <c>UNIQUE(SchoolId, StudentNumber)</c>, so the caller's corrected re-send
    /// collides with their own failed attempt.
    /// </summary>
    [Fact]
    public async Task A_refused_create_leaves_no_row_holding_the_student_number()
    {
        await ArrangeSchoolAsync();

        await using (var db = NewDbContext())
        {
            var refused = await StudentsOn(db).CreateAsync(
                Request(unmapped: Sent(("course", "BSCRIM"))));

            Assert.Equal(StudentWriteOutcome.FieldIsDerived, refused.Outcome);
        }

        await using var retry = NewDbContext();
        var response = await StudentsOn(retry).CreateAsync(Request());

        Assert.Equal(StudentWriteOutcome.Saved, response.Outcome);
    }

    /// <summary>
    /// <c>DELETE /students/{id}</c> carries no body and therefore no tripwire — worth one assertion
    /// because <c>DeleteAsync</c> is the one write path that does not call <c>Reject</c>, so a reader
    /// checking that every write is guarded finds an exception and needs to know it is deliberate rather
    /// than missed. There is nothing for a caller to supply.
    /// </summary>
    [Fact]
    public async Task Deleting_a_student_leaves_the_derived_cache_exactly_as_it_was()
    {
        var schoolId = await ArrangeSchoolAsync();
        var studentId = await ArrangeStudentAsync(schoolId);

        await using (var db = NewDbContext())
            Assert.Equal(StudentWriteOutcome.Saved, (await StudentsOn(db).DeleteAsync(studentId)).Outcome);

        await using var read = NewDbContext();
        var stored = await read.Students.AsNoTracking().SingleAsync(s => s.Id == studentId);

        Assert.Equal("BSIT", stored.Course);
        Assert.Equal("3rd Year", stored.YearLevel);
        Assert.Equal("A", stored.Section);
    }
}
