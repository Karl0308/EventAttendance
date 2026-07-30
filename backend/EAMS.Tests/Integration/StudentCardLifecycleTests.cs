using System.Text;
using EAMS.Application.Abstractions;
using EAMS.Application.Dtos;
using EAMS.Domain;
using EAMS.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EAMS.Tests.Integration;

/// <summary>
/// A card's whole life through the §6.2 endpoints, and the two column boundaries either side of it.
///
/// <para>
/// <see cref="StudentWriteTests"/> proves each step in isolation, but it arranges the interesting
/// starting states by writing rows directly — <c>A_deactivated_card_uid_can_be_issued_again</c> inserts
/// an already-inactive card rather than deactivating one. That leaves the actual sequence untested:
/// <b>assign → deactivate → reissue</b> is ADR-001 D-3's entire reason for existing (a card is retired
/// and its serial issued again, on a re-encoded replacement or on a recycled card), and it is the one
/// path where a mistake costs the issuance history rather than a request.
/// </para>
///
/// <para>
/// The boundaries are here for the reason <c>KnownDefectTests</c> DEFECT 4 records: closing a
/// validation gap on one column says nothing about the column beside it. <c>CardUid</c> is bounded and
/// checked <em>after</em> normalization; <c>Label</c> is bounded and was never covered at all, and an
/// over-length one reaches SQL Server as error 2628 — a 500 on input the caller got wrong.
/// </para>
/// </summary>
[Collection(DatabaseCollection.Name)]
public class StudentCardLifecycleTests : IntegrationTest
{
    public StudentCardLifecycleTests(SqlServerFixture sql) : base(sql) { }

    private const string ReaderFormatUid = "04:a7:b8:c9";
    private const string NormalizedUid = "04A7B8C9";

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

    // ------------------------------------------------------------------------------ reissue

    /// <summary>
    /// <b>ADR-001 D-3's scenario, run end to end through the endpoints for the first time.</b>
    ///
    /// <para>
    /// A student loses their ID; the registrar issues a replacement encoded with the <em>same
    /// serial</em>.
    /// Under §4.4's literal <c>UNIQUE(CardUid)</c> this is unrecordable without erasing the original
    /// row, which is why D-3 rescoped it to <c>WHERE IsActive = 1</c>. The property that matters is not
    /// that the second assignment succeeds — it is that <b>both rows survive</b>, because
    /// <c>AttendanceRecords.RfidCardId</c> on every past tap points at the first one, and "which
    /// physical card was presented" has to stay answerable.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_card_reissued_to_the_same_student_keeps_both_rows_and_leaves_one_active()
    {
        var schoolId = await ArrangeSchoolAsync();
        var studentId = await ArrangeStudentAsync(schoolId);

        Guid originalCardId;
        await using (var db = NewDbContext())
        {
            var issued = await StudentsOn(db).AddCardAsync(
                studentId, new StudentCardRequest(ReaderFormatUid, "Original ID"));

            Assert.Equal(StudentWriteOutcome.Saved, issued.Outcome);
            originalCardId = issued.Card!.Id;
        }

        await using (var db = NewDbContext())
        {
            Assert.Equal(StudentWriteOutcome.Saved,
                (await StudentsOn(db).DeactivateCardAsync(studentId, originalCardId)).Outcome);
        }

        Guid replacementCardId;
        await using (var db = NewDbContext())
        {
            // The same serial, written in a different reader's format — because the replacement card is
            // read by whatever hardware is nearest, and normalization is what makes it the same card.
            var reissued = await StudentsOn(db).AddCardAsync(
                studentId, new StudentCardRequest("04-A7-B8-C9", "Replacement ID"));

            Assert.Equal(StudentWriteOutcome.Saved, reissued.Outcome);
            replacementCardId = reissued.Card!.Id;
        }

        Assert.NotEqual(originalCardId, replacementCardId);

        await using var read = NewDbContext();
        var cards = await read.RfidCards.AsNoTracking()
            .Where(c => c.StudentId == studentId).OrderBy(c => c.IssuedAt).ToListAsync();

        Assert.Equal(2, cards.Count);

        var original = cards.Single(c => c.Id == originalCardId);
        var replacement = cards.Single(c => c.Id == replacementCardId);

        Assert.False(original.IsActive);
        Assert.NotNull(original.DeactivatedAt);
        Assert.Equal("Original ID", original.Label);

        Assert.True(replacement.IsActive);
        Assert.Null(replacement.DeactivatedAt);
        Assert.Equal(NormalizedUid, replacement.CardUid);

        // And the hot path resolves to the student through the live card, not the retired one.
        var resolved = await StudentsOn(read).GetByCardUidAsync(ReaderFormatUid);
        Assert.Equal(studentId, resolved!.Id);
    }

