using System.Net;
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
/// <b>Finding a student by the number printed on their card</b> — the searchable-RFID requirement, and
/// QA's two answers about what it has to do (Q5: an old card still finds its student; Q6: a fragment
/// finds the whole serial).
///
/// <para>
/// <b>The case every assertion here is really about is the one ADR-001 D-3 created deliberately.</b>
/// Card uniqueness is <c>UNIQUE(SchoolId, CardUid) WHERE IsActive = 1</c>, so <em>inactive</em> rows are
/// unconstrained on purpose — a serial that is revoked today may be re-encoded and issued to somebody
/// else tomorrow, and the old row survives to explain the taps it produced. The moment QA said a
/// withdrawn card must still resolve, this lookup became multi-valued: one serial, several cards,
/// several people, and no single right answer. A shape that returned one student would have been
/// plausible, non-empty and wrong for exactly the case the schema was designed around.
/// </para>
///
/// <para>
/// <b>On SQL Server, never EF InMemory.</b> The filtered unique index is what makes the reissue case
/// legal in the first place; under a provider that ignores index filters the arrange step below would
/// be rejected, and the test would be proving something about a database nobody runs.
/// </para>
/// </summary>
[Collection(DatabaseCollection.Name)]
public class CardSearchTests : IntegrationTest
{
    public CardSearchTests(SqlServerFixture sql) : base(sql) { }

    private const string Route = "/api/v1/cards";

    /// <summary>The serial the client's export actually writes — decimal digits, leading zeros and all.</summary>
    private const string RealSerial = "0012503326";

    // ------------------------------------------------------------------------------------- arrange

    /// <summary>
    /// A school and a student holding one card with <paramref name="uid"/>, active or not.
    /// </summary>
    private async Task<(Guid SchoolId, Guid StudentId, Guid CardId)> ArrangeAsync(
        string uid = RealSerial, bool active = true, string studentNumber = "2023-0001")
    {
        await using var db = NewDbContext();

        var school = TestData.NewSchool();
        db.Schools.Add(school);

        var student = TestData.NewStudent(school.Id, studentNumber);
        db.Students.Add(student);

        var card = TestData.NewCard(school.Id, student.Id, uid, active);
        db.RfidCards.Add(card);

        await db.SaveChangesAsync();

        return (school.Id, student.Id, card.Id);
    }

    private async Task<Guid> AddStudentWithCardAsync(
        Guid schoolId, string studentNumber, string uid, bool active)
    {
        await using var db = NewDbContext();

        var student = TestData.NewStudent(schoolId, studentNumber, lastName: $"Holder{studentNumber}");
        db.Students.Add(student);
        db.RfidCards.Add(TestData.NewCard(schoolId, student.Id, uid, active));

        await db.SaveChangesAsync();

        return student.Id;
    }

    private async Task<PagedResult<CardMatchDto>> SearchAsync(string fragment)
    {
        await using var db = NewDbContext();

        var response = await StudentsOn(db).SearchCardsAsync(fragment, PageRequest.Default);

        Assert.Equal(CardSearchOutcome.Matched, response.Outcome);
        return response.Matches!;
    }

    // ----------------------------------------------------------- D-3: one serial, two people, no winner

