using EAMS.Application.Abstractions;
using EAMS.Domain;
using EAMS.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace EAMS.Infrastructure.Services;

/// <summary>
/// Projects one term's academic structure into §4.7 <c>StudentGroups</c> — see
/// <see cref="IStudentGroupProjection"/> for what it is for and the three rules it obeys.
///
/// <para>
/// <b>Four group kinds, and why "section" is not the same as "offering".</b> A <c>CourseOffering</c>
/// is one course taught to one section; a <em>section</em> is the cohort itself, which appears across
/// every offering that shares its <c>SectionKey</c> within a term. Both are useful audiences and they
/// are not interchangeable: "everyone in BSFS 2-A" is a cohort event, "everyone in SSCI 7 – BSFS 2-A"
/// is a class. They are projected separately, with distinct
/// <see cref="GroupSourceEntityType"/> values.
/// </para>
///
/// <para>
/// <b>Everything is scoped by an explicit <c>SchoolId</c> predicate</b> rather than relying on the
/// global query filter. The filter is inert whenever no tenant is pinned (the whole of design time,
/// most of the test suite, and any unseeded start), and a projection that silently pulled a second
/// school's colleges into this term's groups would be a cross-tenant leak written into a table the
/// event roster reads. Term ownership is the scope, and it is stated, not assumed.
/// </para>
/// </summary>
internal sealed class StudentGroupProjection : IStudentGroupProjection
{
    private readonly EamsDbContext _db;
    public StudentGroupProjection(EamsDbContext db) => _db = db;

    /// <summary>One group the academic tables say should exist, with the exact membership it should have.</summary>
    private sealed record DesiredGroup(
        string SourceEntityType,
        string SourceKey,
        Guid? SourceEntityId,
        string Type,
        string Name,
        IReadOnlySet<Guid> StudentIds);

    public async Task<GroupProjectionResult> SyncTermAsync(Guid termId, CancellationToken ct = default)
    {
        // IgnoreQueryFilters here and on every read below, and it is safe only because of what
        // replaces it. The projection is addressed by term id by its caller (the Phase 2 importer,
        // which has just written that term) and must behave identically whether or not a tenant
        // happens to be pinned — an import that silently projected nothing because the process had a
        // different school pinned would be very hard to diagnose. The tenant scope is not dropped, it
        // is moved: the term's own SchoolId becomes the explicit predicate on every subsequent query,
        // which is stricter than the ambient filter (that filter is inert whenever nothing is pinned,
        // which is design time and most of the test suite). Compare AttendanceService, where reaching
        // for IgnoreQueryFilters would have been a disclosure bug: there the widened row is returned
        // to an HTTP caller, whereas here it is only ever used to decide which term to scope to.
        var term = await _db.Terms.IgnoreQueryFilters().AsNoTracking()
                       .FirstOrDefaultAsync(t => t.Id == termId, ct)
                   ?? throw new InvalidOperationException(
                       $"Cannot project student groups for term {termId}: no such term. The caller " +
                       "must create the term before importing or projecting anything under it " +
                       "(ADR-001 D-5 makes TermId a required input on an import batch).");

        var desired = await BuildDesiredGroupsAsync(term, ct);
        return await ReconcileAsync(term, desired, ct);
    }

    // ------------------------------------------------------------------ what should exist

    private async Task<IReadOnlyList<DesiredGroup>> BuildDesiredGroupsAsync(Term term, CancellationToken ct)
    {
        var groups = new List<DesiredGroup>();
        groups.AddRange(await BuildCollegeAndProgramGroupsAsync(term, ct));
        groups.AddRange(await BuildSectionAndOfferingGroupsAsync(term, ct));
        return groups;
    }