    /// <summary>
    /// The same sequence with the serial changing hands. Distinct from the test above because the
    /// deactivation and the reissue are performed by <em>different</em> student resources, so the
    /// service's "is this already ours?" short circuit cannot be what makes it work — only the filtered
    /// index can.
    /// </summary>
    [Fact]
    public async Task A_uid_freed_by_a_deactivation_can_be_reissued_to_a_different_student()
    {
        var schoolId = await ArrangeSchoolAsync();
        var ownerId = await ArrangeStudentAsync(schoolId, "2023-0001");
        var successorId = await ArrangeStudentAsync(schoolId, "2023-0002");

        Guid cardId;
        await using (var db = NewDbContext())
            cardId = (await StudentsOn(db).AddCardAsync(
                ownerId, new StudentCardRequest(NormalizedUid, null))).Card!.Id;

        // Before the deactivation, the successor cannot have it.
        await using (var db = NewDbContext())
        {
            Assert.Equal(StudentWriteOutcome.CardUidInUse,
                (await StudentsOn(db).AddCardAsync(
                    successorId, new StudentCardRequest(NormalizedUid, null))).Outcome);
        }

        await using (var db = NewDbContext())
            await StudentsOn(db).DeactivateCardAsync(ownerId, cardId);

        await using (var db = NewDbContext())
        {
            Assert.Equal(StudentWriteOutcome.Saved,
                (await StudentsOn(db).AddCardAsync(
                    successorId, new StudentCardRequest(NormalizedUid, null))).Outcome);
        }

        await using var read = NewDbContext();

        Assert.Equal(2, await read.RfidCards.AsNoTracking().CountAsync());
        Assert.Equal(successorId,
            (await read.RfidCards.AsNoTracking().SingleAsync(c => c.IsActive)).StudentId);

        // The UID now names the successor everywhere it is resolved — including on the tap path's own
        // lookup, which is the read that matters and the one a stale index would get wrong.
        Assert.Equal(successorId, (await StudentsOn(read).GetByCardUidAsync(NormalizedUid))!.Id);
    }

    /// <summary>
    /// Deactivating twice is idempotent through the service and the retired row is not re-stamped. The
    /// existing coverage asserts the outcome of the second call; this asserts that
    /// <c>DeactivatedAt</c> — the only record of <em>when</em> a card left circulation, and therefore
    /// the only thing that orders a student's issuance history — is not quietly overwritten by a
    /// retried DELETE.
    /// </summary>
    [Fact]
    public async Task Deactivating_twice_does_not_move_the_deactivation_timestamp()
    {
        var schoolId = await ArrangeSchoolAsync();
        var studentId = await ArrangeStudentAsync(schoolId);

        Guid cardId;
        await using (var db = NewDbContext())
            cardId = (await StudentsOn(db).AddCardAsync(
                studentId, new StudentCardRequest(NormalizedUid, null))).Card!.Id;

        DateTime first;
        await using (var db = NewDbContext())
        {
            await StudentsOn(db).DeactivateCardAsync(studentId, cardId);
            first = (await db.RfidCards.AsNoTracking().SingleAsync(c => c.Id == cardId)).DeactivatedAt!.Value;
        }

        await using (var db = NewDbContext())
            await StudentsOn(db).DeactivateCardAsync(studentId, cardId);

        await using var read = NewDbContext();
        Assert.Equal(first, (await read.RfidCards.AsNoTracking().SingleAsync(c => c.Id == cardId)).DeactivatedAt);
    }

    // --------------------------------------------------------------------------- boundaries