    /// <summary>
    /// <b>A serial re-issued across two students surfaces both cards and nominates neither.</b>
    ///
    /// <para>
    /// This is the whole reason the lookup returns a list. The arrange itself is half the assertion: the
    /// filtered index permits a deactivated <c>0012503326</c> on one student and an active one on
    /// another, so the database really does hold two answers, and any shape that could only carry one
    /// would have to silently discard a card somebody is still walking around with.
    /// </para>
    ///
    /// <para>
    /// The response is checked for <em>both</em> students and for each card's own state. Asserting only
    /// the count would pass against a lookup that returned the same card twice.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_serial_reissued_across_two_students_surfaces_both_and_claims_neither()
    {
        var (schoolId, firstStudentId, firstCardId) =
            await ArrangeAsync(RealSerial, active: false, studentNumber: "2023-0001");

        var secondStudentId = await AddStudentWithCardAsync(
            schoolId, "2024-0002", RealSerial, active: true);

        var matches = await SearchAsync(RealSerial);

        Assert.Equal(2, matches.Total);
        Assert.Equal(2, matches.Items.Count);

        // Two distinct cards on two distinct students — not one card reported twice.
        Assert.Equal(2, matches.Items.Select(m => m.CardId).Distinct().Count());
        Assert.Equal(
            new[] { firstStudentId, secondStudentId }.Order(),
            matches.Items.Select(m => m.StudentId).Order());

        var withdrawn = Assert.Single(matches.Items, m => m.CardId == firstCardId);
        Assert.False(withdrawn.IsActive);
        Assert.NotNull(withdrawn.DeactivatedAt);
        Assert.Equal(firstStudentId, withdrawn.StudentId);

        var live = Assert.Single(matches.Items, m => m.StudentId == secondStudentId);
        Assert.True(live.IsActive);
        Assert.Null(live.DeactivatedAt);

        // NOTE: there is deliberately no assertion here that "the payload names no single student".
        // That property is held by the return TYPE — a PagedResult<CardMatchDto> — so a change to it is
        // a compile error in this file, not a runtime failure. An assertion that a database-generated
        // Guid is non-empty would have read as if it were guarding the shape while guarding nothing.
    }

    // -------------------------------------------------------------------- QA Q5: an old card still finds

    /// <summary>
    /// <b>A deactivated card resolves, and is reported as deactivated.</b> QA Q5 — "so admin can still
    /// be able to track the card's association with the student".
    ///
    /// <para>
    /// Both halves matter and fail separately. Resolving it is the requirement; saying it is withdrawn,
    /// and when, is what stops an administrator handing a revoked card back as though it still worked.
    /// </para>
    ///
    /// <para>
    /// <b>The kiosk route is asserted to still refuse it, in the same test.</b>
    /// <c>GET /students/by-card/{uid}</c> filters <c>IsActive</c> on purpose — only one card may tap at
    /// a time — and this feature must not have relaxed it. Two routes, two correct and opposite answers
    /// about the same card; pinning them together is what keeps a later "tidy-up" from collapsing them
    /// into one.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_deactivated_card_resolves_and_is_reported_as_deactivated()
    {
        var (schoolId, studentId, cardId) = await ArrangeAsync(RealSerial, active: false);

        var matches = await SearchAsync(RealSerial);

        var match = Assert.Single(matches.Items);

        Assert.Equal(cardId, match.CardId);
        Assert.Equal(studentId, match.StudentId);
        Assert.Equal("2023-0001", match.StudentNumber);
        Assert.Equal("Maria Reyes Santos", match.FullName);
        Assert.False(match.IsActive);
        Assert.NotNull(match.DeactivatedAt);

        // The capture route is unchanged: no ACTIVE card matches, so it is still a 404.
        await using var db = NewDbContext();
        Assert.Null(await StudentsOn(db).GetByCardUidAsync(RealSerial));

        // Guard against the arrange having silently produced nothing to find.
        Assert.True(
            await db.RfidCards.AsNoTracking().AnyAsync(c => c.SchoolId == schoolId && !c.IsActive),
            "The fixture wrote no deactivated card, so neither half of this test was exercised.");
    }

    // ------------------------------------------------------------------ QA Q6: a fragment finds the card

    /// <summary>
    /// <b><c>25-03</c> finds <c>0012503326</c>.</b> QA Q6 answered yes to the fragment, and the
    /// separators are the half that is easy to miss: stored serials are normalized, so the fragment has
    /// to be normalized the same way before it is compared or a reader's punctuation would make a search
    /// that looks right return nothing.
    /// </summary>
    [Theory]
    [InlineData("2503")]
    [InlineData("25-03")]
    [InlineData("25:03")]
    [InlineData("25 03")]
    [InlineData("0012503326")]
    [InlineData("326")]
    public async Task A_fragment_finds_the_serial_it_is_part_of(string fragment)
    {
        var (_, studentId, cardId) = await ArrangeAsync(RealSerial);

        var matches = await SearchAsync(fragment);

        var match = Assert.Single(matches.Items);
        Assert.Equal(cardId, match.CardId);
        Assert.Equal(studentId, match.StudentId);

        // The stored form is what comes back, never the fragment the caller typed.
        Assert.Equal(RealSerial, match.CardUid);
    }

