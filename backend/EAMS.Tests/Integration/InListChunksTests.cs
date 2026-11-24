using EAMS.Domain;
using EAMS.Infrastructure.Data;
using EAMS.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EAMS.Tests.Integration;

/// <summary>
/// <see cref="InListChunks"/> — the compatibility-110 IN-list splitter the roster import's lookups go
/// through (2026-11-24 scale run: a single 100,000-literal IN list failed with SQL Server error 8623).
///
/// <para>
/// Against a real SQL Server on purpose: the de-duplication case exists because the column compares
/// under a case-insensitive collation while the caller distincts its keys ordinally, and an in-memory
/// provider compares the way C# does, so that case would pass there for the wrong reason.
/// </para>
/// </summary>
[Collection(DatabaseCollection.Name)]
public class InListChunksTests : IntegrationTest
{
    public InListChunksTests(SqlServerFixture sql) : base(sql) { }

    private async Task<Guid> ArrangeStudentsAsync(params string[] numbers)
    {
        await using var db = NewDbContext();
        var school = TestData.NewSchool();
        db.Schools.Add(school);
        foreach (var number in numbers) db.Students.Add(TestData.NewStudent(school.Id, number));
        await db.SaveChangesAsync();
        return school.Id;
    }

    [Fact]
    public async Task A_key_list_longer_than_one_chunk_returns_every_match_exactly_once()
    {
        var numbers = Enumerable.Range(1, 7).Select(i => $"N-{i:D3}").ToArray();
        var schoolId = await ArrangeStudentsAsync(numbers);

        // Two keys that match nothing, so the chunks are not all full of hits.
        var keys = numbers.Concat(["N-998", "N-999"]).ToList();
        var chunks = 0;

        await using var db = NewDbContext();
        var found = await InListChunks.ToListInChunksAsync(keys, chunk =>
        {
            chunks++;
            Assert.True(chunk.Length <= 3, $"A chunk carried {chunk.Length} keys; the ceiling was 3.");
            return db.Students.Where(s => s.SchoolId == schoolId && chunk.Contains(s.StudentNumber));
        }, CancellationToken.None, chunkSize: 3);

        Assert.Equal(3, chunks);
        Assert.Equal(numbers.Order(), found.Select(s => s.StudentNumber).Order());
    }

    /// <summary>
    /// The case the helper's de-duplication exists for. <c>StudentNumbers</c> compares case-insensitively,
    /// so two ordinally-distinct keys find the same row. In one IN list that row came back once; split
    /// across chunks it comes back once per chunk, and the importer's <c>ToDictionary</c> by number would
    /// throw on the second copy.
    /// </summary>
    [Fact]
    public async Task Keys_differing_only_by_case_in_different_chunks_return_the_row_once()
    {
        var schoolId = await ArrangeStudentsAsync("AB-0001");

        await using var db = NewDbContext();
        var found = await InListChunks.ToListInChunksAsync(
            ["AB-0001", "ab-0001"],
            chunk => db.Students.Where(s => s.SchoolId == schoolId && chunk.Contains(s.StudentNumber)),
            CancellationToken.None, chunkSize: 1);

        var only = Assert.Single(found);
        Assert.Equal("AB-0001", only.StudentNumber);
    }

    [Fact]
    public async Task An_empty_key_list_issues_no_statement()
    {
        var found = await InListChunks.ToListInChunksAsync<string, Student>(
            [],
            _ => throw new InvalidOperationException("No statement should be composed for an empty key list."),
            CancellationToken.None);

        Assert.Empty(found);
    }

    /// <summary>
    /// Pins the production ceiling. 1,000 literals is where the measured compile cost was still linear;
    /// a later edit raising it toward the list sizes a roster produces walks back toward error 8623, and
    /// this is where that edit is noticed.
    /// </summary>
    [Fact]
    public async Task The_production_chunk_size_splits_a_roster_sized_list_into_statements_of_at_most_1000()
    {
        Assert.Equal(1000, InListChunks.Size);

        var keys = Enumerable.Range(0, 2_500).Select(i => $"MISSING-{i:D5}").ToList();
        var sizes = new List<int>();

        await using var db = NewDbContext();
        var found = await InListChunks.ToListInChunksAsync(keys, chunk =>
        {
            sizes.Add(chunk.Length);
            return db.Students.Where(s => chunk.Contains(s.StudentNumber));
        }, CancellationToken.None);

        Assert.Empty(found);
        Assert.Equal([1000, 1000, 500], sizes);
    }
}
