using System.Net;
using System.Text.Json;
using EAMS.Infrastructure.Data;
using EAMS.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EAMS.Tests.Integration;

/// <summary>
/// The seeded development <c>Term</c>.
///
/// <para>
/// <b>A term is the one row the import flow cannot start without</b>, which is why this file exists.
/// The §10 importer takes a <c>TermId</c> as an <em>input</em> (ADR-001 D-5), so without a term the
/// admin SPA's roster-import page renders an empty picker and refuses to stage a batch
/// (<c>StudentsImport.tsx</c>, <c>NO_TERMS</c>) — the first thing a new developer tries is dead, and the
/// failure looks like a broken import page rather than like a missing row.
/// </para>
///
/// <para>
/// <b>D-53 gave the product a way to create one, and that did not make the seed redundant.</b>
/// <c>ITermAdminService</c> behind <c>POST /academic/terms</c> means a freshly migrated database is
/// recoverable by hand — which is the right answer for a real install, and is what
/// <c>docs/DEPLOY-IIS.md</c> now tells an operator to do. It is the wrong answer for a developer whose
/// first five minutes on the repo should not include authoring a term to make the page they were
/// looking at work. The seed keeps that path free; these tests keep the seed.
/// </para>
///
/// <para>
/// <b>The regression these tests exist to catch is deletion, not malfunction.</b> The seed is
/// Development-only, so nobody running the suite in CI, and nobody on a machine whose <c>EAMS</c>
/// database already carries a term, would notice its removal until the next new machine was set up —
/// weeks later, by someone with no reason to connect it to the edit that caused it.
/// </para>
///
/// <para>
/// <b>On <see cref="DevelopmentApiFactory"/>'s "do not assert on row counts".</b> That constraint is
/// about a test's <em>own</em> arrangement being polluted by seeded rows it did not write. Here the
/// seed is the subject, there is no arrangement, and every assertion below is scoped — by term code,
/// or by school id and <c>IsCurrent</c> — rather than being a claim about the cardinality of a table.
/// </para>
/// </summary>
[Collection(DatabaseCollection.Name)]
public class DevelopmentSeedTermTests : IntegrationTest
{
    // Not published as constants on SeedData, unlike the code — asserted as literals so that changing
    // either half of the seeded term is a decision someone has to make here as well as there.
    private const string SchoolYear = "2025-2026";
    private const string Semester = "1st Semester";

    public DevelopmentSeedTermTests(SqlServerFixture sql) : base(sql) { }

    /// <summary>
    /// Boots the Development host and returns once it has migrated and seeded.
    ///
    /// <para>
    /// The client is what forces it: <c>WebApplicationFactory</c> builds the host lazily, so reading
    /// the database before <c>CreateClient</c> would look at the empty database
    /// <see cref="IntegrationTest.InitializeAsync"/> just left behind and fail for a reason that has
    /// nothing to do with the seed.
    /// </para>
    /// </summary>
    private static void Boot(DevelopmentApiFactory factory) => factory.CreateClient().Dispose();

    /// <summary>
    /// The row is there, and it carries the values the SPA renders in the picker.
    ///
    /// <para>
    /// <c>SingleOrDefaultAsync</c> on the code rather than <c>FirstOrDefaultAsync</c>:
    /// <c>UX_Terms_SchoolId_Code</c> makes a duplicate impossible per school, so two rows here would
    /// mean the seed had started writing terms for a second school — worth failing on rather than
    /// silently taking one. The <c>OrDefault</c> half is what buys the explanatory message below when
    /// the row is simply absent, which is the regression this test exists for.
    /// </para>
    ///
    /// <para>
    /// <c>StartsOn</c>/<c>EndsOn</c> are asserted <em>null</em> deliberately. The SIS export has no
    /// term-date columns, so a real term has none either, and
    /// <c>AcademicReferenceService.ListTermsAsync</c> orders on <c>Code</c> precisely because of it.
    /// A seed that grew dates would be the only term in the system carrying them, and would make the
    /// ordering path this fixture exercises the one path real data never takes.
    /// </para>
    /// </summary>
    [Fact]
    public async Task The_development_host_seeds_a_term()
    {
        using var factory = new DevelopmentApiFactory(Sql.ConnectionString);
        Boot(factory);

        await using var read = NewDbContext();

        var term = await read.Terms.AsNoTracking()
            .SingleOrDefaultAsync(t => t.Code == SeedData.DevelopmentTermCode);

        Assert.True(
            term is not null,
            $"The Development host seeded no term with code '{SeedData.DevelopmentTermCode}'. The " +
            "importer takes a TermId as input, so without this row a freshly migrated database gives " +
            "the roster-import page an empty term picker and it refuses to stage a batch " +
            "(StudentsImport.tsx, NO_TERMS). A term can be authored on the /terms page (D-53), but a " +
            "developer should not have to do that before the import page works — that is what the seed " +
            "is for, and its removal is what this test exists to catch.");

        Assert.Equal(SchoolYear, term!.SchoolYear);
        Assert.Equal(Semester, term.Semester);
        Assert.True(term.IsCurrent, "The seeded term is not flagged current.");
        Assert.Null(term.StartsOn);
        Assert.Null(term.EndsOn);
    }

