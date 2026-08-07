using System.Net;
using System.Text.Json;
using EAMS.Application.Dtos;
using EAMS.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EAMS.Tests.Integration;

/// <summary>
/// Paging on the admin list reads — Technical Plan §6.2 ("Paged list") and §6.3 ("Paged"), which the
/// API declared and did not do until Phase 3b-3.
///
/// <para>
/// <see cref="Unit.PageRequestTests"/> owns the clamp arithmetic, which needs no database. This file
/// owns the two things only a real SQL Server can answer: <b>whether consecutive pages actually
/// partition the result set</b>, and whether the total describes the same filter the rows came from.
/// </para>
///
/// <para>
/// <b>The overlapping-page failure is the reason this file exists.</b> An unordered — or partially
/// ordered — <c>OFFSET/FETCH</c> query is not an error. SQL Server returns rows, every page looks
/// plausible, and the only symptom is that a student appears on page 1 and again on page 2 while
/// somebody else appears on neither. Nothing in a single-page test can see it, and nothing in the
/// database complains. It is found by walking every page and comparing the union against the table,
/// which is what these tests do.
/// </para>
/// </summary>
[Collection(DatabaseCollection.Name)]
public class PaginationTests : IntegrationTest
{
    public PaginationTests(SqlServerFixture sql) : base(sql) { }

    /// <summary>
    /// <b>Every student shares a surname, and that is the fixture's entire job.</b>
    ///
    /// <para>
    /// The list orders by <c>LastName</c>. Give each row a distinct one and the sort key is already
    /// unique, the tiebreaker is never consulted, and a test that removed it would still pass — the
    /// green-but-useless shape this repo has been bitten by before. With every key identical, the
    /// tiebreaker is the <em>only</em> thing imposing an order, so deleting <c>ThenBy(Id)</c> makes
    /// the page-partition assertion fail rather than merely become lucky.
    /// </para>
    /// </summary>
    private async Task<Guid> ArrangeTiedStudentsAsync(int count)
    {
        await using var db = NewDbContext();

        var school = TestData.NewSchool();
        db.Schools.Add(school);

        for (var i = 0; i < count; i++)
        {
            db.Students.Add(TestData.NewStudent(
                school.Id, $"2026-{i:D4}", firstName: "Maria", lastName: "Santos"));
        }

        await db.SaveChangesAsync();
        return school.Id;
    }

    // ---------------------------------------------------------------- pages partition the result

    /// <summary>
    /// Walk every page at a size that does not divide the total, and assert the union is the table
    /// exactly once over — no row served twice, none missed.
    /// </summary>
    [Fact]
    public async Task Consecutive_student_pages_partition_the_roster_with_no_row_repeated_or_lost()
    {
        const int total = 25;
        const int pageSize = 7;

        await ArrangeTiedStudentsAsync(total);
        await using var db = NewDbContext();
        var students = StudentsOn(db);

        var seen = new List<Guid>();

        for (var page = Paging.FirstPage; ; page++)
        {
            var result = await students.ListAsync(
                null, null, null, PageRequest.From(page, pageSize));

            Assert.Equal(total, result.Total);
            seen.AddRange(result.Items.Select(s => s.Id));

            if (!result.HasMore) break;

            // A page that claims more must have served a full one, or the walk below would loop.
            Assert.Equal(pageSize, result.Items.Count);
        }

        Assert.Equal(total, seen.Count);
        Assert.Equal(total, seen.Distinct().Count());

        var stored = await db.Students.AsNoTracking().Select(s => s.Id).ToListAsync();
        Assert.Equal(stored.Order(), seen.Order());
    }

