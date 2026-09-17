using System.Net;
using System.Text.Json;
using EAMS.Application.Abstractions;
using EAMS.Domain;
using EAMS.Infrastructure.Data;
using EAMS.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EAMS.Tests.Integration;

/// <summary>
/// The seeded classification vocabulary, and specifically <b>the seed-path trap</b>.
///
/// <para>
/// <b>The failure this file exists to catch is invisible to a clean-slate test, which is why the
/// arrangement matters more than the assertions.</b> <c>SeedData.InitializeAsync</c> returns early once
/// a <c>School</c> row exists, so anything written below that return reaches only a database nobody
/// has. This project has already shipped that bug once — the <c>Terms</c> block was added after every
/// dev database already carried a school, so those machines carried a school, students, events and a
/// device and <em>zero terms</em>, and the roster-import page was dead on all of them while a freshly
/// dropped database looked perfect.
/// </para>
///
/// <para>
/// <b>So the subject of every test below is a database that ALREADY HAS A SCHOOL.</b> A test that
/// booted the host against an empty database would pass with the seed written in the wrong place, and
/// would therefore prove nothing at all. That arrangement is the test.
/// </para>
///
/// <para>
/// <b>On <see cref="DevelopmentApiFactory"/>'s "do not assert on row counts".</b> That constraint is
/// about a test's own arrangement being polluted by seeded rows it did not write. Here the seed is the
/// subject, and every assertion is scoped to the school the test created.
/// </para>
/// </summary>
[Collection(DatabaseCollection.Name)]
public class DevelopmentSeedClassificationTests : IntegrationTest
{
    public DevelopmentSeedClassificationTests(SqlServerFixture sql) : base(sql) { }

    /// <summary>
    /// Boots the Development host and returns once it has migrated and seeded.
    ///
    /// <para>
    /// The client is what forces it: <c>WebApplicationFactory</c> builds the host lazily, so reading
    /// the database before <c>CreateClient</c> would look at whatever
    /// <see cref="IntegrationTest.InitializeAsync"/> left behind.
    /// </para>
    /// </summary>
    private static void Boot(DevelopmentApiFactory factory) => factory.CreateClient().Dispose();

    /// <summary>
    /// <b>A database exactly as a carried-over dev machine or the VM has it: a school, a term, and
    /// nothing this phase added.</b>
    ///
    /// <para>
    /// The term is written as well as the school, deliberately. Without it
    /// <c>SeedData.SeedTermAsync</c> would fire on this boot, and a reader could believe the
    /// classifications arrived on the back of the term seed rather than on their own guard. With both
    /// rows present, every pre-existing seed step is a no-op and the classifications are the only thing
    /// the boot can write.
    /// </para>
    /// </summary>
    private async Task<Guid> ArrangeCarriedOverDatabaseAsync()
    {
        await using var db = NewDbContext();

        var school = TestData.NewSchool();
        db.Schools.Add(school);
        db.Terms.Add(TestData.NewTerm(school.Id));
        await db.SaveChangesAsync();

        return school.Id;
    }

    private async Task<List<string>> NamesAsync(Guid schoolId)
    {
        await using var read = NewDbContext();

        return await read.Classifications.AsNoTracking()
            .Where(c => c.SchoolId == schoolId)
            .OrderBy(c => c.Name)
            .Select(c => c.Name)
            .ToListAsync();
    }

    /// <summary>
    /// <b>THE test.</b> A database that already has a school gets all eight classifications.
    ///
    /// <para>
    /// Written as a set comparison rather than a count, so a failure says which values are missing
    /// instead of saying "expected 8, got 0" — and so that a seed which wrote eight rows of the wrong
    /// spellings fails here rather than passing and failing later against the importer.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_database_that_already_has_a_school_still_gets_every_classification()
    {
        var schoolId = await ArrangeCarriedOverDatabaseAsync();

        using var factory = new DevelopmentApiFactory(Sql.ConnectionString);
        Boot(factory);

        var names = await NamesAsync(schoolId);

        Assert.True(
            ClassificationSeedValues.Names.Order(StringComparer.Ordinal)
                .SequenceEqual(names.Order(StringComparer.Ordinal)),
            $"A database that already carried a School row received [{string.Join(", ", names)}] " +
            $"instead of the eight seeded classifications. This is the seed-path trap: " +
            "SeedData.InitializeAsync returns early once a School exists, so a classification seed " +
            "placed below that return reaches only a freshly dropped database — which is the one " +
            "database no developer and no deployed VM actually has. The Terms seed was written in " +
            "exactly the wrong place once already, and the symptom was an import page that was dead " +
            "on every carried-over machine.");
    }