    /// <summary>
    /// College and programme audiences, read from <c>StudentTermRecords</c> — the per-term
    /// authoritative placement, not <c>Students.Course</c> (ADR-001 D-2: that column is a lossy cache
    /// and would misplace every student who changed programme).
    /// </summary>
    private async Task<IReadOnlyList<DesiredGroup>> BuildCollegeAndProgramGroupsAsync(
        Term term, CancellationToken ct)
    {
        var placements = await _db.StudentTermRecords.IgnoreQueryFilters().AsNoTracking()
            .Where(r => r.TermId == term.Id && r.Student!.SchoolId == term.SchoolId)
            .Select(r => new { r.StudentId, r.CollegeId, r.ProgramId })
            .ToListAsync(ct);

        if (placements.Count == 0) return [];

        var colleges = await _db.Colleges.IgnoreQueryFilters().AsNoTracking()
            .Where(c => c.SchoolId == term.SchoolId)
            .Select(c => new { c.Id, c.Name })
            .ToDictionaryAsync(c => c.Id, c => c.Name, ct);

        var programs = await _db.Programs.IgnoreQueryFilters().AsNoTracking()
            .Where(p => p.SchoolId == term.SchoolId)
            .Select(p => new { p.Id, p.Code })
            .ToDictionaryAsync(p => p.Id, p => p.Code, ct);

        var groups = new List<DesiredGroup>();

        foreach (var byCollege in placements.Where(p => p.CollegeId is not null).GroupBy(p => p.CollegeId!.Value))
        {
            // A placement pointing at a college of another school cannot happen through this code, but
            // if it ever did, skipping is the safe answer: inventing a group named after a row we
            // refused to read would be worse than not projecting it.
            if (!colleges.TryGetValue(byCollege.Key, out var name)) continue;

            groups.Add(new DesiredGroup(
                GroupSourceEntityType.College, byCollege.Key.ToString("D"), byCollege.Key,
                StudentGroupType.College, NameFor(name, term),
                byCollege.Select(p => p.StudentId).ToHashSet()));
        }

        foreach (var byProgram in placements.Where(p => p.ProgramId is not null).GroupBy(p => p.ProgramId!.Value))
        {
            if (!programs.TryGetValue(byProgram.Key, out var code)) continue;

            groups.Add(new DesiredGroup(
                GroupSourceEntityType.Program, byProgram.Key.ToString("D"), byProgram.Key,
                StudentGroupType.Program, NameFor(code, term),
                byProgram.Select(p => p.StudentId).ToHashSet()));
        }

        return groups;
    }

