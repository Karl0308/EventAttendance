using EAMS.Application.Abstractions;
using EAMS.Application.Dtos;
using EAMS.Domain;
using EAMS.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EAMS.Tests.Integration;

/// <summary>
/// The two systems the §6.2 write surface has to agree with, and neither of which it is tested against:
/// the §10 roster importer, which writes the same columns, and the §6.4 tap path, which reads them.
///
/// <para>
/// <b>Why this is the interesting file.</b> <see cref="StudentWriteTests"/> asserts that a manual create
/// runs its values through <c>RosterText</c>, and the service's own remarks explain why — a manual
/// "Maria&#160;Santos" carrying a non-breaking space would otherwise be a different string from the
/// imported one, compare unequal everywhere, and look identical in every UI. But that assertion is made
/// against a hard-coded expected string, so it proves the manual path normalizes; it does not prove the
/// two paths normalize <b>the same way</b>. Those are different claims, and only the second one is the
/// one that matters: the failure is not a wrong value, it is <em>two students</em>.
/// </para>
///
/// <para>
/// So every test here runs the other system for real. The import is the same in-memory workbook
/// <see cref="SisImportPipelineTests"/> uses; the tap is <c>AttendanceService</c>. Nothing is stubbed,
/// because a stub of either would agree with whatever the manual path did.
/// </para>
/// </summary>
[Collection(DatabaseCollection.Name)]
public class StudentWriteInteropTests : IntegrationTest
{
    public StudentWriteInteropTests(SqlServerFixture sql) : base(sql) { }

    /// <summary>
    /// Worked out by hand from <see cref="SyntheticRoster"/>'s twelve rows, matching
    /// <c>SisImportPipelineTests</c>. Restated rather than shared: the number this file is about is
    /// "eight, not nine", and importing a constant from the file that also asserts it would make a
    /// duplicate invisible in both places at once.
    /// </summary>
    private const int StudentsInTheRoster = 8;

    private sealed record World(Guid SchoolId, Guid TermId);

    private async Task<World> ArrangeAsync()
    {
        await using var db = NewDbContext();
        var school = TestData.NewSchool();
        db.Schools.Add(school);
        var term = TestData.NewTerm(school.Id);
        db.Terms.Add(term);
        await db.SaveChangesAsync();
        return new World(school.Id, term.Id);
    }

    private async Task<SisImportBatchDto> ImportAsync(Guid termId)
    {
        await using var content = SyntheticRoster.Build();
        content.Position = 0;

        await using var db = NewDbContext();
        var service = SisImportOn(db);

        var preview = await service.UploadAsync(
            new SisImportUploadRequest(content, "Copy-of-CCJ.xlsx", termId));

        return await service.RunAsync(preview.Batch.Id, termId);
    }

    private static StudentWriteRequest Request(
        string studentNumber, string firstName = "Maria", string lastName = "Santos",
        string? middleName = null) =>
        new(studentNumber, firstName, middleName, lastName, null, null, null, null);

    // ------------------------------------------------------------------------- the importer