    /// <summary>
    /// <b>The slash survives the round trip through startup and into the database.</b>
    ///
    /// <para>
    /// Asserted against the stored row and its key together, because the two halves fail differently:
    /// a display name that lost its slash is a sanitisation nobody asked for, and a key that kept it
    /// is an index that will not recognise <c>Supervisory / Managerial</c> as the same category.
    /// </para>
    /// </summary>
    [Fact]
    public async Task The_value_with_a_slash_is_seeded_verbatim_with_a_folded_key()
    {
        var schoolId = await ArrangeCarriedOverDatabaseAsync();

        using var factory = new DevelopmentApiFactory(Sql.ConnectionString);
        Boot(factory);

        await using var read = NewDbContext();

        var row = await read.Classifications.AsNoTracking()
            .SingleOrDefaultAsync(c => c.SchoolId == schoolId && c.Name == "SUPERVISORY/MANAGERIAL");

        Assert.True(
            row is not null,
            "No classification was seeded with the literal name 'SUPERVISORY/MANAGERIAL'. It is the " +
            "one seeded value carrying punctuation, so a seed that sanitised names on the way in " +
            "would fail here and nowhere else.");

        Assert.Equal("SUPERVISORYMANAGERIAL", row!.NameKey);
        Assert.True(row.IsActive);
        Assert.Null(row.RetiredAt);
        Assert.Null(row.MergedIntoClassificationId);
    }

    /// <summary>
    /// <b>Booting twice does not produce sixteen rows.</b>
    ///
    /// <para>
    /// The guard is per value rather than "does this school have any classifications", which is the
    /// difference from <c>SeedTermAsync</c> and is what makes a later addition to the list reach an
    /// installation that already ran the earlier one. Per-value guards are also the ones that duplicate
    /// when they are written wrongly, so the second boot is asserted rather than assumed.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Booting_twice_seeds_each_classification_once()
    {
        var schoolId = await ArrangeCarriedOverDatabaseAsync();

        using (var first = new DevelopmentApiFactory(Sql.ConnectionString)) Boot(first);
        using (var second = new DevelopmentApiFactory(Sql.ConnectionString)) Boot(second);

        var names = await NamesAsync(schoolId);

        Assert.True(
            names.Count == ClassificationSeedValues.Names.Count,
            $"Two boots left {names.Count} classifications where eight were expected: " +
            $"[{string.Join(", ", names)}]. The seed guard is per value and keyed on NameKey; a guard " +
            "that compared display names, or that checked only whether the table was empty, produces " +
            "exactly this.");
    }

    /// <summary>
    /// <b>A restart does not undo an administrator's edits — which is the whole feature.</b>
    ///
    /// <para>
    /// This phase ships an <em>editable</em> list. A seed that reasserted its opening state on every
    /// start would silently revert a rename on every deployment, and the operator would have no way to
    /// tell that from their edit never having saved. Both halves are asserted: the rename survives,
    /// <b>and</b> no row is helpfully re-added under the original spelling — the second is what a
    /// name-keyed guard buys over a display-name-keyed one, since the key is unchanged by a
    /// punctuation edit.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_renamed_classification_survives_the_next_boot_and_is_not_re_added()
    {
        var schoolId = await ArrangeCarriedOverDatabaseAsync();

        using (var first = new DevelopmentApiFactory(Sql.ConnectionString)) Boot(first);

        await using (var edit = NewDbContext())
        {
            var row = await edit.Classifications
                .SingleAsync(c => c.SchoolId == schoolId && c.Name == "SUPERVISORY/MANAGERIAL");

            // A punctuation-and-case edit: the display name moves, the key does not. That is the case
            // a display-name-keyed guard gets wrong, by re-adding the original spelling beside it.
            row.Name = "Supervisory / Managerial";
            await edit.SaveChangesAsync();
        }

        using (var second = new DevelopmentApiFactory(Sql.ConnectionString)) Boot(second);

        var names = await NamesAsync(schoolId);

        Assert.Contains("Supervisory / Managerial", names);
        Assert.DoesNotContain("SUPERVISORY/MANAGERIAL", names);
        Assert.Equal(ClassificationSeedValues.Names.Count, names.Count);
    }

