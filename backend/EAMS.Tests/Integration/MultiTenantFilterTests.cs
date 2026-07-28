using EAMS.Domain;
using EAMS.Tests.Integration.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EAMS.Tests.Integration;

/// <summary>
/// The <c>SchoolId</c> global query filter (Technical Plan §11, installed early by ADR-001 D-6).
///
/// <para>
/// Two things need proving and they pull in opposite directions. <b>Today the filter must hide
/// nothing</b> — one school exists, it is pinned, and every query must still return everything, or
/// the seam has broken the running system to buy a future feature. <b>And it must actually filter</b>
/// — a filter that is installed but inert proves nothing and would let a real cross-tenant leak ship
/// the day a second school appears. The first assertion is the one the brief asks for; the second is
/// what makes the first mean something other than "the filter is switched off".
/// </para>
///
/// <para>
/// The dependent tables get their own tests. Six of them reach a school only through a parent, and a
/// partial tenant filter is the worst version of this feature: it looks installed while leaking the
/// most sensitive rows in the system.
/// </para>
/// </summary>
[Collection(DatabaseCollection.Name)]
public class MultiTenantFilterTests : IntegrationTest
{
    public MultiTenantFilterTests(SqlServerFixture sql) : base(sql) { }

    private sealed record Tenant(Guid SchoolId, Guid EventId, IReadOnlyList<Guid> StudentIds);

    private async Task<Tenant> AddSchoolAsync(string code, int studentCount)
    {
        await using var db = NewDbContext(new TestSchoolContext()); // unpinned: arrange writes freely

        var school = TestData.NewSchool(code);
        db.Schools.Add(school);

        var ev = TestData.NewEvent(school.Id);
        db.Events.Add(ev);

        var studentIds = new List<Guid>();
        for (var i = 1; i <= studentCount; i++)
        {
            var student = TestData.NewStudent(school.Id, $"{code}-{i:0000}", lastName: $"Student{i}");
            db.Students.Add(student);
            db.RfidCards.Add(TestData.NewCard(school.Id, student.Id, $"{code}{i:0000}"));
            db.AttendanceRecords.Add(new AttendanceRecord
            {
                EventId = ev.Id, StudentId = student.Id, CheckInAt = TestData.Now, Status = "Present",
            });
            // §4.8: the student is *invited* as well as recorded. Added in Phase 3a, when
            // EventSummaryDto.Expected stopped being the recorded-row count and became the invited
            // roster — without this the denominator is honestly zero and the summary assertion below
            // would be asserting nothing. It also widens what this file covers: the denominator now
            // joins EventGroups and StudentGroupMembers, which are two more tables the tenant filter
            // has to reach through.
            db.EventGroups.Add(new EventGroup { EventId = ev.Id, StudentId = student.Id });
            studentIds.Add(student.Id);
        }

        await db.SaveChangesAsync();
        return new Tenant(school.Id, ev.Id, studentIds);
    }

    private TestSchoolContext PinnedTo(Guid? schoolId) => new() { CurrentSchoolId = schoolId };

    // ---------------------------------------------------------- the state that ships today

    /// <summary>
    /// The production configuration: exactly one school, pinned at startup. Every query must return
    /// every row — the filter is live but excludes nothing that exists.
    /// </summary>
    [Fact]
    public async Task With_the_only_school_pinned_every_query_still_returns_every_row()
    {
        var usa = await AddSchoolAsync("USA", studentCount: 5);

        await using var db = NewDbContext(PinnedTo(usa.SchoolId));

        Assert.Equal(5, (await StudentsOn(db).ListAsync(null, null, null)).Count);
        Assert.Single(await EventsOn(db).ListAsync(null));
        Assert.Equal(5, (await AttendanceOn(db).ListAsync(null, null, null)).Count);
        Assert.Equal(5, await db.RfidCards.CountAsync());
    }

    [Fact]
    public async Task With_the_only_school_pinned_a_card_still_resolves_to_its_student()
    {
        var usa = await AddSchoolAsync("USA", studentCount: 3);

        await using var db = NewDbContext(PinnedTo(usa.SchoolId));
        var student = await StudentsOn(db).GetByCardUidAsync("usa-0002");

        Assert.NotNull(student);
        Assert.Equal("USA-0002", student!.StudentNumber);
    }

    [Fact]
    public async Task With_the_only_school_pinned_the_event_summary_counts_every_attendee()
    {
        var usa = await AddSchoolAsync("USA", studentCount: 4);

        await using var db = NewDbContext(PinnedTo(usa.SchoolId));
        var summary = await EventsOn(db).GetSummaryAsync(usa.EventId);

        Assert.NotNull(summary);
        Assert.Equal(4, summary!.Present);
        Assert.Equal(4, summary.Expected);
    }

