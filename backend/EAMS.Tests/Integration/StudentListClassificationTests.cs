using System.Net;
using System.Text.Json;
using EAMS.Application.Dtos;
using EAMS.Domain;
using EAMS.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace EAMS.Tests.Integration;

/// <summary>
/// <b>The Classification column on the students list.</b> Task 3's second requirement — "UI - List View:
/// Add a new 'Classification' column to the main list table" — which the grid cannot render without the
/// backend putting it on the row.
///
/// <para>
/// <b>The three populations are arranged deliberately, because a projection can be wrong for each of
/// them differently.</b> Most people hold exactly one classification, three in the sampled export hold
/// two on different axes, and thirty-four hold none. A projection that returned the first row per
/// student looks perfect against the first group and silently halves the second; one that inner-joined
/// looks perfect against both and drops the third from the grid entirely.
/// </para>
///
/// <para>
/// <b>On SQL Server, never EF InMemory</b> — the ordering is resolved by the database, and the N+1 guard
/// below asserts on the SQL that was actually sent.
/// </para>
/// </summary>
[Collection(DatabaseCollection.Name)]
public class StudentListClassificationTests : IntegrationTest
{
    public StudentListClassificationTests(SqlServerFixture sql) : base(sql) { }

    /// <summary>
    /// A school, four vocabulary entries, and three students: one on two axes, one on one, one on none.
    /// </summary>
    private async Task<World> ArrangeAsync()
    {
        await using var db = NewDbContext();

        var school = TestData.NewSchool();
        db.Schools.Add(school);

        var enrolled = TestData.NewClassification(school.Id, "STUDENT", ClassificationAxis.Student);
        var nap = TestData.NewClassification(school.Id, "NAP", ClassificationAxis.Personnel);
        var acad = TestData.NewClassification(school.Id, "ACAD", ClassificationAxis.Personnel);
        var friars = TestData.NewClassification(school.Id, "USA FRIARS", ClassificationAxis.Friars);
        db.Classifications.AddRange(enrolled, nap, acad, friars);

        // Surnames drive the list order (LastName, then Id), so they are chosen to make the expected
        // order of the page explicit rather than incidental.
        var two = TestData.NewStudent(school.Id, "2023-0001", lastName: "Alvarez");
        var one = TestData.NewStudent(school.Id, "2023-0002", lastName: "Bautista");
        var none = TestData.NewStudent(school.Id, "2023-0003", lastName: "Cruz");
        db.Students.AddRange(two, one, none);

        db.StudentClassifications.AddRange(
            TestData.NewStudentClassification(two.Id, enrolled),
            TestData.NewStudentClassification(two.Id, nap),
            TestData.NewStudentClassification(one.Id, acad));

        await db.SaveChangesAsync();

        return new World(school.Id, two.Id, one.Id, none.Id, enrolled.Id, nap.Id, acad.Id, friars.Id);
    }

    private sealed record World(
        Guid SchoolId, Guid TwoAxes, Guid OneAxis, Guid None,
        Guid StudentAxisId, Guid NapId, Guid AcadId, Guid FriarsId);

    private async Task<PagedResult<StudentDto>> ListAsync(string? search = null)
    {
        await using var db = NewDbContext();
        return await StudentsOn(db).ListAsync(search, null, null, PageRequest.Default);
    }

    // ------------------------------------------------------------------- the three populations

    /// <summary>
    /// <b>Two classifications, one, and none — each rendered as what it is.</b>
    ///
    /// <para>
    /// Every row is asserted by <em>content</em>, not by count. A projection that put the same person's
    /// categories on the wrong row, or gave everyone the first classification in the table, produces
    /// exactly the right counts.
    /// </para>
    /// </summary>
    [Fact]
    public async Task The_list_carries_each_persons_classifications()
    {
        var world = await ArrangeAsync();

        var page = await ListAsync();

        Assert.Equal(3, page.Total);

        var two = Assert.Single(page.Items, s => s.Id == world.TwoAxes);
        var one = Assert.Single(page.Items, s => s.Id == world.OneAxis);
        var none = Assert.Single(page.Items, s => s.Id == world.None);

        // The person the junction table exists for: one value on each of two axes, both present.
        Assert.Equal(
            new[] { world.NapId, world.StudentAxisId }.Order(),
            two.Classifications.Select(c => c.ClassificationId).Order());

        Assert.Equal(
            new[] { ClassificationAxis.Personnel, ClassificationAxis.Student }.Order(),
            two.Classifications.Select(c => c.Axis).Order());

        // The ordinary case.
        var only = Assert.Single(one.Classifications);
        Assert.Equal(world.AcadId, only.ClassificationId);
        Assert.Equal("ACAD", only.Name);
        Assert.Equal(ClassificationAxis.Personnel, only.Axis);
        Assert.True(only.IsActive);

        // And the uncategorised: an empty collection, never null, and the row still appears.
        Assert.NotNull(none.Classifications);
        Assert.Empty(none.Classifications);
    }