    /// <summary>
    /// <b>A student typed in by hand is the same student the roster names, not a second one.</b>
    ///
    /// <para>
    /// This is the ordinary sequence at the start of a term: the registrar's file has not arrived, a
    /// student turns up, and somebody adds them through the admin. The import lands a week later
    /// carrying the same REGNO. §10.3's upsert key within a school is <c>StudentNumber</c>, matched
    /// <b>ordinally</b> — so the manual write and the import have to produce byte-identical values or
    /// the roster silently gains a duplicate that <c>UNIQUE(SchoolId, StudentNumber)</c> would not even
    /// refuse, because the two strings differ.
    /// </para>
    ///
    /// <para>
    /// The assertion is on the surviving <c>Id</c> rather than only on the count: an equal count could
    /// also mean the import inserted one and something else removed one, and the identity is what proves
    /// the row was <em>matched</em>. Everything hanging off that student — cards, enrollments, past
    /// attendance — follows the id.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_manually_created_student_is_matched_by_a_later_import_rather_than_duplicated()
    {
        var world = await ArrangeAsync();

        Guid manualId;
        await using (var db = NewDbContext())
        {
            var created = await StudentsOn(db).CreateAsync(
                Request(SyntheticRoster.MariaRegNo, firstName: "Typed", lastName: "ByHand"));

            Assert.Equal(StudentWriteOutcome.Saved, created.Outcome);
            manualId = created.Student!.Id;
        }

        var batch = await ImportAsync(world.TermId);
        Assert.Equal(0, batch.FailedRows);

        await using var read = NewDbContext();
        var students = await read.Students.AsNoTracking().ToListAsync();

        Assert.Equal(StudentsInTheRoster, students.Count);

        var maria = students.Single(s => s.StudentNumber == SyntheticRoster.MariaRegNo);
        Assert.Equal(manualId, maria.Id);

        // Matched and updated, not left as the hand-typed placeholder — which is the other half of
        // "the import owns roster identity" and the reason the manual entry is safe to make.
        Assert.NotEqual("ByHand", maria.LastName);
    }

    /// <summary>
    /// The same, with the whitespace a paste from a spreadsheet actually carries.
    ///
    /// <para>
    /// <c>StudentService.Validate</c> cleans the student number but deliberately does not
    /// <c>CardUid.Normalize</c> it, and its comment names this exact failure: <em>"a manual create that
    /// normalized differently would produce a student the next import cannot match"</em>. That claim had
    /// no test. A padded REGNO is not a contrived input — it is what a cell copied out of the
    /// registrar's own workbook contains, and it is invisible in every form field it is pasted into.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_padded_student_number_still_matches_the_import_that_names_it()
    {
        var world = await ArrangeAsync();

        Guid manualId;
        await using (var db = NewDbContext())
        {
            var created = await StudentsOn(db).CreateAsync(
                Request($"  {SyntheticRoster.MariaRegNo}  ", firstName: "Typed", lastName: "ByHand"));

            Assert.Equal(StudentWriteOutcome.Saved, created.Outcome);
            manualId = created.Student!.Id;
        }

        // Stored trimmed, exactly as the importer stores the same REGNO.
        await using (var read = NewDbContext())
        {
            Assert.Equal(SyntheticRoster.MariaRegNo,
                (await read.Students.AsNoTracking().SingleAsync()).StudentNumber);
        }

        var batch = await ImportAsync(world.TermId);
        Assert.Equal(0, batch.FailedRows);

        await using var after = NewDbContext();
        var students = await after.Students.AsNoTracking().ToListAsync();

        Assert.Equal(StudentsInTheRoster, students.Count);
        Assert.Equal(manualId, students.Single(s => s.StudentNumber == SyntheticRoster.MariaRegNo).Id);
    }