    /// <summary>
    /// <b>Leading zeros are significant: <c>0012503326</c> and <c>12503326</c> are two different
    /// cards.</b>
    ///
    /// <para>
    /// The CICSS export writes card numbers as decimal digits, which is why <c>RfidCards.CardUid</c> is
    /// a string and not a number — a numeric column would have collapsed these two into one card held by
    /// two people, silently, and the reissue case above would have become unrepresentable rather than
    /// merely multi-valued.
    /// </para>
    ///
    /// <para>
    /// Searching the <em>shorter</em> serial does return both, and that is the substring rule working
    /// rather than the zeros being lost: <c>0012503326</c> genuinely contains <c>12503326</c>. Searching
    /// the longer one returns only the longer card, which is what proves they are distinct rows.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Leading_zeros_are_significant_and_are_never_trimmed()
    {
        const string Padded = "0012503326";
        const string Bare = "12503326";

        var (schoolId, paddedStudentId, paddedCardId) = await ArrangeAsync(Padded);
        var bareStudentId = await AddStudentWithCardAsync(schoolId, "2024-0002", Bare, active: true);

        // Both serials are present in the table as two distinct rows, which is what makes the searches
        // below meaningful rather than a statement about one card. This says nothing about the write
        // path — the fixture inserts directly and never calls AddCardAsync; that UID normalization is
        // StudentCardLifecycleTests' subject.
        await using (var read = NewDbContext())
        {
            var stored = await read.RfidCards.AsNoTracking()
                .Select(c => c.CardUid).OrderBy(u => u).ToListAsync();

            Assert.Equal(new[] { Padded, Bare }.Order(), stored);
        }

        // The padded serial names exactly one card: its own.
        var padded = await SearchAsync(Padded);
        var onlyMatch = Assert.Single(padded.Items);
        Assert.Equal(paddedCardId, onlyMatch.CardId);
        Assert.Equal(paddedStudentId, onlyMatch.StudentId);

        // The bare serial is contained in the padded one, so it names both — two cards, two people.
        var bare = await SearchAsync(Bare);
        Assert.Equal(2, bare.Total);
        Assert.Equal(
            new[] { paddedStudentId, bareStudentId }.Order(),
            bare.Items.Select(m => m.StudentId).Order());
    }

    // --------------------------------------------------------------- the fragment that matches everything

    /// <summary>
    /// <b>A fragment with no letter or digit is refused, not run.</b>
    ///
    /// <para>
    /// This is the guard that keeps "normalize first, compare second" from becoming a disclosure. The
    /// normalized form of <c>'-'</c> is the empty string, and <c>LIKE '%%'</c> matches every card in the
    /// school — so a caller who typed a separator by accident would be handed the entire card registry,
    /// one page at a time, looking exactly like a result.
    /// </para>
    ///
    /// <para>
    /// The assertion is on the refusal <em>and</em> on the absence of matches, because a version that
    /// returned 400 with a populated page would be the same leak wearing an error code.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("-")]
    [InlineData("::")]
    [InlineData("   ")]
    [InlineData("")]
    [InlineData(null)]
    public async Task A_fragment_that_normalizes_to_nothing_is_refused_rather_than_matching_everything(
        string? fragment)
    {
        var (schoolId, _, _) = await ArrangeAsync(RealSerial);
        await AddStudentWithCardAsync(schoolId, "2024-0002", "04A7B8C9", active: true);

        await using var db = NewDbContext();

        var response = await StudentsOn(db).SearchCardsAsync(fragment, PageRequest.Default);

        Assert.Equal(CardSearchOutcome.FragmentUnusable, response.Outcome);

        Assert.True(
            response.Matches is null,
            "A refused search returned a page of matches. There are two cards in this school and an " +
            "empty fragment matches both — returning them alongside a 400 is the same disclosure with " +
            "a different status code on it.");
    }

    // ------------------------------------------------------------------------------ the grid's own search