    /// <summary>
    /// The unpinned state, which is what an unseeded or design-time context sees. It must behave
    /// exactly as if no filter existed — a null tenant that returned zero rows would be a silent,
    /// baffling failure at exactly the moment someone is bootstrapping an empty database.
    /// </summary>
    [Fact]
    public async Task With_no_tenant_pinned_nothing_is_filtered()
    {
        await AddSchoolAsync("USA", studentCount: 3);
        await AddSchoolAsync("CICSS", studentCount: 2);

        await using var db = NewDbContext(PinnedTo(null));

        Assert.Equal(5, (await StudentsOn(db).ListAsync(null, null, null)).Count);
        Assert.Equal(2, (await EventsOn(db).ListAsync(null)).Count);
        Assert.Equal(5, (await AttendanceOn(db).ListAsync(null, null, null)).Count);
    }

    // ---------------------------------------------------------- proof the filter is not inert

    [Fact]
    public async Task A_pinned_tenant_sees_only_its_own_owned_rows()
    {
        var usa = await AddSchoolAsync("USA", studentCount: 3);
        await AddSchoolAsync("CICSS", studentCount: 2);

        await using var db = NewDbContext(PinnedTo(usa.SchoolId));
        var students = await StudentsOn(db).ListAsync(null, null, null);

        Assert.Equal(3, students.Count);
        Assert.All(students, s => Assert.StartsWith("USA-", s.StudentNumber));
        Assert.Single(await EventsOn(db).ListAsync(null));
    }

    /// <summary>
    /// <c>AttendanceRecords</c> reaches a school only through its <c>Event</c>. This is the row that
    /// matters most: without the through-the-parent filter EF would return the other tenant's
    /// attendance with a null <c>Event</c> navigation rather than not returning it at all.
    /// </summary>
    [Fact]
    public async Task A_pinned_tenant_cannot_see_another_schools_attendance_through_the_dependent_filter()
    {
        var usa = await AddSchoolAsync("USA", studentCount: 3);
        var cicss = await AddSchoolAsync("CICSS", studentCount: 2);

        await using var db = NewDbContext(PinnedTo(usa.SchoolId));

        Assert.Equal(3, (await AttendanceOn(db).ListAsync(null, null, null)).Count);
        Assert.Empty(await AttendanceOn(db).ListAsync(cicss.EventId, null, null));
        Assert.Null(await EventsOn(db).GetAsync(cicss.EventId));
        Assert.Null(await EventsOn(db).GetSummaryAsync(cicss.EventId));
    }

    /// <summary>
    /// The tap hot path resolves a UID without knowing the school. Once a tenant is pinned, another
    /// school's card must not resolve — otherwise a reader in one campus could check a student into
    /// another campus's event.
    /// </summary>
    [Fact]
    public async Task A_pinned_tenant_cannot_resolve_another_schools_card()
    {
        var usa = await AddSchoolAsync("USA", studentCount: 2);
        await AddSchoolAsync("CICSS", studentCount: 2);

        await using var db = NewDbContext(PinnedTo(usa.SchoolId));

        Assert.NotNull(await StudentsOn(db).GetByCardUidAsync("USA0001"));
        Assert.Null(await StudentsOn(db).GetByCardUidAsync("CICSS0001"));
    }

    /// <summary>
    /// §4.13: a NULL <c>SchoolId</c> on <c>SystemSettings</c> is the <em>global</em> scope, not an
    /// untenanted row. It has to stay visible under every tenant or reader defaults, grace minutes
    /// and the retention policy disappear the moment authentication is switched on.
    /// </summary>
    [Fact]
    public async Task Global_system_settings_stay_visible_to_a_pinned_tenant()
    {
        var usa = await AddSchoolAsync("USA", studentCount: 1);
        var cicss = await AddSchoolAsync("CICSS", studentCount: 1);

        await using (var seed = NewDbContext(PinnedTo(null)))
        {
            seed.SystemSettings.Add(new SystemSetting { SchoolId = null, Key = "grace.minutes", Value = "15" });
            seed.SystemSettings.Add(new SystemSetting { SchoolId = usa.SchoolId, Key = "grace.minutes", Value = "20" });
            seed.SystemSettings.Add(new SystemSetting { SchoolId = cicss.SchoolId, Key = "grace.minutes", Value = "25" });
            await seed.SaveChangesAsync();
        }

        await using var db = NewDbContext(PinnedTo(usa.SchoolId));
        var visible = await db.SystemSettings.AsNoTracking().ToListAsync();

        Assert.Equal(2, visible.Count);
        Assert.Contains(visible, s => s.SchoolId is null);
        Assert.Contains(visible, s => s.SchoolId == usa.SchoolId);
        Assert.DoesNotContain(visible, s => s.SchoolId == cicss.SchoolId);
    }

    /// <summary>
    /// <c>Schools</c> is the tenant root and is deliberately unfiltered — tenant selection has to be
    /// able to enumerate them. Pinned so the exclusion stays a decision rather than an oversight.
    /// </summary>
    [Fact]
    public async Task The_schools_table_itself_is_never_filtered()
    {
        var usa = await AddSchoolAsync("USA", studentCount: 1);
        await AddSchoolAsync("CICSS", studentCount: 1);

        await using var db = NewDbContext(PinnedTo(usa.SchoolId));

        Assert.Equal(2, await db.Schools.CountAsync());
    }
}