    /// <summary>
    /// Section and per-offering audiences, read from <c>Enrollments</c>.
    ///
    /// <para>
    /// This is the half that <c>Students.Section</c> cannot produce. A student enrolled in offerings
    /// under two different section keys lands in <em>both</em> section groups, which is the correct
    /// answer for 12 of the 52 students in the real roster and is unrepresentable in a single-valued
    /// column.
    /// </para>
    /// </summary>
    private async Task<IReadOnlyList<DesiredGroup>> BuildSectionAndOfferingGroupsAsync(
        Term term, CancellationToken ct)
    {
        var offerings = await _db.CourseOfferings.IgnoreQueryFilters().AsNoTracking()
            .Where(o => o.TermId == term.Id)
            .Select(o => new { o.Id, o.CourseId, o.SectionKey, o.SectionName })
            .ToListAsync(ct);

        if (offerings.Count == 0) return [];

        var offeringIds = offerings.Select(o => o.Id).ToList();

        var enrollments = await _db.Enrollments.IgnoreQueryFilters().AsNoTracking()
            .Where(e => offeringIds.Contains(e.CourseOfferingId) && e.Student!.SchoolId == term.SchoolId)
            .Select(e => new { e.StudentId, e.CourseOfferingId })
            .ToListAsync(ct);

        var courseIds = offerings.Select(o => o.CourseId).Distinct().ToList();
        // The SchoolId predicate is not redundant, even though these ids came from this term's own
        // offerings. Nothing in the schema ties CourseOfferings.CourseId to the term's school — a
        // cross-school offering is representable — and dropping the ambient filter without replacing
        // it is how another tenant's course code would end up pasted into a derived group name, in
        // the very table EventGroups reads. Every other read in this class carries the same guard;
        // this one is the reason the class doc's "explicit SchoolId everywhere" claim is worth
        // stating. A course that fails it simply falls through to the "?" label below.
        var courseCodes = await _db.Courses.IgnoreQueryFilters().AsNoTracking()
            .Where(c => courseIds.Contains(c.Id) && c.SchoolId == term.SchoolId)
            .Select(c => new { c.Id, c.Code })
            .ToDictionaryAsync(c => c.Id, c => c.Code, ct);

        var sectionKeyByOffering = offerings.ToDictionary(o => o.Id, o => o.SectionKey);
        var groups = new List<DesiredGroup>();

        // ---- one group per section key in the term
        //
        // The display label prefers a non-blank SectionName from any offering sharing the key, so the
        // group reads "BSFS 2-A" rather than "BSFS2A". Ordered so the choice is deterministic: two
        // runs must produce the same name or the idempotency contract quietly stops holding.
        var sectionLabels = offerings
            .GroupBy(o => o.SectionKey)
            .ToDictionary(
                g => g.Key,
                g => g.Select(o => o.SectionName)
                      .Where(n => !string.IsNullOrWhiteSpace(n))
                      .OrderBy(n => n, StringComparer.Ordinal)
                      .FirstOrDefault() ?? g.Key);

        // The sentinel is deliberately excluded here, though it is essential one level down.
        //
        // At the *offering* level `(unspecified)` is what stops 39 blank-section rows collapsing into
        // one offering — it means "this particular class, whose section nobody recorded". Lifted to a
        // *section* group it stops meaning that: every blank-section offering across every course and
        // programme unions into a single cohort whose only shared property is that a field was empty.
        // The admin UI would then offer "(unspecified)" as a selectable audience beside "BSFS 2-A",
        // which is not a cohort anyone could have intended to invite.
        //
        // Nothing becomes uninvitable: those students still have their offering group, which is the
        // precise audience. StudentTermRecord.HomeSectionKey draws the same distinction by staying
        // null for "not recorded" — this keeps the projection from erasing it.
        foreach (var bySection in enrollments
                     .Where(e => sectionKeyByOffering.TryGetValue(e.CourseOfferingId, out var key)
                                 && key != AcademicKey.Unspecified)
                     .GroupBy(e => sectionKeyByOffering[e.CourseOfferingId]))
        {
            groups.Add(new DesiredGroup(
                GroupSourceEntityType.Section, bySection.Key,
                // No row to point at — a section is a key shared by many offerings. This is why
                // StudentGroups.SourceKey exists; see GroupSourceEntityType.Section.
                SourceEntityId: null,
                StudentGroupType.Section, NameFor(sectionLabels[bySection.Key], term),
                bySection.Select(e => e.StudentId).ToHashSet()));
        }

        // ---- one group per offering
        var enrolledByOffering = enrollments
            .GroupBy(e => e.CourseOfferingId)
            .ToDictionary(g => g.Key, g => g.Select(e => e.StudentId).ToHashSet());

        foreach (var offering in offerings)
        {
            var label = courseCodes.TryGetValue(offering.CourseId, out var code) ? code : "?";
            var section = string.IsNullOrWhiteSpace(offering.SectionName)
                ? offering.SectionKey
                : offering.SectionName;

            groups.Add(new DesiredGroup(
                GroupSourceEntityType.CourseOffering, offering.Id.ToString("D"), offering.Id,
                StudentGroupType.Course, NameFor($"{label} — {section}", term),
                enrolledByOffering.TryGetValue(offering.Id, out var students) ? students : new HashSet<Guid>()));
        }

        return groups;
    }

    /// <summary>
    /// The term suffix every derived group name carries.
    ///
    /// <para>
    /// Section names repeat every year — <c>BSFS 2-A</c> exists in each of them, against a different
    /// set of students. An <c>EventGroups</c> row from last semester still points at last semester's
    /// group, so without the term in the name an organizer reading a past event's audience sees a name
    /// that now describes a different cohort, and there is nothing on screen to say so.
    /// </para>
    /// </summary>
    private static string NameFor(string label, Term term) =>
        GroupName.Clamp($"{label} ({term.Code})");

    // ------------------------------------------------------------------ set-diff against what exists