    /// <summary>
    /// <b>The students grid finds a student by their card number</b> — the requirement as written ("add
    /// RFID Card Number as a searchable field"), rather than only through the dedicated lookup.
    ///
    /// <para>
    /// The fragment and separator cases are included here too: the grid's <c>search</c> box is one field
    /// serving four columns, and the card arm is the only one that normalizes, so it is the arm most
    /// likely to be wired up without that step and look correct against a serial typed with no
    /// punctuation.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("0012503326")]
    [InlineData("2503")]
    [InlineData("25-03")]
    public async Task The_students_grid_finds_a_student_by_card_number(string search)
    {
        var (_, studentId, _) = await ArrangeAsync(RealSerial);

        await using var db = NewDbContext();

        var page = await StudentsOn(db).ListAsync(search, null, null, PageRequest.Default);

        var student = Assert.Single(page.Items);
        Assert.Equal(studentId, student.Id);
    }

    /// <summary>
    /// And the grid finds them by a card that has been <b>withdrawn</b> — QA Q5 again, on the surface an
    /// administrator actually uses.
    /// </summary>
    [Fact]
    public async Task The_students_grid_finds_a_student_by_a_deactivated_card()
    {
        var (_, studentId, _) = await ArrangeAsync(RealSerial, active: false);

        await using var db = NewDbContext();

        var page = await StudentsOn(db).ListAsync("2503", null, null, PageRequest.Default);

        var student = Assert.Single(page.Items);
        Assert.Equal(studentId, student.Id);

        // The card that matched is visible on the row, flagged, so the grid can say which one it was
        // and that it is no longer live.
        var card = Assert.Single(student.Cards);
        Assert.Equal(RealSerial, card.CardUid);
        Assert.False(card.IsActive);
    }

    /// <summary>
    /// <b>The card arm did not swallow the three that were already there.</b>
    ///
    /// <para>
    /// The search box is one field over four columns, and the change that added the fourth is exactly
    /// the change that can quietly break the other three — by normalizing the fragment for all of them,
    /// which would stop <c>2023-0001</c> matching a student number stored verbatim.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("Maria")]
    [InlineData("Santos")]
    [InlineData("2023-0001")]
    public async Task The_grid_still_searches_names_and_student_numbers(string search)
    {
        var (_, studentId, _) = await ArrangeAsync(RealSerial);

        await using var db = NewDbContext();

        var page = await StudentsOn(db).ListAsync(search, null, null, PageRequest.Default);

        Assert.Equal(studentId, Assert.Single(page.Items).Id);
    }

    /// <summary>
    /// <b>And a punctuation-only search does not return every student who holds a card.</b>
    ///
    /// <para>
    /// The same empty-fragment hazard as the lookup, in the place it is harder to see: the grid's
    /// <c>search</c> cannot answer 400 — it is one of four arms and the other three are perfectly happy
    /// with punctuation — so the card arm has to be dropped instead. A version that left
    /// <c>Contains("")</c> in the predicate would turn a stray separator into "every carded student",
    /// which looks like a working search.
    /// </para>
    ///
    /// <para>
    /// <b>The separator is a colon and not a hyphen, and that is not arbitrary.</b> Written with
    /// <c>'-'</c> this test failed on its first run for an entirely legitimate reason: student numbers
    /// are <c>2023-0001</c>, so the <em>student-number</em> arm matched both rows and the card arm was
    /// never reached. A colon appears in no name and no student number here, so the only arm that could
    /// possibly match is the one under test.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_punctuation_only_grid_search_does_not_match_every_carded_student()
    {
        var (schoolId, _, _) = await ArrangeAsync(RealSerial);
        await AddStudentWithCardAsync(schoolId, "2024-0002", "04A7B8C9", active: true);

        await using var db = NewDbContext();

        var page = await StudentsOn(db).ListAsync("::", null, null, PageRequest.Default);

        Assert.True(
            page.Total == 0,
            $"Searching the grid for '::' returned {page.Total} student(s). No name and no student " +
            "number contains a colon, so this can only be the card arm: the fragment normalizes to the " +
            "empty string, and Contains(\"\") is true of every card — every student who holds one, " +
            "presented as a search result.");
    }