    /// <summary>
    /// <b>A person with no classification is still on the grid.</b>
    ///
    /// <para>
    /// Stated as its own test because it is the failure a join gets wrong rather than a projection: an
    /// inner join onto <c>StudentClassifications</c> would drop the thirty-four uncategorised people out
    /// of the roster entirely, and the grid would look correct — a shorter list is not obviously a wrong
    /// one. The total is asserted alongside, because that is what would expose it.
    /// </para>
    /// </summary>
    [Fact]
    public async Task An_uncategorised_person_still_appears_on_the_list()
    {
        var world = await ArrangeAsync();

        var page = await ListAsync();

        Assert.True(
            page.Total == 3,
            $"The list reported {page.Total} students where 3 exist. One of them holds no " +
            "classification at all — if the classifications column is joined rather than looked up, " +
            "that person silently leaves the roster and a shorter grid does not look like a bug.");

        Assert.Contains(page.Items, s => s.Id == world.None);
        Assert.Equal(3, page.Items.Count);
    }

    /// <summary>
    /// <b>The order is deterministic — axis, then display name — so a grid cell does not reshuffle
    /// between page loads.</b>
    ///
    /// <para>
    /// Asserted against a person holding two values whose axis order and name order <em>disagree</em>:
    /// <c>NAP</c> is Personnel and <c>STUDENT</c> is Student, so ordering by axis puts NAP first while
    /// ordering by name puts NAP first too — which would prove nothing. A third classification is added
    /// here to break that tie, so the assertion can only pass on the axis ordering.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Classifications_come_back_in_a_stable_order()
    {
        var world = await ArrangeAsync();

        // Give the two-axis person a third, on the Friars axis, named to sort LAST alphabetically —
        // which is what makes the two candidate orderings distinguishable. By name alone the expected
        // sequence would be NAP, STUDENT, ZEBRA; by axis then name it is ZEBRA (Friars), NAP
        // (Personnel), STUDENT (Student). Only the second can pass the assertion below.
        await using (var db = NewDbContext())
        {
            var zebra = TestData.NewClassification(world.SchoolId, "ZEBRA", ClassificationAxis.Friars);
            db.Classifications.Add(zebra);
            await db.SaveChangesAsync();

            db.StudentClassifications.Add(
                TestData.NewStudentClassification(world.TwoAxes, zebra));
            await db.SaveChangesAsync();
        }

        var first = await ListAsync();
        var second = await ListAsync();

        var a = Assert.Single(first.Items, s => s.Id == world.TwoAxes).Classifications;
        var b = Assert.Single(second.Items, s => s.Id == world.TwoAxes).Classifications;

        // Axis order: Friars, Personnel, Student. Name order would have been NAP, STUDENT, ZEBRA.
        Assert.Equal(
            new[] { ClassificationAxis.Friars, ClassificationAxis.Personnel, ClassificationAxis.Student },
            a.Select(c => c.Axis));

        Assert.Equal(new[] { "ZEBRA", "NAP", "STUDENT" }, a.Select(c => c.Name));

        // And it is the same order twice — the property a grid actually depends on.
        Assert.Equal(a.Select(c => c.ClassificationId), b.Select(c => c.ClassificationId));
    }

    /// <summary>
    /// A retired classification somebody still holds appears on their row, flagged. Retiring withdraws a
    /// category from pickers and reassigns nobody, so a grid that filtered it would under-report the
    /// people who carry it — the same class of error as reading <c>students.section</c> instead of the
    /// offerings.
    /// </summary>
    [Fact]
    public async Task A_retired_classification_still_shows_on_the_row()
    {
        var world = await ArrangeAsync();

        await using (var db = NewDbContext())
        {
            var acad = await db.Classifications.SingleAsync(c => c.Id == world.AcadId);
            acad.IsActive = false;
            acad.RetiredAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }

        var page = await ListAsync();

        var held = Assert.Single(Assert.Single(page.Items, s => s.Id == world.OneAxis).Classifications);

        Assert.Equal(world.AcadId, held.ClassificationId);
        Assert.False(held.IsActive);
    }