    /// <summary>
    /// The UID length is judged on the <em>normalized</em> value, at both ends of the boundary.
    ///
    /// <para>
    /// One character over is a named 400. One character over with the check removed is SQL Server error
    /// 2628 — <c>String or binary data would be truncated</c> — reaching the pipeline as a 500, which is
    /// the exact defect shape <c>KnownDefectTests</c> DEFECT 4 closed on <c>notes</c> and which nothing
    /// covered here.
    /// </para>
    ///
    /// <para>
    /// The separators matter to the arithmetic: 128 alphanumeric characters interleaved with colons is
    /// 255 characters on the wire and 128 in the column, so a check applied before normalization would
    /// refuse a perfectly storable UID. Both cases are asserted rather than only the failing one.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_uid_is_measured_after_normalization_at_both_sides_of_the_column_width()
    {
        var schoolId = await ArrangeSchoolAsync();
        var studentId = await ArrangeStudentAsync(schoolId);

        var atTheLimit = new string('A', RfidCardText.CardUidMaxLength);
        var overTheLimit = new string('A', RfidCardText.CardUidMaxLength + 1);
        var separated = string.Join(':', atTheLimit.ToCharArray());

        await using (var db = NewDbContext())
        {
            var refused = await StudentsOn(db).AddCardAsync(
                studentId, new StudentCardRequest(overTheLimit, null));

            Assert.Equal(StudentWriteOutcome.ValidationFailed, refused.Outcome);
            Assert.Contains(
                RfidCardText.CardUidMaxLength.ToString(), refused.Message, StringComparison.Ordinal);
        }

        await using (var db = NewDbContext())
            Assert.Empty(await db.RfidCards.AsNoTracking().ToListAsync());

        await using (var db = NewDbContext())
        {
            // 255 characters on the wire, 128 in the column. Accepted, because the column is what the
            // limit is about.
            Assert.True(separated.Length > RfidCardText.CardUidMaxLength);

            var accepted = await StudentsOn(db).AddCardAsync(
                studentId, new StudentCardRequest(separated, null));

            Assert.Equal(StudentWriteOutcome.Saved, accepted.Outcome);
            Assert.Equal(atTheLimit, accepted.Card!.CardUid);
        }
    }

    /// <summary>
    /// <c>Label</c> is <c>nvarchar(100)</c> and was the one bounded column on this request with no
    /// coverage at all. Same failure shape as the UID's, one parameter over.
    /// </summary>
    [Fact]
    public async Task An_over_length_label_is_rejected_rather_than_truncated()
    {
        var schoolId = await ArrangeSchoolAsync();
        var studentId = await ArrangeStudentAsync(schoolId);

        await using var db = NewDbContext();
        var response = await StudentsOn(db).AddCardAsync(
            studentId,
            new StudentCardRequest(NormalizedUid, new string('L', RfidCardText.LabelMaxLength + 1)));

        Assert.Equal(StudentWriteOutcome.ValidationFailed, response.Outcome);

        await using var read = NewDbContext();
        Assert.Empty(await read.RfidCards.AsNoTracking().ToListAsync());
    }

    /// <summary>
    /// <c>Label</c>'s length is judged on the <b>cleaned</b> value, exactly as the UID's is judged on
    /// the normalized one.
    ///
    /// <para>
    /// <see cref="An_over_length_label_is_rejected_rather_than_truncated"/> cannot see this: 101
    /// ordinary <c>'L'</c>s are unchanged by <c>RosterText.Clean</c>, so it passes identically whether
    /// the check runs before or after cleaning. This case only passes if it runs after —
    /// <c>RosterText</c> composes to NFC and NFC <em>expands</em> U+0344 into U+0308 U+0301, so 60
    /// characters on the wire become 120 in the column. Measured raw, this is a comfortable 60 against
    /// a limit of 100 and reaches <c>nvarchar(100)</c> as SQL Server error 2628 — the same 500 the
    /// student-name path closed, on the one bounded column of this request that had no equivalent
    /// cover.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_label_is_measured_after_cleaning_and_not_before()
    {
        var schoolId = await ArrangeSchoolAsync();
        var studentId = await ArrangeStudentAsync(schoolId);

        var expanding = new string('̈́', 60);

        // The premise: raw it fits, composed it does not. Without this the test could pass for the
        // trivial reason that the input was over the limit all along.
        Assert.True(expanding.Length <= RfidCardText.LabelMaxLength);
        Assert.True(expanding.Normalize(NormalizationForm.FormC).Length > RfidCardText.LabelMaxLength);

        await using var db = NewDbContext();
        var response = await StudentsOn(db).AddCardAsync(
            studentId, new StudentCardRequest(NormalizedUid, expanding));

        Assert.Equal(StudentWriteOutcome.ValidationFailed, response.Outcome);

        await using var read = NewDbContext();
        Assert.Empty(await read.RfidCards.AsNoTracking().ToListAsync());
    }