    // ------------------------------------------------------------------------------------- over HTTP

    /// <summary>
    /// The published shape: a page of matches, each carrying the card that matched and its state.
    /// </summary>
    [Fact]
    public async Task The_endpoint_returns_a_page_of_matches_with_each_cards_state()
    {
        var (schoolId, firstStudentId, _) = await ArrangeAsync(RealSerial, active: false);
        var secondStudentId = await AddStudentWithCardAsync(
            schoolId, "2024-0002", RealSerial, active: true);

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory);

        var response = await client.GetAsync($"{Route}?cardUid=25-03");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(2, body.RootElement.GetProperty("total").GetInt32());

        var items = body.RootElement.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(2, items.Count);

        Assert.Equal(
            new[] { firstStudentId, secondStudentId }.Order(),
            items.Select(i => i.GetProperty("studentId").GetGuid()).Order());

        // Each row says which card it is and whether it is live — the two fields that make a
        // multi-valued answer usable instead of ambiguous.
        foreach (var item in items)
        {
            Assert.Equal(RealSerial, item.GetProperty("cardUid").GetString());
            Assert.NotEqual(Guid.Empty, item.GetProperty("cardId").GetGuid());

        }

        // Each card's state is asserted against the ARRANGEMENT rather than against the other field on
        // its own row. Comparing isActive to deactivatedAt-is-null only says the two agree, which they
        // would if both described the wrong card — and this fixture has two cards with the same serial,
        // so that is exactly the confusion available to be made.
        var withdrawn = Assert.Single(items, i => i.GetProperty("studentId").GetGuid() == firstStudentId);
        Assert.False(withdrawn.GetProperty("isActive").GetBoolean());
        Assert.NotEqual(JsonValueKind.Null, withdrawn.GetProperty("deactivatedAt").ValueKind);

        var live = Assert.Single(items, i => i.GetProperty("studentId").GetGuid() == secondStudentId);
        Assert.True(live.GetProperty("isActive").GetBoolean());
        Assert.Equal(JsonValueKind.Null, live.GetProperty("deactivatedAt").ValueKind);
    }

    /// <summary>An empty result is an ordinary 200, not a 404 — it is a search, not a lookup.</summary>
    [Fact]
    public async Task A_search_that_matches_nothing_is_an_empty_200()
    {
        await ArrangeAsync(RealSerial);

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory);

        var response = await client.GetAsync($"{Route}?cardUid=999999");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(0, body.RootElement.GetProperty("total").GetInt32());
        Assert.Empty(body.RootElement.GetProperty("items").EnumerateArray());
    }

    /// <summary>
    /// A missing or unusable <c>cardUid</c> is a 400 carrying the stable <c>code</c> a client branches
    /// on, in §6's RFC 7807 shape like every other refusal on this API.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("-")]
    public async Task An_unusable_fragment_is_a_problem_body_carrying_its_code(string fragment)
    {
        await ArrangeAsync(RealSerial);

        using var factory = new EamsApiFactory(Sql.ConnectionString);
        using var client = await SignedInClientAsync(factory);

        var response = await client.GetAsync($"{Route}?cardUid={Uri.EscapeDataString(fragment)}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(
            nameof(CardSearchOutcome.FragmentUnusable),
            body.RootElement.GetProperty(CardsController.ErrorCodeProperty).GetString());
    }

    /// <summary>
    /// A soft-deleted student's cards do not surface, matching all three of <c>StudentService</c>'s
    /// other reads — the predicate whose absence <c>KnownDefectTests</c> DEFECT 1 was about.
    /// </summary>
    [Fact]
    public async Task A_soft_deleted_students_cards_do_not_surface()
    {
        var (_, studentId, _) = await ArrangeAsync(RealSerial);

        await using (var db = NewDbContext())
        {
            var student = await db.Students.SingleAsync(s => s.Id == studentId);
            student.IsDeleted = true;
            await db.SaveChangesAsync();
        }

        var matches = await SearchAsync(RealSerial);

        Assert.Equal(0, matches.Total);
        Assert.Empty(matches.Items);
    }
}