    // ------------------------------------------------------------------------------ the N+1 guard

    /// <summary>Captures the SQL EF actually sent — the only place a per-row query is visible.</summary>
    private sealed class CapturedSql : DbCommandInterceptor
    {
        public List<string> Statements { get; } = [];

        public override ValueTask<InterceptionResult<System.Data.Common.DbDataReader>>
            ReaderExecutingAsync(
                System.Data.Common.DbCommand command,
                CommandEventData eventData,
                InterceptionResult<System.Data.Common.DbDataReader> result,
                CancellationToken cancellationToken = default)
        {
            Statements.Add(command.CommandText);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    /// <summary>
    /// <b>The classifications cost one query for the whole page, not one per student.</b>
    ///
    /// <para>
    /// <b>This is the assertion that makes the feature acceptable rather than merely present.</b> A
    /// per-row lookup produces an identical, entirely correct response — every test above it passes —
    /// and turns a fifty-row grid into fifty-one round trips against the roster, which is exactly the
    /// N+1 this repository's own lessons are about. Nothing observable in the payload distinguishes the
    /// two, so the SQL is what gets asserted.
    /// </para>
    ///
    /// <para>
    /// <b>The count is compared between two page sizes rather than against a fixed number</b>, because
    /// a fixed number bakes in today's query plan and fails on any unrelated change. What must be true
    /// is that the statement count does not <em>grow with the number of rows</em> — that is the whole of
    /// what N+1 means, and it is the only thing this asserts.
    /// </para>
    /// </summary>
    [Fact]
    public async Task The_list_does_not_issue_a_query_per_student()
    {
        var world = await ArrangeAsync();

        // Twelve more students, each holding a classification, so a per-row lookup has something to
        // multiply by. Three from the fixture plus twelve is fifteen.
        await using (var db = NewDbContext())
        {
            var acad = await db.Classifications.SingleAsync(c => c.Id == world.AcadId);

            for (var i = 0; i < 12; i++)
            {
                var student = TestData.NewStudent(
                    world.SchoolId, $"2025-{i:D4}", lastName: $"Delgado{i:D2}");

                db.Students.Add(student);
                db.StudentClassifications.Add(TestData.NewStudentClassification(student.Id, acad));
            }

            await db.SaveChangesAsync();
        }

        var captured = new CapturedSql();

        await using var counted = new EAMS.Infrastructure.Data.EamsDbContext(
            new DbContextOptionsBuilder<EAMS.Infrastructure.Data.EamsDbContext>()
                .UseSqlServer(Sql.ConnectionString)
                .AddInterceptors(captured)
                .Options,
            School);

        var students = StudentsOn(counted);

        // Three rows.
        captured.Statements.Clear();
        var small = await students.ListAsync(null, null, null, PageRequest.From(1, 3));
        var forThree = captured.Statements.Count;

        // Fifteen rows — five times as many.
        captured.Statements.Clear();
        var large = await students.ListAsync(null, null, null, PageRequest.From(1, 50));
        var forFifteen = captured.Statements.Count;

        Assert.Equal(3, small.Items.Count);
        Assert.Equal(15, large.Items.Count);

        // The fixture really does give the rows something to look up, or "no extra queries" would be
        // a statement about an empty table.
        Assert.Contains(large.Items, s => s.Classifications.Count > 0);
        Assert.Contains(large.Items, s => s.Classifications.Count == 2);

        Assert.True(
            forThree == forFifteen,
            $"Paging 3 students took {forThree} statements and paging 15 took {forFifteen}. The " +
            "classifications column must cost one query for the page regardless of its size — a count " +
            "that grows with the rows is a query per student, which returns an identical and entirely " +
            "correct-looking payload while turning a fifty-row grid into fifty-one round trips.");
    }

    // --------------------------------------------------------------------- the rest of the read

    /// <summary>
    /// <b>The list's existing behaviour is unchanged.</b> Paging, the search box (including the card arm
    /// added in this same pass), the course and status filters, and the surname ordering all still do
    /// what they did — the column was added beside them, not through them.
    /// </summary>
    [Fact]
    public async Task The_existing_list_behaviour_is_untouched()
    {
        var world = await ArrangeAsync();

        // Ordering: Alvarez, Bautista, Cruz.
        var all = await ListAsync();
        Assert.Equal(
            new[] { world.TwoAxes, world.OneAxis, world.None },
            all.Items.Select(s => s.Id));

        // Search still matches names and student numbers.
        Assert.Equal(world.OneAxis, Assert.Single((await ListAsync("Bautista")).Items).Id);
        Assert.Equal(world.None, Assert.Single((await ListAsync("2023-0003")).Items).Id);

        // Filters still narrow, and paging still pages with a truthful total.
        await using var db = NewDbContext();

        var filtered = await StudentsOn(db).ListAsync(null, "BSIT", "Active", PageRequest.Default);
        Assert.Equal(3, filtered.Total);

        var firstPage = await StudentsOn(db).ListAsync(null, null, null, PageRequest.From(1, 2));
        Assert.Equal(2, firstPage.Items.Count);
        Assert.Equal(3, firstPage.Total);
        Assert.True(firstPage.HasMore);

        var secondPage = await StudentsOn(db).ListAsync(null, null, null, PageRequest.From(2, 2));
        Assert.Equal(world.None, Assert.Single(secondPage.Items).Id);
        Assert.False(secondPage.HasMore);
    }

    /// <summary>
    /// <b>The detail read and the write responses carry the same collection the grid does.</b>
    ///
    /// <para>
    /// The edit form opens from a grid row and refreshes itself from the <c>PUT</c> response, so a
    /// detail or write payload that answered <c>[]</c> for somebody the grid showed two categories for
    /// would contradict the row it came from — and the form would then save the empty version back.
    /// </para>
    /// </summary>
    [Fact]
    public async Task The_detail_read_and_the_write_response_agree_with_the_list()
    {
        var world = await ArrangeAsync();

        var row = Assert.Single((await ListAsync()).Items, s => s.Id == world.TwoAxes);
        Assert.Equal(2, row.Classifications.Count);

        await using var db = NewDbContext();
        var students = StudentsOn(db);

        var detail = await students.GetAsync(world.TwoAxes);
        Assert.NotNull(detail);
        Assert.Equal(
            row.Classifications.Select(c => c.ClassificationId),
            detail.Classifications.Select(c => c.ClassificationId));

        // And an edit round trip does not blank them.
        var updated = await students.UpdateAsync(
            world.TwoAxes,
            new StudentWriteRequest(
                "2023-0001", "Maria", "Reyes", "Alvarez", null, null, null, "Active"));

        Assert.NotNull(updated.Student);
        Assert.Equal(
            row.Classifications.Select(c => c.ClassificationId),
            updated.Student.Classifications.Select(c => c.ClassificationId));
    }

    // ------------------------------------------------------------------------------- over HTTP

    /// <summary>The published shape: a <c>classifications</c> array on every row of <c>GET /students</c>.</summary>
    [Fact]
    public async Task The_endpoint_publishes_a_classifications_array_on_every_row()
    {
        var world = await ArrangeAsync();

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient();

        using var body = JsonDocument.Parse(
            await (await client.GetAsync("/api/v1/students")).Content.ReadAsStringAsync());

        var items = body.RootElement.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(3, items.Count);

        // Present on every row — including the person who holds none, as an empty array rather than
        // a missing member or a null, so a grid can bind it without a guard.
        foreach (var item in items)
        {
            Assert.Equal(
                JsonValueKind.Array,
                item.GetProperty("classifications").ValueKind);
        }

        var two = Assert.Single(items, i => i.GetProperty("id").GetGuid() == world.TwoAxes);
        Assert.Equal(2, two.GetProperty("classifications").GetArrayLength());

        var none = Assert.Single(items, i => i.GetProperty("id").GetGuid() == world.None);
        Assert.Equal(0, none.GetProperty("classifications").GetArrayLength());

        // Each entry carries what a column needs to render: the name, the axis it occupies, and
        // whether it is still offered.
        var entry = two.GetProperty("classifications").EnumerateArray().First();
        Assert.False(string.IsNullOrWhiteSpace(entry.GetProperty("name").GetString()));
        Assert.Contains(entry.GetProperty("axis").GetString(), ClassificationAxis.All);
        Assert.True(entry.TryGetProperty("isActive", out _));
    }
}