    /// <summary>
    /// The card half of the same handshake.
    ///
    /// <para>
    /// An operator who enrols a student at the desk writes the same two values the importer will later
    /// try to resolve: the student number, and the serial off the card in their hand. The importer's
    /// <c>RfidCardStudentMismatch</c> hard-fails a row whose serial is already active on a different
    /// student — a correct and deliberate refusal — so a manual assignment that stored even a slightly
    /// different UID would fail that student's row on every future import, permanently, with a message
    /// pointing at a card that looks identical to the one in the file. Both paths must therefore agree
    /// on the stored form, and this runs both to prove it rather than comparing two normalizers.
    /// </para>
    ///
    /// <para>
    /// Assigned here in a reader's separator format, which is what a scan-to-enroll screen sends and is
    /// the shape most likely to diverge — and with the serial's leading zeros intact, which is the part
    /// a numeric round trip anywhere on either path would silently destroy.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_manually_assigned_card_is_recognised_by_the_import_rather_than_colliding_with_it()
    {
        var world = await ArrangeAsync();

        Guid manualId, manualCardId;
        await using (var db = NewDbContext())
        {
            var created = await StudentsOn(db).CreateAsync(Request(SyntheticRoster.MariaRegNo));
            manualId = created.Student!.Id;
        }

        await using (var db = NewDbContext())
        {
            var issued = await StudentsOn(db).AddCardAsync(
                manualId, new StudentCardRequest("00-125-033-26", "Scanned at enrolment"));

            Assert.Equal(StudentWriteOutcome.Saved, issued.Outcome);
            Assert.Equal(SyntheticRoster.MariaRfid, issued.Card!.CardUid);
            manualCardId = issued.Card.Id;
        }

        var batch = await ImportAsync(world.TermId);

        Assert.Equal(0, batch.FailedRows);

        await using var read = NewDbContext();

        // One card for this serial, and it is the one the operator issued — not a second one the
        // importer created because it did not recognise the first.
        var cards = await read.RfidCards.AsNoTracking()
            .Where(c => c.CardUid == SyntheticRoster.MariaRfid).ToListAsync();

        Assert.Equal(manualCardId, Assert.Single(cards).Id);
        Assert.Equal(manualId, cards[0].StudentId);
    }

    /// <summary>
    /// Display columns, normalized identically on both paths — <b>asserted by running both</b>.
    ///
    /// <para>
    /// This used to compare the manual path's output against <c>RosterText.Clean</c>, which is the rule
    /// the two paths are <em>believed</em> to share rather than evidence that they do: the importer
    /// never ran, so a divergence introduced on its side would not have failed it. The name promised a
    /// handshake and the body performed half of one.
    /// </para>
    ///
    /// <para>
    /// It now feeds the manual path the <em>same dirty value the roster already carries</em> — Lucia's
    /// first name is prefixed with a byte-order mark in <c>SyntheticRoster</c> — imports that roster,
    /// and asserts the two stored strings are equal to each other as well as to the expected clean
    /// value. Both halves are needed: equality alone would still hold if the two paths broke
    /// identically, and the literal alone would not notice the importer drifting.
    /// </para>
    ///
    /// <para>
    /// The invisible character is spelled <c>\uFEFF</c> rather than embedded, for the reason
    /// <c>RosterText</c> gives about its own set — a literal one is unreadable in every diff, every
    /// editor and every review. That mattered here more than most: the previous version embedded
    /// U+00A0 and U+FEFF next to a doubled ordinary space, so if an editor or a normalizing filter had
    /// stripped either, the doubled space alone would have kept both assertions green and the test
    /// would have silently stopped covering the invisible half.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_manually_created_name_is_normalized_to_exactly_what_the_importer_would_store()
    {
        var world = await ArrangeAsync();

        // Byte-order mark, exactly as SyntheticRoster prefixes Lucia's first name in the workbook.
        const string Dirty = "\uFEFF" + SyntheticRoster.LuciaFirstName;

        Guid manualId;
        await using (var db = NewDbContext())
        {
            var created = await StudentsOn(db).CreateAsync(
                Request("2023-9001", firstName: Dirty, lastName: "Reyes"));

            Assert.Equal(StudentWriteOutcome.Saved, created.Outcome);
            manualId = created.Student!.Id;
        }

        var batch = await ImportAsync(world.TermId);
        Assert.Equal(0, batch.FailedRows);

        await using var read = NewDbContext();

        var byHand = await read.Students.AsNoTracking().SingleAsync(s => s.Id == manualId);
        var imported = await read.Students.AsNoTracking()
            .SingleAsync(s => s.StudentNumber == SyntheticRoster.LuciaRegNo);

        // The handshake: one dirty input, two independent code paths, one stored string.
        Assert.Equal(imported.FirstName, byHand.FirstName);

        // And the value itself, so the equality above cannot pass because both paths broke the same
        // way — for instance if Clean became the identity function on both sides.
        Assert.Equal(SyntheticRoster.LuciaFirstName, byHand.FirstName);
    }