    /// <summary>
    /// The observable half of the guarantee: <b>page 1 and page 2 share no row</b>, on a fixture where
    /// every sort key is tied.
    ///
    /// <para>
    /// <b>What this test does not do is negative-control the tiebreaker, and saying so is the point.</b>
    /// Removing <c>ThenBy(s =&gt; s.Id)</c> from <c>StudentService.ListAsync</c> leaves it green: SQL
    /// Server is <em>permitted</em> to return tied rows in a different order per execution, and on this
    /// schema and data volume it simply does not. That is exactly what makes the bug dangerous — it is
    /// latent behind a plan choice, so it surfaces when the table grows, the statistics change, or the
    /// query goes parallel, and never in a test run. The structural guarantee is pinned by
    /// <see cref="Every_paged_list_query_orders_by_a_unique_column"/>, which asserts the ORDER BY
    /// itself and does fail when the tiebreaker is removed.
    /// </para>
    ///
    /// <para>
    /// It is kept because it is the property a caller actually depends on, and because it would catch a
    /// <c>Skip</c>/<c>Take</c> arithmetic error — an off-by-one in <see cref="PageRequest.Skip"/>
    /// re-serves or drops a row on every boundary, and the ORDER BY test cannot see that.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Adjacent_student_pages_never_share_a_row_even_when_every_sort_key_is_tied()
    {
        await ArrangeTiedStudentsAsync(count: 20);
        await using var db = NewDbContext();
        var students = StudentsOn(db);

        var first = await students.ListAsync(null, null, null, PageRequest.From(1, 10));
        var second = await students.ListAsync(null, null, null, PageRequest.From(2, 10));

        Assert.Equal(10, first.Items.Count);
        Assert.Equal(10, second.Items.Count);

        var overlap = first.Items.Select(s => s.Id)
            .Intersect(second.Items.Select(s => s.Id))
            .ToList();

        Assert.True(
            overlap.Count == 0,
            $"{overlap.Count} row(s) appeared on both page 1 and page 2 of GET /students. Every " +
            "student in this fixture shares a surname, so LastName alone is not a total order — the " +
            "ThenBy(Id) tiebreaker is what makes OFFSET/FETCH partition the set.");
    }

    // ------------------------------------------------------------------ the ordering, structurally

    /// <summary>Captures the SQL EF actually sent, which is the only place the ORDER BY is visible.</summary>
    private sealed class CapturedSql : Microsoft.EntityFrameworkCore.Diagnostics.DbCommandInterceptor
    {
        public List<string> Statements { get; } = [];

