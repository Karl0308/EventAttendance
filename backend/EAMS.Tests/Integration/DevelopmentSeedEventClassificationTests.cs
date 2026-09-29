using EAMS.Domain;
using EAMS.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EAMS.Tests.Integration;

/// <summary>
/// The seeded event-classification vocabulary, and specifically <b>the seed-path trap</b> — the same one
/// <see cref="DevelopmentSeedClassificationTests"/> exists for. <c>SeedData.InitializeAsync</c> returns
/// early once a <c>School</c> row exists, so a seed step placed below that return reaches only a database
/// nobody has. Every test here therefore boots against a database that <b>already has a school</b>: that
/// arrangement is the test.
/// </summary>
[Collection(DatabaseCollection.Name)]
public class DevelopmentSeedEventClassificationTests : IntegrationTest
{
    public DevelopmentSeedEventClassificationTests(SqlServerFixture sql) : base(sql) { }

    private static void Boot(DevelopmentApiFactory factory) => factory.CreateClient().Dispose();

    /// <summary>
    /// A database as a carried-over dev machine has it: a school and a term already present, so every
    /// pre-existing seed step is a no-op and the event classifications are the only thing this boot can
    /// write.
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
        return await read.EventClassifications.AsNoTracking()
            .Where(c => c.SchoolId == schoolId)
            .OrderBy(c => c.Name)
            .Select(c => c.Name)
            .ToListAsync();
    }

    [Fact]
    public async Task A_database_that_already_has_a_school_still_gets_every_event_classification()
    {
        var schoolId = await ArrangeCarriedOverDatabaseAsync();

        using var factory = new DevelopmentApiFactory(Sql.ConnectionString);
        Boot(factory);

        var names = await NamesAsync(schoolId);

        Assert.True(
            EventClassificationSeedValues.Names.Order(StringComparer.Ordinal)
                .SequenceEqual(names.Order(StringComparer.Ordinal)),
            $"A database that already carried a School row received [{string.Join(", ", names)}] instead " +
            "of the three seeded event classifications. This is the seed-path trap: a seed placed below " +
            "SeedData.InitializeAsync's early return reaches only a freshly dropped database, which is the " +
            "one database no developer and no deployed VM actually has.");
    }

    [Fact]
    public async Task The_seeded_descriptions_are_written()
    {
        var schoolId = await ArrangeCarriedOverDatabaseAsync();

        using var factory = new DevelopmentApiFactory(Sql.ConnectionString);
        Boot(factory);

        await using var read = NewDbContext();
        var institutional = await read.EventClassifications.AsNoTracking()
            .SingleAsync(c => c.SchoolId == schoolId && c.Name == "Institutional Events");

        Assert.Equal("Captures attendance for all university stakeholders.", institutional.Description);
        Assert.True(institutional.IsActive);
    }

    [Fact]
    public async Task Booting_twice_seeds_each_event_classification_once()
    {
        var schoolId = await ArrangeCarriedOverDatabaseAsync();

        using (var first = new DevelopmentApiFactory(Sql.ConnectionString)) Boot(first);
        using (var second = new DevelopmentApiFactory(Sql.ConnectionString)) Boot(second);

        var names = await NamesAsync(schoolId);

        Assert.True(
            names.Count == EventClassificationSeedValues.Names.Count,
            $"Two boots left {names.Count} event classifications where three were expected: " +
            $"[{string.Join(", ", names)}]. The seed guard is per value and keyed on NameKey; a guard " +
            "that compared display names, or checked only whether the table was empty, produces this.");
    }

    [Fact]
    public async Task A_deactivated_event_classification_stays_deactivated_across_a_boot()
    {
        var schoolId = await ArrangeCarriedOverDatabaseAsync();

        using (var first = new DevelopmentApiFactory(Sql.ConnectionString)) Boot(first);

        await using (var edit = NewDbContext())
        {
            var row = await edit.EventClassifications
                .SingleAsync(c => c.SchoolId == schoolId && c.Name == "Organizational Events");
            row.IsActive = false;
            row.RetiredAt = DateTime.UtcNow;
            await edit.SaveChangesAsync();
        }

        using (var second = new DevelopmentApiFactory(Sql.ConnectionString)) Boot(second);

        await using var read = NewDbContext();
        var row2 = await read.EventClassifications.AsNoTracking()
            .SingleAsync(c => c.SchoolId == schoolId && c.Name == "Organizational Events");

        Assert.False(
            row2.IsActive,
            "A restart reactivated an event classification an administrator had deactivated. The seed's " +
            "job is to make the vocabulary exist, not to reassert its opening state against the operator.");
    }
}