    private async Task<GroupProjectionResult> ReconcileAsync(
        Term term, IReadOnlyList<DesiredGroup> desired, CancellationToken ct)
    {
        var existing = await _db.StudentGroups.IgnoreQueryFilters()
            .Include(g => g.Members)
            .Where(g => g.SchoolId == term.SchoolId
                     && g.TermId == term.Id
                     && g.SourceType == GroupSourceType.Derived)
            .ToListAsync(ct);

        var byKey = existing.ToDictionary(g => (g.SourceEntityType, g.SourceKey));
        var now = DateTime.UtcNow;

        var created = 0;
        var updated = 0;
        var membersAdded = 0;
        var membersRemoved = 0;

        foreach (var want in desired)
        {
            if (!byKey.TryGetValue((want.SourceEntityType, want.SourceKey), out var group))
            {
                group = new StudentGroup
                {
                    SchoolId = term.SchoolId,
                    TermId = term.Id,
                    SourceType = GroupSourceType.Derived,
                    SourceEntityType = want.SourceEntityType,
                    SourceEntityId = want.SourceEntityId,
                    SourceKey = want.SourceKey,
                    Type = want.Type,
                    Name = want.Name,
                };
                _db.StudentGroups.Add(group);
                byKey[(want.SourceEntityType, want.SourceKey)] = group;
                created++;
            }
            else if (group.Name != want.Name
                  || group.Type != want.Type
                  || group.SourceEntityId != want.SourceEntityId)
            {
                group.Name = want.Name;
                group.Type = want.Type;
                group.SourceEntityId = want.SourceEntityId;
                group.UpdatedAt = now;
                updated++;
            }

            // Stamped unconditionally, and deliberately not counted as an update: it must be possible
            // to tell "reconciled, nothing changed" from "never reconciled", and if this bumped the
            // counters then a no-op run would report work and IsNoOp would never be true.
            group.LastSyncedAt = now;

            var (added, removed) = DiffMembers(group, want.StudentIds);
            membersAdded += added;
            membersRemoved += removed;
        }

        // Groups whose academic source has gone away: the row survives (deleting it would break any
        // historical EventGroups link, and every FK here is Restrict so it would not even succeed) and
        // loses its derived members. Manual members stay, which is what keeps a hand-curated audience
        // that happens to hang off a retired section alive.
        var wanted = desired.Select(d => (d.SourceEntityType, d.SourceKey)).ToHashSet();
        foreach (var orphan in existing.Where(g => !wanted.Contains((g.SourceEntityType, g.SourceKey))))
        {
            var (_, removed) = DiffMembers(orphan, new HashSet<Guid>());
            membersRemoved += removed;
            orphan.LastSyncedAt = now;
        }

        await _db.SaveChangesAsync(ct);
        return new GroupProjectionResult(created, updated, membersAdded, membersRemoved);
    }

    /// <summary>
    /// Brings one group's membership to the desired set, touching only rows the projection owns.
    ///
    /// <para>
    /// <b>Manual members are read but never written.</b> They are counted as already present, which
    /// matters for more than politeness: <c>UX_StudentGroupMembers_Group_Student</c> is unique on
    /// <c>(StudentGroupId, StudentId)</c>, so adding a derived row for a student who is already a
    /// manual member would not merely duplicate them — it would throw a duplicate-key violation and
    /// abort the whole projection. The rule and the constraint agree.
    /// </para>
    /// </summary>
    private (int Added, int Removed) DiffMembers(StudentGroup group, IReadOnlySet<Guid> desiredStudentIds)
    {
        var present = group.Members.Select(m => m.StudentId).ToHashSet();

        var stale = group.Members
            .Where(m => m.SourceType == GroupSourceType.Derived && !desiredStudentIds.Contains(m.StudentId))
            .ToList();

        foreach (var member in stale)
        {
            _db.StudentGroupMembers.Remove(member);
            group.Members.Remove(member);
        }

        var missing = desiredStudentIds.Where(id => !present.Contains(id)).ToList();
        foreach (var studentId in missing)
        {
            // Added through the DbSet, with the parent set as a *navigation* rather than only as an
            // FK value. Both halves are load-bearing. Adding through the navigation collection alone
            // is the EF trap where a graph member arriving with its Id already populated (the Entity
            // base assigns one in its initializer) can be classified as an existing row and turned
            // into an UPDATE that matches nothing; DbSet.Add states Added unambiguously. Setting the
            // navigation is what lets EF order the insert after a group that is itself still pending.
            _db.StudentGroupMembers.Add(new StudentGroupMember
            {
                StudentGroup = group,
                StudentId = studentId,
                SourceType = GroupSourceType.Derived,
            });
        }

        return (missing.Count, stale.Count);
    }
}