    /// <summary>
    /// <b>A retired classification stays retired across a restart.</b>
    ///
    /// <para>
    /// The same rule as the rename, stated on the flag, because reasserting it is the more tempting
    /// mistake: a seed that "made sure" its eight values were active would quietly re-offer a category
    /// the institution deliberately withdrew, and the next person filed under it would be a data
    /// problem nobody could trace to a deployment.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_retired_classification_stays_retired_across_a_boot()
    {
        var schoolId = await ArrangeCarriedOverDatabaseAsync();

        using (var first = new DevelopmentApiFactory(Sql.ConnectionString)) Boot(first);

        await using (var edit = NewDbContext())
        {
            var row = await edit.Classifications
                .SingleAsync(c => c.SchoolId == schoolId && c.Name == "CFI");

            row.IsActive = false;
            row.RetiredAt = DateTime.UtcNow;
            await edit.SaveChangesAsync();
        }

        using (var second = new DevelopmentApiFactory(Sql.ConnectionString)) Boot(second);

        await using var read = NewDbContext();

        var cfi = await read.Classifications.AsNoTracking()
            .SingleAsync(c => c.SchoolId == schoolId && c.Name == "CFI");

        Assert.False(
            cfi.IsActive,
            "A restart reactivated a classification an administrator had retired. The seed's job is " +
            "to make sure the vocabulary exists, not to keep enforcing its opening state against the " +
            "operator — that would make the one feature this phase ships silently revert on every " +
            "deployment.");
    }

    /// <summary>
    /// The same fact stated the way a client observes it: <c>GET /classifications</c> on a freshly
    /// booted Development host publishes all eight.
    ///
    /// <para>
    /// Not redundant with the reads above. They would all pass on a build where the rows existed but
    /// the read filtered them out — a tenancy pin, a paging default, an <c>includeRetired</c> default
    /// pointing the wrong way — and "the row is in the table" is not the property a picker cares about.
    /// </para>
    /// </summary>
    [Fact]
    public async Task The_seeded_vocabulary_is_visible_on_the_route_a_picker_reads()
    {
        await ArrangeCarriedOverDatabaseAsync();

        using var factory = new DevelopmentApiFactory(Sql.ConnectionString);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/v1/classifications");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        var published = body.RootElement.GetProperty("items").EnumerateArray()
            .Select(c => c.GetProperty("name").GetString())
            .ToList();

        foreach (var expected in ClassificationSeedValues.Names)
        {
            Assert.True(
                published.Contains(expected),
                $"GET /classifications did not publish '{expected}' on a freshly seeded Development " +
                $"host. Published: [{string.Join(", ", published)}].");
        }
    }

    // ---------------------------------------------------------------------- delete is refused, durably

    /// <summary>
    /// <b>The durability half of the seed-protection guard.</b> A carried-over database — a school
    /// already present, exactly the arrangement the rest of this file exists to force — refuses to
    /// delete one of the eight seeded classifications, and a SECOND boot afterwards does not revive the
    /// question: the row is exactly where the first boot's refusal left it, deleted by nobody, because
    /// nobody's delete ever got past the guard.
    ///
    /// <para>
    /// This is the fact <c>ClassificationSeedProtectedDeleteTests</c> cannot reach on its own —
    /// <see cref="EamsApiFactory"/> never boots the seed, so it can only prove the guard fires against a
    /// row that merely happens to share a seeded <c>NameKey</c>. Here the row IS the seeded row, on a
    /// host that actually runs <c>SeedData.SeedClassificationsAsync</c>, and the second boot is what
    /// would have silently undone a delete the first cut of this guard let through.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_seeded_classification_cannot_be_deleted_and_the_refusal_survives_a_second_boot()
    {
        var schoolId = await ArrangeCarriedOverDatabaseAsync();

        using (var first = new DevelopmentApiFactory(Sql.ConnectionString)) Boot(first);

        Guid napId;
        await using (var db = NewDbContext())
        {
            napId = await db.Classifications
                .Where(c => c.SchoolId == schoolId && c.Name == "NAP")
                .Select(c => c.Id)
                .SingleAsync();

            var deletion = await ClassificationsOn(db).DeleteAsync(napId);

            Assert.Equal(ClassificationWriteOutcome.SeedProtected, deletion.Outcome);
        }

        await using (var read = NewDbContext())
        {
            Assert.True(
                await read.Classifications.AsNoTracking().AnyAsync(c => c.Id == napId),
                "DeleteAsync answered SeedProtected but the row is gone before a second boot even ran.");
        }

        using (var second = new DevelopmentApiFactory(Sql.ConnectionString)) Boot(second);

        var names = await NamesAsync(schoolId);

        Assert.Contains("NAP", names);
        Assert.Equal(
            ClassificationSeedValues.Names.Count,
            names.Count(n => ClassificationSeedValues.Names.Contains(n)));

        await using var afterSecondBoot = NewDbContext();
        var napRow = await afterSecondBoot.Classifications
            .AsNoTracking()
            .SingleAsync(c => c.Id == napId);

        Assert.True(
            napRow.IsActive,
            "The row survived, but the second boot's seed guard treated the refused delete as a gap " +
            "and reseeded a SECOND NAP row instead of leaving this one alone — Booting_twice_seeds_" +
            "each_classification_once would also have caught a duplicate; this pins that the SAME row, " +
            "by id, is what is left both before and after the second boot.");
    }
}