    /// <summary>
    /// <b>An import names a soft-deleted student and updates them without restoring their visibility.</b>
    ///
    /// <para>
    /// Pinned as an observation rather than as a complaint, because there is no obviously right
    /// alternative and the current behaviour is at least conservative: the importer matches on
    /// <c>StudentNumber</c> with no <c>IsDeleted</c> predicate, so a deleted student is updated in place
    /// — new name, new e-mail, a fresh <c>LastSyncedAt</c>, an issued card, term records and enrollments
    /// — and remains invisible to <c>GET /students</c> afterwards.
    /// </para>
    ///
    /// <para>
    /// It is worth a test because <b>Phase 3b-1 is what made it reachable</b>, and because the two
    /// plausible readings point opposite ways: "the SIS is authoritative, so a student it lists is
    /// enrolled and should be restored", or "a deletion is a local decision the SIS knows nothing about
    /// and must not silently undo". Whichever is chosen, the state this test pins — updated, enrolled,
    /// carded, and invisible — is the one nobody chose.
    /// </para>
    /// </summary>
    [Fact]
    public async Task An_import_updates_a_soft_deleted_student_without_making_them_visible_again()
    {
        var world = await ArrangeAsync();

        Guid manualId;
        await using (var db = NewDbContext())
        {
            var created = await StudentsOn(db).CreateAsync(
                Request(SyntheticRoster.MariaRegNo, firstName: "Typed", lastName: "ByHand"));

            manualId = created.Student!.Id;
        }

        await using (var db = NewDbContext())
            Assert.Equal(StudentWriteOutcome.Saved, (await StudentsOn(db).DeleteAsync(manualId)).Outcome);

        var batch = await ImportAsync(world.TermId);
        Assert.Equal(0, batch.FailedRows);

        await using var read = NewDbContext();
        var stored = await read.Students.AsNoTracking().SingleAsync(s => s.Id == manualId);

        // Matched and rewritten by the import...
        Assert.NotEqual("ByHand", stored.LastName);
        Assert.NotNull(stored.LastSyncedAt);
        Assert.True(await read.Enrollments.AsNoTracking().AnyAsync(e => e.StudentId == manualId));

        // ...and still gone from every operator-facing read.
        Assert.True(stored.IsDeleted);
        Assert.Null(await StudentsOn(read).GetAsync(manualId));
        Assert.DoesNotContain(
            (await StudentsOn(read).ListAsync(null, null, null, PageRequest.Default)).Items,
            s => s.Id == manualId);
    }

    // -------------------------------------------------------------------------- the tap path