    /// <summary>
    /// A blank label is stored as <c>NULL</c>, not as <c>''</c> — the <c>RosterText.Clean</c> rule the
    /// student columns already follow, applied here so a card added by hand and a card issued by the
    /// §10 importer are the same row shape. Two ways of spelling "no label" is how a grid ends up
    /// rendering an empty chip for half the roster.
    /// </summary>
    [Fact]
    public async Task A_blank_label_is_stored_as_null_rather_than_an_empty_string()
    {
        var schoolId = await ArrangeSchoolAsync();
        var studentId = await ArrangeStudentAsync(schoolId);

        await using (var db = NewDbContext())
        {
            Assert.Equal(StudentWriteOutcome.Saved,
                (await StudentsOn(db).AddCardAsync(
                    studentId, new StudentCardRequest(NormalizedUid, "   "))).Outcome);
        }

        await using var read = NewDbContext();
        Assert.Null((await read.RfidCards.AsNoTracking().SingleAsync()).Label);
    }

    /// <summary>
    /// A card cannot be assigned to a soft-deleted student — <c>FindAsync</c> filters them, so the
    /// student is simply not there. Asserted because the alternative is worse than it looks: an active
    /// card on a deleted student occupies the one active slot for that UID
    /// (<see cref="StudentSoftDeleteStrandingTests"/>), and letting a write <em>create</em> one would
    /// mean the API can manufacture that state rather than merely inherit it.
    /// </summary>
    [Fact]
    public async Task A_card_cannot_be_assigned_to_a_soft_deleted_student()
    {
        var schoolId = await ArrangeSchoolAsync();
        var studentId = await ArrangeStudentAsync(schoolId);

        await using (var db = NewDbContext())
            await StudentsOn(db).DeleteAsync(studentId);

        await using var add = NewDbContext();
        var response = await StudentsOn(add).AddCardAsync(
            studentId, new StudentCardRequest(NormalizedUid, null));

        Assert.Equal(StudentWriteOutcome.NotFound, response.Outcome);

        await using var read = NewDbContext();
        Assert.Empty(await read.RfidCards.AsNoTracking().ToListAsync());
    }

    /// <summary>
    /// Every separator shape one reader or another emits, all resolving to the one stored card. The
    /// existing suite proves two of these; the value of the rest is that <c>CardUid.Normalize</c> keeps
    /// only letters and digits, so anything else a vendor puts between the bytes has to vanish — and the
    /// one that would not is a UID whose separator is alphanumeric, which is why <c>04x A7x B8x C9</c>
    /// is deliberately absent from the list.
    /// </summary>
    [Theory]
    [InlineData("04:a7:b8:c9")]
    [InlineData("04-A7-B8-C9")]
    [InlineData("04 a7 b8 c9")]
    [InlineData("04a7b8c9")]
    [InlineData("  04:A7:b8:C9  ")]
    [InlineData("04.a7.b8.c9")]
    public async Task Every_reader_spelling_of_one_uid_resolves_to_the_one_stored_card(string spelling)
    {
        var schoolId = await ArrangeSchoolAsync();
        var studentId = await ArrangeStudentAsync(schoolId);

        await using (var db = NewDbContext())
            await StudentsOn(db).AddCardAsync(studentId, new StudentCardRequest(NormalizedUid, null));

        await using var read = NewDbContext();

        // Re-posting it in this spelling is a no-op rather than a second row or a conflict...
        var repost = await StudentsOn(read).AddCardAsync(studentId, new StudentCardRequest(spelling, null));
        Assert.Equal(StudentWriteOutcome.Saved, repost.Outcome);

        // ...and resolving it finds the same student.
        Assert.Equal(studentId, (await StudentsOn(read).GetByCardUidAsync(spelling))!.Id);

        await using var count = NewDbContext();
        Assert.Single(await count.RfidCards.AsNoTracking().ToListAsync());
    }
}