        public override ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader>>
            ReaderExecutingAsync(
                System.Data.Common.DbCommand command,
                Microsoft.EntityFrameworkCore.Diagnostics.CommandEventData eventData,
                Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader> result,
                CancellationToken cancellationToken = default)
        {
            Statements.Add(command.CommandText);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    /// <summary>
    /// <b>Every paged read ends its <c>ORDER BY</c> on a unique column, asserted against the SQL that
    /// was sent.</b>
    ///
    /// <para>
    /// This is the deterministic guard, and it exists because the behavioural one is not. An
    /// <c>OFFSET/FETCH</c> over a non-total order is not an error and does not misbehave on demand —
    /// SQL Server may return tied rows in any order it likes, and on a small table it reliably picks
    /// the same one, so a test that pages a fixture and compares rows passes against a query that has
    /// no total order at all. The ORDER BY clause is the thing that is actually true or false, so that
    /// is what gets asserted.
    /// </para>
    ///
    /// <para>
    /// All nine paged reads in one test on purpose. The failure being guarded is somebody adding the
    /// tenth list, or tidying a tiebreaker away as redundant on the one list whose natural key looks
    /// unique — so the assertion has to be over the set rather than per endpoint, or the next list is
    /// added without one and nothing says so.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Every_paged_list_query_orders_by_a_unique_column()
    {
        await using (var arrange = NewDbContext())
        {
            arrange.Schools.Add(TestData.NewSchool());
            await arrange.SaveChangesAsync();
        }

        var captured = new CapturedSql();

        await using var db = new EAMS.Infrastructure.Data.EamsDbContext(
            new DbContextOptionsBuilder<EAMS.Infrastructure.Data.EamsDbContext>()
                .UseSqlServer(Sql.ConnectionString)
                .AddInterceptors(captured)
                .Options,
            School);

        var academic = AcademicOn(db);
        var page = PageRequest.From(2, 10);

        // Table is the row each read pages — the one whose primary key has to end the ORDER BY.
        var reads = new (string Name, string Table, Func<Task> Run)[]
        {
            ("GET /students", "Students",
                () => StudentsOn(db).ListAsync(null, null, null, page)),
            ("GET /events", "Events",
                () => EventsOn(db).ListAsync(null, page)),
            ("GET /attendance", "AttendanceRecords",
                () => AttendanceOn(db).ListAsync(null, null, null, page)),
            ("GET /academic/terms", "Terms",
                () => academic.ListTermsAsync(page)),
            ("GET /academic/colleges", "Colleges",
                () => academic.ListCollegesAsync(page)),
            ("GET /academic/programs", "Programs",
                () => academic.ListProgramsAsync(null, page)),
            ("GET /academic/courses", "Courses",
                () => academic.ListCoursesAsync(null, null, page)),
            // An explicit term, so the current-term default cannot short-circuit this to the empty
            // page before a query is ever built.
            ("GET /academic/course-offerings", "CourseOfferings",
                () => academic.ListCourseOfferingsAsync(Guid.NewGuid(), null, null, page)),
            ("GET /student-groups", "StudentGroups",
                () => StudentGroupsOn(db).ListAsync(null, null, null, null, page)),
        };

        foreach (var read in reads)
        {
            captured.Statements.Clear();
            await read.Run();

            var paged = captured.Statements
                .Where(sql => sql.Contains("OFFSET", StringComparison.OrdinalIgnoreCase))
                .ToList();

            Assert.True(
                paged.Count > 0,
                $"{read.Name} issued no OFFSET/FETCH query, so it is not paging in the database. " +
                "Paging in memory defeats the whole point — the rows still all cross the wire.");

            foreach (var sql in paged)
            {
                // Every OFFSET in the statement, each matched to the ORDER BY it belongs to — the one
                // immediately before it. An Include over a collection nests the paged read in a
                // subquery and re-states the sort on the outer select, so "the last ORDER BY" is the
                // outer one and sits *after* the OFFSET; matching backwards from each OFFSET is what
                // reads the clause that actually governs the page.
                var offsets = new List<int>();
                for (var at = sql.IndexOf("OFFSET", StringComparison.OrdinalIgnoreCase);
                     at >= 0;
                     at = sql.IndexOf("OFFSET", at + 1, StringComparison.OrdinalIgnoreCase))
                {
                    offsets.Add(at);
                }

                foreach (var offset in offsets)
                {
                    var orderBy = sql.LastIndexOf(
                        "ORDER BY", offset, StringComparison.OrdinalIgnoreCase);

                    Assert.True(
                        orderBy >= 0,
                        $"{read.Name} pages with OFFSET and no ORDER BY governing it:\n{sql}");

                    var clause = sql[(orderBy + "ORDER BY".Length)..offset];

                    var (rootTable, rootAlias) = GoverningFrom(sql, orderBy);

                    Assert.True(
                        rootAlias is not null,
                        $"{read.Name}: could not find the FROM governing this ORDER BY:\n{sql}");

                    // The scope resolution is itself checked against what the read is known to page,
                    // so a parser that silently drifts onto the wrong FROM shows up here as a named
                    // mismatch rather than as a confusing alias failure below.
                    //
                    // An unnamed source is a FAILURE here, not a pass. GoverningFrom returns a null
                    // table when the governing FROM is a derived table rather than a named one, and no
                    // shape in these nine reads reaches that today. Tolerating it would mean that the
                    // first shape that did — a Distinct().Skip().Take(), a SelectMany pushdown, EF
                    // changing its Include strategy on an upgrade — would silently retire this
                    // cross-check and leave the terminal-term assertion comparing against the derived
                    // table's alias, whose [Id] need not be the paged row's key. This guard has already
                    // produced a false negative and two false positives; a vacuous pass is the one
                    // failure mode it has not yet had, and the one nobody would notice.
                    Assert.True(
                        rootTable == read.Table,
                        $"{read.Name} pages [{rootTable}] and not [{read.Table}]:\n{sql}");

                    // The LAST ordering term, not "any term anywhere in the clause".
                    //
                    // A substring search for ".[Id]" across the whole clause is satisfied by
                    // "ORDER BY [c].[Code], [c].[Id]" on course-offerings — where [c] is the joined
                    // Course, so Course.Id is one value shared by every section of that course and the
                    // order is still not total. That is not a contrived slip: it is exactly what
                    // .ThenBy(o => o.Course.Id) compiles to, and it is most tempting precisely when the
                    // natural sort key is already a joined column.
                    var terminal = clause.Split(',')[^1].Trim();

                    Assert.True(
                        terminal.StartsWith($"{rootAlias}.[Id]", StringComparison.Ordinal),
                        $"{read.Name} pages on an order whose last term is not the row's own key:\n" +
                        $"  ORDER BY{clause}\n" +
                        $"  root alias: {rootAlias}, last term: {terminal}\n" +
                        "The final ordering term has to be the primary key of the row being paged. A " +
                        "key from a joined table is not unique per row — every section of one course " +
                        "shares its Course.Id — so ties remain, and SQL Server resolves ties per " +
                        "execution: consecutive pages then repeat one row and skip another, silently. " +
                        "This will not show up as a failing behavioural test, because tie order is " +
                        "usually stable on a small table. That is why the assertion is on the SQL.");
                }
            }
        }
    }

    /// <summary>
    /// The <c>FROM</c> that belongs to the same query scope as the <c>ORDER BY</c> at
    /// <paramref name="orderBy"/> — the row that clause is actually ordering — as
    /// <c>(table, alias)</c>. <c>Table</c> is null when the source is a derived table rather than a
    /// named one.
    ///
    /// <para>
    /// <b>Scope is found by paren depth, because both cheaper heuristics produced a false positive on
    /// a correct query — and a guard that cries wolf gets deleted by whoever next sees it red, after
    /// which the real defect ships silently.</b> "Nearest <c>FROM</c> before the <c>ORDER BY</c>"
    /// lands inside whichever correlated subquery sits in the <c>SELECT</c> list — offerings carry an
    /// <c>EnrolledCount</c> one, groups a <c>MemberCount</c> one — and bound an alias belonging to
    /// something else. Looking the alias up by expected table name fared no better: it takes the first
    /// textual occurrence, which is not necessarily the one this scope binds.
    /// </para>
    ///
    /// <para>
    /// Walking backwards while counting <c>)</c> up and <c>(</c> down means every enclosed subquery is
    /// traversed at depth &gt; 0 and skipped whole, so the first <c>FROM</c> seen at depth 0 is this
    /// statement's own — the definition of the thing being paged, rather than a guess at it. It works
    /// unchanged on the nested shape a collection <c>Include</c> produces, where the <c>OFFSET</c>
    /// lives in an inner subquery with its own <c>FROM</c> and its own <c>ORDER BY</c>.
    /// </para>
    ///
    /// <para>
    /// <c>FROM</c> and never <c>JOIN</c>: the joined copies are exactly what must not satisfy the
    /// assertion, since <c>Course.Id</c> is shared by every section of a course.
    /// </para>
    /// </summary>
    private static (string? Table, string? Alias) GoverningFrom(string sql, int orderBy)
    {
        const string From = "FROM ";
        var depth = 0;

        for (var i = orderBy - 1; i >= 0; i--)
        {
            switch (sql[i])
            {
                case ')':
                    depth++;
                    continue;

                // A '(' at depth 0 is the paren that opened this scope: the ORDER BY's own query
                // starts here, so there is no FROM of ours further back.
                case '(' when depth == 0:
                    return (null, null);

                case '(':
                    depth--;
                    continue;
            }

            if (depth != 0) continue;

            if (i + From.Length > sql.Length ||
                string.Compare(sql, i, From, 0, From.Length, StringComparison.OrdinalIgnoreCase) != 0)
            {
                continue;
            }

            return ParseFrom(sql, i + From.Length, orderBy);
        }

        return (null, null);
    }

    /// <summary>
    /// Reads <c>[Table] AS [alias]</c> or <c>( … ) AS [alias]</c> starting at <paramref name="at"/>.
    /// The forward scan skips parenthesised groups whole, so a derived table's own inner
    /// <c>AS [x]</c> cannot be mistaken for the outer one.
    /// </summary>
    private static (string? Table, string? Alias) ParseFrom(string sql, int at, int limit)
    {
        string? table = null;

        if (sql[at] == '[')
        {
            var close = sql.IndexOf(']', at);
            if (close < 0) return (null, null);

            table = sql[(at + 1)..close];
            at = close + 1;
        }

        var depth = 0;

        for (var i = at; i < limit; i++)
        {
            switch (sql[i])
            {
                case '(':
                    depth++;
                    continue;

                case ')':
                    depth--;
                    continue;
            }

            if (depth != 0) continue;

            if (string.Compare(sql, i, " AS [", 0, " AS [".Length, StringComparison.OrdinalIgnoreCase) == 0)
            {
                var open = i + " AS ".Length;
                var close = sql.IndexOf(']', open);

                return close < 0 ? (table, null) : (table, sql[open..(close + 1)]);
            }
        }

        return (table, null);
    }

    /// <summary>
    /// The same failure on attendance, where the tie is not a coincidence of the data but the shape of
    /// the table: <c>ChangeStatusAsync</c>'s close materializes one <c>Absent</c> row per un-tapped
    /// invitee and every one of them has <c>CheckInAt</c> null — so the entire sort key of a closed
    /// event's absentee block is identical, at institution scale.
    /// </summary>
    [Fact]
    public async Task Adjacent_attendance_pages_never_share_a_row_when_every_check_in_is_null()
    {
        const int rows = 24;

        await using (var arrange = NewDbContext())
        {
            var school = TestData.NewSchool();
            arrange.Schools.Add(school);

            var @event = TestData.NewEvent(school.Id);
            arrange.Events.Add(@event);

            for (var i = 0; i < rows; i++)
            {
                var student = TestData.NewStudent(school.Id, $"2026-{i:D4}");
                arrange.Students.Add(student);
                arrange.AttendanceRecords.Add(new EAMS.Domain.AttendanceRecord
                {
                    SchoolId = school.Id,
                    EventId = @event.Id,
                    StudentId = student.Id,
                    // Null, exactly as the close path writes them.
                    CheckInAt = null,
                    Status = "Absent",
                    CaptureMethod = "Import",
                });
            }

            await arrange.SaveChangesAsync();
        }

        await using var db = NewDbContext();
        var attendance = AttendanceOn(db);

        var first = await attendance.ListAsync(null, null, null, PageRequest.From(1, 8));
        var second = await attendance.ListAsync(null, null, null, PageRequest.From(2, 8));
        var third = await attendance.ListAsync(null, null, null, PageRequest.From(3, 8));

        var all = first.Items.Concat(second.Items).Concat(third.Items).Select(a => a.Id).ToList();

        Assert.Equal(rows, all.Count);
        Assert.Equal(rows, all.Distinct().Count());
        Assert.Equal(rows, first.Total);
        Assert.False(third.HasMore);
    }

    // ------------------------------------------------------------------------------ bounds and cap

    /// <summary>
    /// A page past the end is served, empty, with the real total — not clamped back to the last page
    /// and not a 404. A client walking pages has to be able to stop.
    /// </summary>
    [Fact]
    public async Task A_page_past_the_end_is_empty_and_still_reports_the_total()
    {
        await ArrangeTiedStudentsAsync(count: 5);
        await using var db = NewDbContext();

        var result = await StudentsOn(db).ListAsync(null, null, null, PageRequest.From(900, 10));

        Assert.Empty(result.Items);
        Assert.Equal(5, result.Total);
        Assert.Equal(900, result.Page);
        Assert.False(result.HasMore);
    }

    /// <summary>
    /// <b>The cap, proved against real rows rather than against the clamp alone.</b>
    /// <see cref="Unit.PageRequestTests"/> already asserts <c>From</c> returns the maximum; this
    /// asserts the maximum is what reaches <c>TAKE</c>, which is the half that a service forgetting to
    /// use <c>page.PageSize</c> would break.
    /// </summary>
    [Fact]
    public async Task An_oversized_page_size_serves_the_cap_and_says_so()
    {
        var rows = Paging.MaxPageSize + 25;
        await ArrangeTiedStudentsAsync(rows);

        await using var db = NewDbContext();

        var result = await StudentsOn(db).ListAsync(
            null, null, null, PageRequest.From(1, 100_000));

        Assert.Equal(Paging.MaxPageSize, result.Items.Count);
        Assert.Equal(Paging.MaxPageSize, result.PageSize);
        Assert.Equal(rows, result.Total);
        Assert.True(result.HasMore);
    }

    /// <summary>
    /// <b>The total describes the filter, not the page.</b> Counting the page instead of the set is
    /// the mistake that makes a grid render "1 of 1" over a filtered roster of hundreds, and it is
    /// invisible until somebody notices the number is always the page size.
    /// </summary>
    [Fact]
    public async Task The_total_counts_the_filter_and_not_the_page()
    {
        await using (var arrange = NewDbContext())
        {
            var school = TestData.NewSchool();
            arrange.Schools.Add(school);

            for (var i = 0; i < 12; i++)
            {
                arrange.Students.Add(TestData.NewStudent(
                    school.Id, $"2026-A{i:D3}", lastName: "Santos", status: "Active"));
            }

            for (var i = 0; i < 4; i++)
            {
                arrange.Students.Add(TestData.NewStudent(
                    school.Id, $"2026-G{i:D3}", lastName: "Santos", status: "Graduated"));
            }

            await arrange.SaveChangesAsync();
        }

        await using var db = NewDbContext();

        var unfiltered = await StudentsOn(db).ListAsync(null, null, null, PageRequest.From(1, 5));
        Assert.Equal(16, unfiltered.Total);
        Assert.Equal(5, unfiltered.Items.Count);

        var filtered = await StudentsOn(db).ListAsync(null, null, "Graduated", PageRequest.From(1, 5));
        Assert.Equal(4, filtered.Total);
        Assert.Equal(4, filtered.Items.Count);
        Assert.False(filtered.HasMore);
    }

    // ------------------------------------------------------------------------------- over the wire

    /// <summary>
    /// The envelope as a client sees it, camelCased by the host rather than by the DTO — the same
    /// class of silent drift <c>StudentsApiTests</c> pins for §6.2's row shape.
    /// </summary>
    [Fact]
    public async Task The_students_list_publishes_the_paging_envelope_a_grid_binds()
    {
        await ArrangeTiedStudentsAsync(count: 12);

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/v1/students?page=2&pageSize=5");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = body.RootElement;

        Assert.Equal(JsonValueKind.Object, root.ValueKind);
        Assert.Equal(5, root.GetProperty("items").GetArrayLength());
        Assert.Equal(2, root.GetProperty("page").GetInt32());
        Assert.Equal(5, root.GetProperty("pageSize").GetInt32());
        Assert.Equal(12, root.GetProperty("total").GetInt32());
        Assert.True(root.GetProperty("hasMore").GetBoolean());
    }

    /// <summary>
    /// <b>An absurd page number is an empty 200, not a 500 — proved through the real pipeline against
    /// a real database, because the failure was SQL Server's rather than C#'s.</b>
    ///
    /// <para>
    /// The unit theory pins the arithmetic; this pins the consequence. An <c>int</c> multiply of
    /// <c>?page=42949674</c> by the default page size wraps to a negative <c>OFFSET</c>, SQL Server
    /// refuses it with Msg 10743, and <c>UseExceptionHandler</c> serves a 500 to an unauthenticated
    /// caller (ADR-001 D-6 leaves the admin surface open). Asserting the status here is what makes the
    /// two halves — saturating in C# and being accepted by the database — one guarantee rather than
    /// two hopes.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(42_949_674, Paging.DefaultPageSize)]
    [InlineData(10_737_420, Paging.MaxPageSize)]
    [InlineData(int.MaxValue, Paging.MaxPageSize)]
    public async Task An_absurd_page_number_over_http_is_an_empty_page_and_never_a_500(
        int page, int pageSize)
    {
        await ArrangeTiedStudentsAsync(count: 3);

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"/api/v1/students?page={page}&pageSize={pageSize}");

        Assert.True(
            response.StatusCode == HttpStatusCode.OK,
            $"?page={page}&pageSize={pageSize} answered {(int)response.StatusCode}. A 500 here is the " +
            "OFFSET overflow: PageRequest.Skip wrapped negative and SQL Server refused it (Msg " +
            "10743). Every one of the nine paged reads shares that arithmetic, and the surface is " +
            "open under ADR-001 D-6.");

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = body.RootElement;

        Assert.Empty(root.GetProperty("items").EnumerateArray());
        Assert.Equal(3, root.GetProperty("total").GetInt32());
        Assert.False(root.GetProperty("hasMore").GetBoolean());
    }