    /// <summary>
    /// The term belongs to the <em>same</em> school as the rest of the seed.
    ///
    /// <para>
    /// A term filed against some other school id would satisfy the test above and still be useless:
    /// every academic read is scoped by the <c>SchoolId</c> query filter, so the picker would be empty
    /// on exactly the tenant the rest of the seeded world lives in. The seeded kiosk is the anchor
    /// because its name is already a published constant — no second copy of the school code.
    /// </para>
    ///
    /// <para>
    /// And <b>exactly one current term for that school</b>: <c>UX_Terms_SchoolId_Current</c> is a
    /// filtered unique index, so a second current term is a duplicate-key error at seed time rather
    /// than a silently arbitrary answer from <c>GET /academic/terms/current</c> — this pins that the
    /// seed stays on the right side of it.
    /// </para>
    /// </summary>
    [Fact]
    public async Task The_seeded_term_is_the_seeded_school_s_one_current_term()
    {
        using var factory = new DevelopmentApiFactory(Sql.ConnectionString);
        Boot(factory);

        await using var read = NewDbContext();

        var schoolId = await read.Devices.AsNoTracking()
            .Where(d => d.Name == SeedData.DevelopmentKioskName)
            .Select(d => d.SchoolId)
            .SingleAsync();

        var current = await read.Terms.AsNoTracking()
            .Where(t => t.SchoolId == schoolId && t.IsCurrent)
            .Select(t => t.Code)
            .ToListAsync();

        Assert.True(
            current is [var only] && only == SeedData.DevelopmentTermCode,
            $"The seeded school has {current.Count} current term(s) [{string.Join(", ", current)}]; " +
            $"expected exactly one, '{SeedData.DevelopmentTermCode}'. Zero means the roster-import " +
            "page's term picker is empty on a fresh machine; more than one means the seed is one " +
            "SaveChanges away from a UX_Terms_SchoolId_Current duplicate-key error.");
    }

    /// <summary>
    /// The same fact stated the way the SPA actually observes it: <c>GET /academic/terms</c> — the
    /// call behind the import page's picker — returns the seeded term on a freshly migrated
    /// Development host.
    ///
    /// <para>
    /// It is not redundant with the two above. They would both pass on a build where the row existed
    /// but the read filtered it out of the response — a tenancy pin, a paging default, an ordering
    /// change — and "the row is in the table" is not the property the developer on the new machine
    /// cares about. What they care about is that the picker has something in it.
    /// </para>
    /// </summary>
    [Fact]
    public async Task The_seeded_term_is_visible_on_the_route_the_import_page_reads()
    {
        using var factory = new DevelopmentApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/v1/academic/terms");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var items = body.RootElement.GetProperty("items");

        var seeded = items.EnumerateArray().SingleOrDefault(
            t => t.GetProperty("code").GetString() == SeedData.DevelopmentTermCode);

        Assert.True(
            seeded.ValueKind == JsonValueKind.Object,
            $"GET /academic/terms on a freshly seeded Development host did not publish " +
            $"'{SeedData.DevelopmentTermCode}'. This is the exact call behind the roster-import " +
            "page's term picker, and an empty picker is a page that refuses to stage a batch.");

        Assert.True(seeded.GetProperty("isCurrent").GetBoolean());
        Assert.Equal(SchoolYear, seeded.GetProperty("schoolYear").GetString());
        Assert.Equal(Semester, seeded.GetProperty("semester").GetString());
    }
}