    /// <summary>
    /// <b>A card assigned through <c>POST /students/{id}/cards</c> records a tap.</b>
    ///
    /// <para>
    /// The end of the only workflow this phase enables on its own: a student who is not in any roster
    /// file is created by hand, handed a card by hand, and walks up to a reader. Every layer of that has
    /// a test except the join between them, and the join is where the two normalizations meet —
    /// <c>AddCardAsync</c> stores <c>CardUid.Normalize(request.CardUid)</c> and <c>TapAsync</c> looks up
    /// <c>CardUid.Normalize(req.CardUid)</c>, from different assemblies, with the reader free to spell
    /// the UID differently at each end.
    /// </para>
    ///
    /// <para>
    /// Assigned in one separator format and tapped in another on purpose. A test using the same spelling
    /// twice would pass even if both sides had stopped normalizing.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_card_assigned_by_hand_records_a_tap_and_the_record_names_that_card()
    {
        var world = await ArrangeAsync();

        Guid eventId;
        await using (var db = NewDbContext())
        {
            var ev = TestData.NewEvent(world.SchoolId);
            db.Events.Add(ev);
            await db.SaveChangesAsync();
            eventId = ev.Id;
        }

        Guid studentId, cardId;
        await using (var db = NewDbContext())
            studentId = (await StudentsOn(db).CreateAsync(Request("2023-0001"))).Student!.Id;

        await using (var db = NewDbContext())
            cardId = (await StudentsOn(db).AddCardAsync(
                studentId, new StudentCardRequest("04:a7:b8:c9", "Primary ID"))).Card!.Id;

        await using (var db = NewDbContext())
        {
            var tap = await AttendanceOn(db).TapAsync(
                new TapRequest(eventId, "04-A7-B8-C9", null, "kiosk-0001", TestData.Now));

            Assert.Equal(TapOutcome.Recorded, tap.Outcome);
            Assert.True(tap.Result.Success);
            Assert.Equal(studentId, tap.Result.Record!.StudentId);
        }

        await using var read = NewDbContext();
        var record = await read.AttendanceRecords.AsNoTracking().SingleAsync();

        Assert.Equal(studentId, record.StudentId);
        // ADR-001 D-3's reason for keeping deactivated rows: the record names the physical card, so
        // the manually issued one has to be the one it points at.
        Assert.Equal(cardId, record.RfidCardId);
        Assert.Equal(AttendanceStatus.Present, record.Status);
    }

    /// <summary>
    /// The reverse: a card deactivated through <c>DELETE /students/{id}/cards/{cardId}</c> stops
    /// recording taps, and the past record that names it survives untouched.
    ///
    /// <para>
    /// This is the property that makes the deactivate endpoint a lost-card response rather than a
    /// cosmetic flag. A found-and-cloned card must stop working <em>immediately</em>, and the attendance
    /// it already produced must not be rewritten by that — the student really was there.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_card_deactivated_by_hand_stops_tapping_and_leaves_its_past_record_alone()
    {
        var world = await ArrangeAsync();

        Guid firstEventId, secondEventId;
        await using (var db = NewDbContext())
        {
            var first = TestData.NewEvent(world.SchoolId);
            // Two hours later rather than a day. Under Phase 4c's D-36 a tap must name a time inside
            // its own event's window *and* not in the future, and TestData.Now is a fixed instant that
            // the real clock has already passed — so `Now.AddDays(1)` is a future timestamp on some
            // days and a past one on others, which would make this test fail on a schedule. Two hours
            // still makes it visibly the later of the two events.
            var second = TestData.NewEvent(world.SchoolId, startAt: TestData.Now.AddHours(2));
            db.Events.AddRange(first, second);
            await db.SaveChangesAsync();
            firstEventId = first.Id;
            secondEventId = second.Id;
        }

        Guid studentId, cardId;
        await using (var db = NewDbContext())
            studentId = (await StudentsOn(db).CreateAsync(Request("2023-0001"))).Student!.Id;

        await using (var db = NewDbContext())
            cardId = (await StudentsOn(db).AddCardAsync(
                studentId, new StudentCardRequest("04A7B8C9", null))).Card!.Id;

        await using (var db = NewDbContext())
        {
            Assert.Equal(TapOutcome.Recorded, (await AttendanceOn(db).TapAsync(
                new TapRequest(firstEventId, "04A7B8C9", null, "before-1", TestData.Now))).Outcome);
        }

        await using (var db = NewDbContext())
            await StudentsOn(db).DeactivateCardAsync(studentId, cardId);

        await using (var db = NewDbContext())
        {
            var refused = await AttendanceOn(db).TapAsync(
                new TapRequest(secondEventId, "04A7B8C9", null, "after-1", TestData.Now.AddHours(2)));

            Assert.Equal(TapOutcome.CardNotFound, refused.Outcome);
        }

        await using var read = NewDbContext();
        var record = Assert.Single(await read.AttendanceRecords.AsNoTracking().ToListAsync());

        Assert.Equal(firstEventId, record.EventId);
        Assert.Equal(cardId, record.RfidCardId);
    }
}