    /// <summary>
    /// An out-of-range page size arriving over the wire is clamped rather than refused, and the
    /// response reports what it actually did. <b>That echo is what makes clamping an acceptable answer
    /// instead of a silent one</b> — a caller asking for 100 000 rows and receiving 200 can see that
    /// it happened, without a 400 and a failure branch on a read that otherwise cannot fail.
    /// </summary>
    [Fact]
    public async Task An_out_of_range_page_size_over_http_is_clamped_and_reported_not_refused()
    {
        await ArrangeTiedStudentsAsync(count: 3);

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/v1/students?page=0&pageSize=100000");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = body.RootElement;

        Assert.Equal(Paging.FirstPage, root.GetProperty("page").GetInt32());
        Assert.Equal(Paging.MaxPageSize, root.GetProperty("pageSize").GetInt32());
        Assert.Equal(3, root.GetProperty("items").GetArrayLength());
    }

    /// <summary>
    /// <b><c>GET /attendance/live/{eventId}</c> is not paged, and this is the test that keeps it that
    /// way.</b>
    ///
    /// <para>
    /// It shares a controller with <c>GET /attendance</c>, which is paged — so "make the attendance
    /// endpoints consistent" is a tidy-up somebody will reach for. It would break a mobile client
    /// polling this API today: the endpoint's <c>since</c>/<c>cursor</c>/<c>hasMore</c> delta shape is
    /// frozen published contract under D-29, D-30 and D-42. Offset paging over a live feed is also
    /// wrong on its own terms — an insert below the offset shifts every later row up by one and the
    /// next page skips it, which is precisely why that endpoint was built on a <c>rowversion</c>
    /// cursor.
    /// </para>
    ///
    /// <para>
    /// Asserted as the <em>absence</em> of <c>items</c>/<c>page</c>/<c>pageSize</c>/<c>total</c> as
    /// well as the presence of the cursor fields, because wrapping the body would keep the cursor
    /// fields — one level down, where the client cannot see them.
    /// </para>
    /// </summary>
    [Fact]
    public async Task The_live_attendance_endpoint_is_not_wrapped_in_the_paging_envelope()
    {
        Guid eventId;

        await using (var arrange = NewDbContext())
        {
            var school = TestData.NewSchool();
            arrange.Schools.Add(school);

            var @event = TestData.NewLiveEvent(school.Id);
            arrange.Events.Add(@event);
            eventId = @event.Id;

            await arrange.SaveChangesAsync();
        }

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient();

        var response = await client.GetAsync($"/api/v1/attendance/live/{eventId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = body.RootElement;

        foreach (var frozen in new[] { "cursor", "hasMore", "pollAfterSeconds" })
        {
            Assert.True(
                root.TryGetProperty(frozen, out _),
                $"GET /attendance/live lost '{frozen}' from its top level. That shape is frozen " +
                "published contract (D-29/D-30/D-42) and a mobile client polls it today.");
        }

        foreach (var envelope in new[] { "items", "page", "pageSize", "total" })
        {
            Assert.False(
                root.TryGetProperty(envelope, out _),
                $"GET /attendance/live grew a '{envelope}' member. It has been wrapped in " +
                "PagedResult, which breaks the polling loop of the client building against this API " +
                "right now. It pages by cursor on purpose: offset paging over a live feed drops any " +
                "row inserted below the offset between two requests.");
        }
    }
}
