using EAMS.Application.Abstractions;
using EAMS.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace EAMS.Infrastructure.Data;

/// <summary>
/// The §4 baseline schema. Column names, types, nullability, defaults and indexes follow the
/// per-table lists in Technical Plan §4.2–§4.13 literally, so the migration stays independently
/// verifiable against the document. Approved divergence is limited to ADR-001 D-3 and is marked
/// inline; anything else marked "not in §4" is a mechanical necessity (SQL Server semantics) and
/// is listed in the Phase 0b report.
/// </summary>
internal class EamsDbContext : DbContext
{
    // Read by the SchoolId global query filters below. EF re-evaluates DbContext instance members on
    // every query execution and passes them as parameters, so swapping the registered implementation
    // in Phase 6 changes every query's tenant without touching a line of entity configuration.
    private readonly ISchoolContext _school;

    public EamsDbContext(DbContextOptions<EamsDbContext> options, ISchoolContext school)
        : base(options) => _school = school;

    public DbSet<School> Schools => Set<School>();
    public DbSet<Student> Students => Set<Student>();
    public DbSet<RfidCard> RfidCards => Set<RfidCard>();
    public DbSet<Event> Events => Set<Event>();
    public DbSet<EventSchedule> EventSchedules => Set<EventSchedule>();
    public DbSet<StudentGroup> StudentGroups => Set<StudentGroup>();
    public DbSet<StudentGroupMember> StudentGroupMembers => Set<StudentGroupMember>();
    public DbSet<EventGroup> EventGroups => Set<EventGroup>();
    public DbSet<AttendanceRecord> AttendanceRecords => Set<AttendanceRecord>();
    public DbSet<Device> Devices => Set<Device>();
    public DbSet<User> Users => Set<User>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<Permission> Permissions => Set<Permission>();
    public DbSet<UserRole> UserRoles => Set<UserRole>();
    public DbSet<RolePermission> RolePermissions => Set<RolePermission>();

    /// <summary>
    /// §11 login's rotating refresh tokens. Additive — not a §4.11 table; see
    /// <see cref="RefreshToken"/> for why one <c>Users.RefreshTokenHash</c> column could not express
    /// it, and why that column is kept and left permanently NULL rather than dropped.
    /// </summary>
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<SisImportBatch> SisImportBatches => Set<SisImportBatch>();
    public DbSet<SisImportRow> SisImportRows => Set<SisImportRow>();

    // ADR-001 D-1's three remaining tables, all import-side, plus the D-4/D-5 columns on the two
    // above. See SisImportEntities.cs for why each exists and what §4.12 could not express.
    public DbSet<SisImportRowEntity> SisImportRowEntities => Set<SisImportRowEntity>();
    public DbSet<SisImportProfile> SisImportProfiles => Set<SisImportProfile>();
    public DbSet<SisImportProfileColumn> SisImportProfileColumns => Set<SisImportProfileColumn>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<SystemSetting> SystemSettings => Set<SystemSetting>();

    // ADR-001 D-1 academic layer. Additive; nothing above changes shape because of them.
    public DbSet<Term> Terms => Set<Term>();
    public DbSet<College> Colleges => Set<College>();
    public DbSet<AcademicProgram> Programs => Set<AcademicProgram>();
    public DbSet<Course> Courses => Set<Course>();
    public DbSet<Instructor> Instructors => Set<Instructor>();
    public DbSet<CourseOffering> CourseOfferings => Set<CourseOffering>();
    public DbSet<CourseOfferingInstructor> CourseOfferingInstructors => Set<CourseOfferingInstructor>();
    public DbSet<Enrollment> Enrollments => Set<Enrollment>();
    public DbSet<StudentTermRecord> StudentTermRecords => Set<StudentTermRecord>();

    /// <summary>
    /// Every <c>DateTime</c> in the model round-trips as UTC. Applied as a convention rather than
    /// per property so entities added later (the ADR-001 D-1 academic layer) inherit it by default
    /// instead of by remembering — the failure mode this closes is silent and invisible in the
    /// payload, so it must not depend on anyone noticing. See <see cref="UtcDateTimeConverter"/>.
    /// </summary>
    // ------------------------------------------------------------------ ADR-001 D-2 cache guard

    /// <summary>
    /// Set only for the duration of <see cref="BeginAcademicCacheRefresh"/>. Instance state on a
    /// scoped context, so it cannot leak between requests.
    /// </summary>
    private bool _academicCacheRefreshInProgress;

    /// <summary>
    /// The one door through which <c>Students.Course</c> / <c>YearLevel</c> / <c>Section</c> may be
    /// written (ADR-001 D-2).
    ///
    /// <para>
    /// <b>Why a door exists at all rather than the columns being frozen.</b> They are a <em>cache</em>,
    /// not a dead column — something has to refresh them from <c>Enrollments</c> /
    /// <c>StudentTermRecords</c> eventually, and the refresh trigger is still an open ADR-001
    /// follow-up. Without a named seam, the first person who needs to write them would delete the
    /// guard, and the rule would be gone rather than narrowed. With one, the write path is greppable:
    /// every legitimate writer names this method.
    /// </para>
    ///
    /// <para>
    /// Nothing calls it today. That is the correct state — no refresher has been written and none was
    /// in scope — and it is asserted by the guard tests, so the day one appears it is a visible,
    /// reviewable change rather than a quiet one.
    /// </para>
    /// </summary>
    internal IDisposable BeginAcademicCacheRefresh() => new AcademicCacheRefreshScope(this);

    private sealed class AcademicCacheRefreshScope : IDisposable
    {
        private readonly EamsDbContext _context;

        public AcademicCacheRefreshScope(EamsDbContext context)
        {
            _context = context;
            _context._academicCacheRefreshInProgress = true;
        }

        public void Dispose() => _context._academicCacheRefreshInProgress = false;
    }

    /// <summary>
    /// Refuses any update that changes one of the ADR-001 D-2 derived cache columns.
    ///
    /// <para>
    /// <b>It sits in <c>SaveChanges</c> rather than in a service because that is the only place it
    /// cannot be routed around.</b> There is no student write endpoint today, so a guard in a service
    /// would be a guard on a path nobody takes — and the writer this rule is actually aimed at is the
    /// Phase 2 importer, which has every reason to set <c>Section</c> because the source column is
    /// sitting right there in the spreadsheet row. Whatever code writes it, it goes through here.
    /// </para>
    ///
    /// <para>
    /// <b>Inserts are deliberately allowed.</b> The columns are populated by the §4 seed and by
    /// existing fixtures, and a new student row carrying a display value is not the failure mode — the
    /// failure mode is the two sources of truth <em>diverging</em> over time, which requires an update.
    /// Blocking inserts would also mean rewriting seed and test data to buy nothing.
    /// </para>
    ///
    /// <para>
    /// <b><c>DetectChanges</c> is called first.</b> <c>SaveChangesAsync</c> runs it internally, but
    /// only after this method would have looked — so without the explicit call the guard would inspect
    /// a change tracker that had not yet noticed the assignment, and would pass everything.
    /// </para>
    /// </summary>
    private void GuardDerivedAcademicFields()
    {
        if (_academicCacheRefreshInProgress) return;

        ChangeTracker.DetectChanges();

        foreach (var entry in ChangeTracker.Entries<Student>())
        {
            if (entry.State != EntityState.Modified) continue;

            foreach (var propertyName in Student.DerivedAcademicPropertyNames)
            {
                var property = entry.Property(propertyName);
                if (!property.IsModified) continue;

                // Deliberately NOT skipped when CurrentValue equals OriginalValue.
                //
                // That comparison looks like a harmless "ignore a no-op assignment" check and is
                // actually a hole. Attaching a detached entity — `db.Students.Update(student)`, the
                // shape a REST PUT handler takes — populates OriginalValues *from the entity's own
                // current values*, because no snapshot exists. So a genuinely changed Section arrives
                // with Current == Original and would be waved straight through, silently, on the one
                // path most likely to carry it.
                //
                // Nothing is lost by removing it: for a tracked entity a true no-op leaves State
                // Unchanged and IsModified false, so the checks above already absorb it. The only
                // case the comparison uniquely covered was Update() on an already-tracked entity,
                // which nothing does — and for a guard, a loud false positive on a path nobody takes
                // beats a silent false negative on the path Phase 3 will build.
                //
                // Known limits, stated so they are not mistaken for coverage: ExecuteUpdateAsync and
                // raw SQL bypass the change tracker entirely and therefore bypass this guard.
                throw new AcademicCacheWriteException(propertyName, entry.Entity.StudentNumber);
            }
        }
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        GuardDerivedAcademicFields();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(
        bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        GuardDerivedAcademicFields();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        configurationBuilder.Properties<DateTime>().HaveConversion<UtcDateTimeConverter>();
        configurationBuilder.Properties<DateTime?>().HaveConversion<NullableUtcDateTimeConverter>();
    }

    protected override void OnModelCreating(ModelBuilder b)
    {
        ConfigureSchools(b);
        ConfigureStudents(b);
        ConfigureRfidCards(b);
        ConfigureEvents(b);
        ConfigureEventSchedules(b);
        ConfigureStudentGroups(b);
        ConfigureEventGroups(b);
        ConfigureAttendanceRecords(b);
        ConfigureDevices(b);
        ConfigureRbac(b);
        ConfigureRefreshTokens(b);
        ConfigureSisImport(b);
        ConfigureSystem(b);
        ConfigureAcademicStructure(b);
        ConfigureSchoolIdQueryFilters(b);

        // Hard deletes are never issued by the application (§4 uses IsDeleted soft delete), and an
        // attendance trail must not disappear behind a cascade. SQL Server also rejects the
        // multiple cascade paths this shape produces (Schools reaches most tables two ways).
        // Restrict everywhere: a delete that would orphan rows fails loudly instead.
        foreach (var fk in b.Model.GetEntityTypes().SelectMany(t => t.GetForeignKeys()))
            fk.DeleteBehavior = DeleteBehavior.Restrict;
    }

    /// <summary>
    /// §11's multi-tenant guard (ADR-001 D-6), applied to every entity that owns a <c>SchoolId</c>
    /// column. Instance method, not static: the filter has to close over <see cref="_school"/>.
    ///
    /// <para>
    /// <b>Active now, not merely declared.</b> A filter that is installed but never evaluated proves
    /// nothing — the failure modes it introduces (a filtered <c>Include</c>, an aggregate that no
    /// longer matches a raw <c>COUNT</c>) only appear once it runs, and discovering them in Phase 6
    /// alongside a new auth stack is the worst possible time. In development
    /// <c>DevelopmentSchoolContext</c> pins the single seeded school, so the filter is exercised on
    /// every request while excluding nothing that exists.
    /// </para>
    ///
    /// <para>
    /// <b>The null branch is why this is safe to turn on.</b> With no tenant resolved the predicate
    /// is trivially true and behaviour is exactly as before this change — an unseeded or
    /// design-time context cannot mysteriously return zero rows. Startup logs which of the two
    /// states is in force and, when several schools exist, that the unpinned ones are hidden. The
    /// rule being honoured: no data disappears without the log saying so. Phase 6 should drop the
    /// null branch once an unauthenticated request can no longer reach a query — it also costs a
    /// non-sargable <c>@p IS NULL OR [SchoolId] = @p</c> in every plan.
    /// </para>
    ///
    /// <para>
    /// <b>Dependents are filtered through their owner, not left open.</b> Six tables reach a school
    /// only via a parent. Filtering just the parents makes EF emit
    /// <c>PossibleIncorrectRequiredNavigationWithQueryFilterInteraction</c> for each, and the
    /// warning is correct: a second tenant's <c>AttendanceRecord</c> would still be returned, merely
    /// with its required <c>Event</c> navigation materialized as null. A partial tenant filter on
    /// the attendance table is the worst version of this feature — it looks installed and leaks the
    /// most sensitive rows in the system. They cost a seek on the parent's primary key per row, and
    /// only while a tenant is pinned; that is worth paying, and it keeps the startup log down to the
    /// two warnings that are meant to be read.
    /// </para>
    ///
    /// <para>
    /// Deliberately <em>not</em> filtered: <c>Schools</c> itself (the tenant root — it has no
    /// <c>SchoolId</c>, and tenant selection has to be able to see it); <c>Roles</c>,
    /// <c>Permissions</c> and <c>RolePermissions</c> (global by design in §4.11, not tenant data);
    /// and <c>AuditLogs</c>, which now owns a nullable <c>SchoolId</c> of its own (§11 groundwork)
    /// but is still unfiltered: a NULL there means "not attributed to a school" and covers every
    /// system-generated entry, so the filter has to be written as a three-way predicate like
    /// <c>SystemSettings</c>' rather than the two-way one every owner above uses, and that belongs
    /// with the rest of §11 enforcement rather than here.
    /// </para>
    ///
    /// <para>
    /// <c>RefreshTokens</c> joins that list, and for a stronger reason than the others: a token is
    /// redeemed <em>before</em> any tenant is resolved, so a filter would not merely be premature, it
    /// would hide every row from the only query that reads them. See
    /// <c>ConfigureRefreshTokens</c>. This list is the known gap, recorded rather than assumed away.
    /// </para>
    /// </summary>
    private void ConfigureSchoolIdQueryFilters(ModelBuilder b)
    {
        // Owners: the SchoolId column §11 names.
        b.Entity<Student>().HasQueryFilter(
            x => _school.CurrentSchoolId == null || x.SchoolId == _school.CurrentSchoolId);
        b.Entity<RfidCard>().HasQueryFilter(
            x => _school.CurrentSchoolId == null || x.SchoolId == _school.CurrentSchoolId);
        b.Entity<Event>().HasQueryFilter(
            x => _school.CurrentSchoolId == null || x.SchoolId == _school.CurrentSchoolId);
        b.Entity<StudentGroup>().HasQueryFilter(
            x => _school.CurrentSchoolId == null || x.SchoolId == _school.CurrentSchoolId);
        b.Entity<Device>().HasQueryFilter(
            x => _school.CurrentSchoolId == null || x.SchoolId == _school.CurrentSchoolId);
        b.Entity<User>().HasQueryFilter(
            x => _school.CurrentSchoolId == null || x.SchoolId == _school.CurrentSchoolId);
        b.Entity<SisImportBatch>().HasQueryFilter(
            x => _school.CurrentSchoolId == null || x.SchoolId == _school.CurrentSchoolId);

        // §4.13: a NULL SchoolId on SystemSettings is the *global* scope, not an untenanted row.
        // It must stay visible under every tenant or reader defaults and retention policy vanish.
        b.Entity<SystemSetting>().HasQueryFilter(
            x => _school.CurrentSchoolId == null
              || x.SchoolId == null
              || x.SchoolId == _school.CurrentSchoolId);

        // Dependents: same predicate, reached through the owning parent. The null-forgiving `!` is
        // safe on each — every one of these navigations is configured IsRequired() above, so the
        // nullable CLR property is a modelling artifact, not a real possibility.
        // AttendanceRecords owns its SchoolId as of Phase 4a design D-35 and is filtered on the column
        // directly rather than through Event. Two reasons, and the first is the one that matters: the
        // filter and UX_Attendance_Device_DeviceTapId now express the *same* predicate over the *same*
        // column, which is the entire point of denormalizing it — see the index for what their
        // disagreeing used to cost. The second is that it removes a seek on Events per row from the
        // busiest table in the system.
        b.Entity<AttendanceRecord>().HasQueryFilter(
            x => _school.CurrentSchoolId == null || x.SchoolId == _school.CurrentSchoolId);
        b.Entity<EventSchedule>().HasQueryFilter(
            x => _school.CurrentSchoolId == null || x.Event!.SchoolId == _school.CurrentSchoolId);
        b.Entity<EventGroup>().HasQueryFilter(
            x => _school.CurrentSchoolId == null || x.Event!.SchoolId == _school.CurrentSchoolId);
        b.Entity<StudentGroupMember>().HasQueryFilter(
            x => _school.CurrentSchoolId == null
              || x.StudentGroup!.SchoolId == _school.CurrentSchoolId);
        b.Entity<SisImportRow>().HasQueryFilter(
            x => _school.CurrentSchoolId == null || x.Batch!.SchoolId == _school.CurrentSchoolId);

        // The three ADR-001 D-1 import tables. A roster import batch is the most complete description
        // of a tenant's people that passes through this system, and its staged rows hold the source
        // verbatim — including names and institutional e-mail addresses — so none of the three is left
        // unfiltered. SisImportProfiles owns a SchoolId; the other two reach one through a required
        // parent, exactly as the dependents above do.
        b.Entity<SisImportProfile>().HasQueryFilter(
            x => _school.CurrentSchoolId == null || x.SchoolId == _school.CurrentSchoolId);
        b.Entity<SisImportProfileColumn>().HasQueryFilter(
            x => _school.CurrentSchoolId == null || x.Profile!.SchoolId == _school.CurrentSchoolId);
        b.Entity<SisImportRowEntity>().HasQueryFilter(
            x => _school.CurrentSchoolId == null
              || x.SisImportRow!.Batch!.SchoolId == _school.CurrentSchoolId);
        b.Entity<UserRole>().HasQueryFilter(
            x => _school.CurrentSchoolId == null || x.User!.SchoolId == _school.CurrentSchoolId);

        // ADR-001 D-1 academic layer. Five of the nine own a SchoolId column; the other four reach a
        // school through a required parent, exactly as the six dependents above do. None is left
        // unfiltered — a roster is the most complete description of a tenant's people that exists,
        // and a partial tenant filter over it would be the same "looks installed, leaks the sensitive
        // rows" failure this block was written to avoid.
        b.Entity<Term>().HasQueryFilter(
            x => _school.CurrentSchoolId == null || x.SchoolId == _school.CurrentSchoolId);
        b.Entity<College>().HasQueryFilter(
            x => _school.CurrentSchoolId == null || x.SchoolId == _school.CurrentSchoolId);
        b.Entity<AcademicProgram>().HasQueryFilter(
            x => _school.CurrentSchoolId == null || x.SchoolId == _school.CurrentSchoolId);
        b.Entity<Course>().HasQueryFilter(
            x => _school.CurrentSchoolId == null || x.SchoolId == _school.CurrentSchoolId);
        b.Entity<Instructor>().HasQueryFilter(
            x => _school.CurrentSchoolId == null || x.SchoolId == _school.CurrentSchoolId);

        // CourseOfferings reach a school through their Term rather than by denormalizing SchoolId,
        // because unlike RfidCards (ADR-001 D-3) there is no index that needs it: the natural key
        // (TermId, CourseId, SectionKey) is already tenant-scoped through TermId.
        b.Entity<CourseOffering>().HasQueryFilter(
            x => _school.CurrentSchoolId == null || x.Term!.SchoolId == _school.CurrentSchoolId);
        b.Entity<CourseOfferingInstructor>().HasQueryFilter(
            x => _school.CurrentSchoolId == null
              || x.CourseOffering!.Term!.SchoolId == _school.CurrentSchoolId);

        // Enrollments and StudentTermRecords go through Student, which owns a SchoolId directly —
        // one join instead of the two the offering path would cost.
        b.Entity<Enrollment>().HasQueryFilter(
            x => _school.CurrentSchoolId == null || x.Student!.SchoolId == _school.CurrentSchoolId);
        b.Entity<StudentTermRecord>().HasQueryFilter(
            x => _school.CurrentSchoolId == null || x.Student!.SchoolId == _school.CurrentSchoolId);
    }

    /// <summary>
    /// ADR-001 D-1's nine academic tables.
    ///
    /// <para>
    /// <b>Every unique index below declares <c>HasFilter</c> explicitly, including where the answer is
    /// null.</b> The SQL Server provider silently appends <c>IS NOT NULL</c> to any unique index that
    /// covers a nullable column, and that defect is not hypothetical here: it shipped once on
    /// <c>UX_Attendance_Event_Student_Occurrence</c> and left every non-recurring attendance row —
    /// which is all of them — completely unconstrained, while the index still appeared in every schema
    /// diff. The indexes here are the roster's only defence against duplicate courses, duplicate
    /// sections and duplicate enrollments, so each one's filter is stated at the call site and
    /// asserted against <c>sys.indexes.filter_definition</c> in <c>AcademicSchemaTests</c>.
    /// </para>
    ///
    /// <para>
    /// <b>Every natural-key component is NOT NULL.</b> See <see cref="AcademicKey.Unspecified"/> for
    /// the sentinel and why a nullable key column would cap a table at one blank row.
    /// </para>
    /// </summary>
    private static void ConfigureAcademicStructure(ModelBuilder b)
    {
        b.Entity<Term>(e =>
        {
            e.ToTable("Terms");
            e.Property(x => x.Code).HasMaxLength(50).IsRequired();
            e.Property(x => x.SchoolYear).HasMaxLength(20).IsRequired();
            e.Property(x => x.Semester).HasMaxLength(30).IsRequired();
            e.Property(x => x.IsCurrent).HasDefaultValue(false).ValueGeneratedNever();

            e.HasOne(x => x.School).WithMany().HasForeignKey(x => x.SchoolId).IsRequired();

            e.HasIndex(x => new { x.SchoolId, x.Code }).IsUnique()
                .HasFilter(null)
                .HasDatabaseName("UX_Terms_SchoolId_Code");

            // "Current" is a claim about the school, not about the row, so it is enforced where it can
            // actually hold. Two current terms would make every term-defaulting query pick one
            // arbitrarily, and the wrong roster would be silently correct-looking.
            e.HasIndex(x => x.SchoolId).IsUnique()
                .HasFilter("[IsCurrent] = 1")
                .HasDatabaseName("UX_Terms_SchoolId_Current");
        });

        b.Entity<College>(e =>
        {
            e.ToTable("Colleges");
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.NameKey).HasMaxLength(AcademicKey.MaxLength).IsRequired();
            e.Property(x => x.Code).HasMaxLength(30);

            e.HasOne(x => x.School).WithMany().HasForeignKey(x => x.SchoolId).IsRequired();

            e.HasIndex(x => new { x.SchoolId, x.NameKey }).IsUnique()
                .HasFilter(null)
                .HasDatabaseName("UX_Colleges_SchoolId_NameKey");
        });

        b.Entity<AcademicProgram>(e =>
        {
            e.ToTable("Programs"); // CLR type renamed to avoid colliding with the API's Program class.
            e.Property(x => x.Code).HasMaxLength(50).IsRequired();
            e.Property(x => x.CodeKey).HasMaxLength(AcademicKey.MaxLength).IsRequired();
            e.Property(x => x.Name).HasMaxLength(200);

            e.HasOne(x => x.School).WithMany().HasForeignKey(x => x.SchoolId).IsRequired();
            e.HasOne(x => x.College).WithMany(c => c.Programs).HasForeignKey(x => x.CollegeId).IsRequired();

            e.HasIndex(x => new { x.SchoolId, x.CodeKey }).IsUnique()
                .HasFilter(null)
                .HasDatabaseName("UX_Programs_SchoolId_CodeKey");

            e.HasIndex(x => x.CollegeId).HasDatabaseName("IX_Programs_CollegeId");
        });

        b.Entity<Course>(e =>
        {
            e.ToTable("Courses");
            e.Property(x => x.Code).HasMaxLength(50).IsRequired();
            e.Property(x => x.CodeKey).HasMaxLength(AcademicKey.MaxLength).IsRequired();
            e.Property(x => x.Title).HasMaxLength(300);

            e.HasOne(x => x.School).WithMany().HasForeignKey(x => x.SchoolId).IsRequired();

            // Nullable and NOT part of the key today — see the Course type remarks for the widening to
            // UNIQUE(SchoolId, CollegeId, CodeKey) this column exists to keep cheap.
            e.HasOne(x => x.College).WithMany().HasForeignKey(x => x.CollegeId);

            e.HasIndex(x => new { x.SchoolId, x.CodeKey }).IsUnique()
                .HasFilter(null)
                .HasDatabaseName("UX_Courses_SchoolId_CodeKey");
        });

        b.Entity<Instructor>(e =>
        {
            e.ToTable("Instructors", t => t.HasCheckConstraint(
                "CK_Instructors_NoSelfMerge",
                "[MergedIntoInstructorId] IS NULL OR [MergedIntoInstructorId] <> [Id]"));

            e.Property(x => x.DisplayName).HasMaxLength(200).IsRequired();
            e.Property(x => x.NameKey).HasMaxLength(AcademicKey.MaxLength).IsRequired();
            e.Property(x => x.ExternalId).HasMaxLength(100);

            e.HasOne(x => x.School).WithMany().HasForeignKey(x => x.SchoolId).IsRequired();
            e.HasOne(x => x.MergedIntoInstructor).WithMany()
                .HasForeignKey(x => x.MergedIntoInstructorId);

            e.HasIndex(x => new { x.SchoolId, x.NameKey }).IsUnique()
                .HasFilter(null)
                .HasDatabaseName("UX_Instructors_SchoolId_NameKey");

            // Unique only where present. Without the filter, SQL Server's NULL-equals-NULL inside a
            // unique index would permit exactly one instructor with no SIS id — which is every
            // instructor today. Same reading as §4.10's nullable Devices.ApiKey.
            e.HasIndex(x => new { x.SchoolId, x.ExternalId }).IsUnique()
                .HasFilter("[ExternalId] IS NOT NULL")
                .HasDatabaseName("UX_Instructors_SchoolId_ExternalId");
        });

        b.Entity<CourseOffering>(e =>
        {
            e.ToTable("CourseOfferings");
            e.Property(x => x.SectionKey).HasMaxLength(AcademicKey.MaxLength).IsRequired()
                .HasDefaultValue(AcademicKey.Unspecified).ValueGeneratedNever();
            e.Property(x => x.SectionName).HasMaxLength(100);

            e.HasOne(x => x.Term).WithMany().HasForeignKey(x => x.TermId).IsRequired();
            e.HasOne(x => x.Course).WithMany(c => c.Offerings).HasForeignKey(x => x.CourseId).IsRequired();

            e.HasIndex(x => new { x.TermId, x.CourseId, x.SectionKey }).IsUnique()
                .HasFilter(null)
                .HasDatabaseName("UX_CourseOfferings_Term_Course_Section");

            // The projection's section grouping and every "who is in this section" report scan a term
            // for a section key; without this they scan the whole table.
            e.HasIndex(x => new { x.TermId, x.SectionKey })
                .HasDatabaseName("IX_CourseOfferings_TermId_SectionKey");
        });

        b.Entity<CourseOfferingInstructor>(e =>
        {
            e.ToTable("CourseOfferingInstructors");

            e.HasOne(x => x.CourseOffering).WithMany(o => o.Instructors)
                .HasForeignKey(x => x.CourseOfferingId).IsRequired();
            e.HasOne(x => x.Instructor).WithMany().HasForeignKey(x => x.InstructorId).IsRequired();

            e.HasIndex(x => new { x.CourseOfferingId, x.InstructorId }).IsUnique()
                .HasFilter(null)
                .HasDatabaseName("UX_CourseOfferingInstructors_Offering_Instructor");
        });

        b.Entity<Enrollment>(e =>
        {
            e.ToTable("Enrollments");

            e.HasOne(x => x.Student).WithMany(s => s.Enrollments).HasForeignKey(x => x.StudentId).IsRequired();
            e.HasOne(x => x.CourseOffering).WithMany(o => o.Enrollments)
                .HasForeignKey(x => x.CourseOfferingId).IsRequired();

            e.HasIndex(x => new { x.StudentId, x.CourseOfferingId }).IsUnique()
                .HasFilter(null)
                .HasDatabaseName("UX_Enrollments_Student_CourseOffering");

            // "Who is enrolled in this offering" is the projection's inner loop and the roster query.
            e.HasIndex(x => x.CourseOfferingId).HasDatabaseName("IX_Enrollments_CourseOfferingId");
        });

        b.Entity<StudentTermRecord>(e =>
        {
            e.ToTable("StudentTermRecords");
            e.Property(x => x.YearLevel).HasMaxLength(50);
            e.Property(x => x.HomeSectionKey).HasMaxLength(AcademicKey.MaxLength);
            e.Property(x => x.HomeSectionName).HasMaxLength(100);

            e.HasOne(x => x.Student).WithMany(s => s.TermRecords).HasForeignKey(x => x.StudentId).IsRequired();
            e.HasOne(x => x.Term).WithMany().HasForeignKey(x => x.TermId).IsRequired();
            e.HasOne(x => x.Program).WithMany().HasForeignKey(x => x.ProgramId);
            e.HasOne(x => x.College).WithMany().HasForeignKey(x => x.CollegeId);

            e.HasIndex(x => new { x.StudentId, x.TermId }).IsUnique()
                .HasFilter(null)
                .HasDatabaseName("UX_StudentTermRecords_Student_Term");

            // The projection reads a whole term's records to build the college and programme groups.
            e.HasIndex(x => x.TermId).HasDatabaseName("IX_StudentTermRecords_TermId");
        });
    }

    // §4.2 Schools
    private static void ConfigureSchools(ModelBuilder b) => b.Entity<School>(e =>
    {
        e.ToTable("Schools");
        e.Property(x => x.Name).HasMaxLength(200).IsRequired();
        e.Property(x => x.Code).HasMaxLength(50).IsRequired();
        e.Property(x => x.Address).HasMaxLength(500);
        e.Property(x => x.ContactEmail).HasMaxLength(256);
        e.Property(x => x.LogoUrl).HasMaxLength(1000);
        e.Property(x => x.TimeZone).HasMaxLength(64).IsRequired()
            .HasDefaultValue("Asia/Manila").ValueGeneratedNever();
        e.Property(x => x.IsActive).HasDefaultValue(true).ValueGeneratedNever();

        e.HasIndex(x => x.Code).IsUnique();
    });

    // §4.3 Students
    private static void ConfigureStudents(ModelBuilder b) => b.Entity<Student>(e =>
    {
        e.ToTable("Students");
        e.Ignore(x => x.FullName); // computed in the domain, not persisted

        e.Property(x => x.StudentNumber).HasMaxLength(50).IsRequired();
        e.Property(x => x.FirstName).HasMaxLength(100).IsRequired();
        e.Property(x => x.MiddleName).HasMaxLength(100);
        e.Property(x => x.LastName).HasMaxLength(100).IsRequired();
        e.Property(x => x.Email).HasMaxLength(256);
        // Not in §4.3 — additive, recorded as drift. Same length as Email; deliberately not unique,
        // because shared family addresses are ordinary and this must never become a lookup key.
        e.Property(x => x.AlternateEmail).HasMaxLength(256);
        e.Property(x => x.Course).HasMaxLength(150);
        e.Property(x => x.YearLevel).HasMaxLength(50);
        e.Property(x => x.Section).HasMaxLength(50);
        e.Property(x => x.Gender).HasMaxLength(20);
        e.Property(x => x.PhotoUrl).HasMaxLength(1000);
        e.Property(x => x.Status).HasMaxLength(20).IsRequired()
            .HasDefaultValue("Active").ValueGeneratedNever();
        e.Property(x => x.SisExternalId).HasMaxLength(100);
        e.Property(x => x.IsDeleted).HasDefaultValue(false).ValueGeneratedNever();

        e.HasOne(x => x.School).WithMany(s => s.Students).HasForeignKey(x => x.SchoolId).IsRequired();

        e.HasIndex(x => new { x.SchoolId, x.StudentNumber }).IsUnique()
            .HasDatabaseName("IX_Students_SchoolId_StudentNumber");
        e.HasIndex(x => x.SisExternalId).HasDatabaseName("IX_Students_SisExternalId");
        // §4.3 asks for a search index over (FirstName, LastName, StudentNumber). Full-text is a
        // separate catalog; these B-tree indexes serve the prefix/equality half of that need.
        e.HasIndex(x => new { x.LastName, x.FirstName }).HasDatabaseName("IX_Students_LastName_FirstName");
        e.HasIndex(x => x.StudentNumber).HasDatabaseName("IX_Students_StudentNumber");
    });

    // §4.4 RfidCards — uniqueness per ADR-001 D-3.
    private static void ConfigureRfidCards(ModelBuilder b) => b.Entity<RfidCard>(e =>
    {
        e.ToTable("RfidCards");
        e.Property(x => x.CardUid).HasMaxLength(128).IsRequired();
        e.Property(x => x.Label).HasMaxLength(100);
        e.Property(x => x.IsActive).HasDefaultValue(true).ValueGeneratedNever();

        e.HasOne(x => x.Student).WithMany(s => s.Cards).HasForeignKey(x => x.StudentId).IsRequired();
        e.HasOne(x => x.School).WithMany().HasForeignKey(x => x.SchoolId).IsRequired();

        // ADR-001 D-3: replaces §4.4's global UNIQUE(CardUid). A card is revoked and its serial can
        // later be issued again — to the same student on a re-encoded card, or to a different one when
        // a serial is recycled — and the old row must survive deactivated to explain the taps it
        // produced. A global unique index makes that impossible to record. Tenant-scoped because two
        // schools' card stocks are independent and may collide on a serial without meaning anything.
        e.HasIndex(x => new { x.SchoolId, x.CardUid }).IsUnique()
            .HasFilter("[IsActive] = 1")
            .HasDatabaseName("UX_RfidCards_SchoolId_CardUid_Active");

        // The UID→student resolution is the tap hot path (§4.4) and does not know the school.
        e.HasIndex(x => x.CardUid).HasDatabaseName("IX_RfidCards_CardUid");
    });

    // §4.5 Events
    private static void ConfigureEvents(ModelBuilder b) => b.Entity<Event>(e =>
    {
        e.ToTable("Events");
        e.Property(x => x.Name).HasMaxLength(200).IsRequired();
        e.Property(x => x.Description).HasMaxLength(2000);
        e.Property(x => x.Location).HasMaxLength(300);
        e.Property(x => x.AttendanceMode).HasMaxLength(20).IsRequired()
            .HasDefaultValue("Single").ValueGeneratedNever();
        e.Property(x => x.GraceMinutes).HasDefaultValue(0).ValueGeneratedNever();
        e.Property(x => x.RequireRegistration).HasDefaultValue(false).ValueGeneratedNever();
        e.Property(x => x.Status).HasMaxLength(20).IsRequired()
            .HasDefaultValue("Draft").ValueGeneratedNever();
        e.Property(x => x.IsDeleted).HasDefaultValue(false).ValueGeneratedNever();

        e.HasOne(x => x.School).WithMany(s => s.Events).HasForeignKey(x => x.SchoolId).IsRequired();
        e.HasOne(x => x.OrganizerUser).WithMany().HasForeignKey(x => x.OrganizerUserId);
    });

    // §4.6 EventSchedules
    private static void ConfigureEventSchedules(ModelBuilder b) => b.Entity<EventSchedule>(e =>
    {
        e.ToTable("EventSchedules");
        e.Property(x => x.RecurrenceRule).HasMaxLength(500);
        e.Property(x => x.IsCancelled).HasDefaultValue(false).ValueGeneratedNever();

        e.HasOne(x => x.Event).WithMany(v => v.Schedules).HasForeignKey(x => x.EventId).IsRequired();
    });

    // §4.7 StudentGroups & StudentGroupMembers, extended with ADR-001 D-1 provenance.
    private static void ConfigureStudentGroups(ModelBuilder b)
    {
        b.Entity<StudentGroup>(e =>
        {
            e.ToTable("StudentGroups");
            e.Property(x => x.Name).HasMaxLength(GroupName.MaxLength).IsRequired();
            e.Property(x => x.Type).HasMaxLength(30).IsRequired();

            // Defaults matter here beyond convenience: the migration backfills them onto the rows that
            // already exist, and "Manual / None" is the true statement about every one of them — they
            // were written before a projection existed.
            e.Property(x => x.SourceType).HasMaxLength(20).IsRequired()
                .HasDefaultValue(GroupSourceType.Manual).ValueGeneratedNever();
            e.Property(x => x.SourceEntityType).HasMaxLength(30).IsRequired()
                .HasDefaultValue(GroupSourceEntityType.None).ValueGeneratedNever();
            e.Property(x => x.SourceKey).HasMaxLength(AcademicKey.MaxLength).IsRequired()
                .HasDefaultValue("").ValueGeneratedNever();

            e.HasOne(x => x.School).WithMany().HasForeignKey(x => x.SchoolId).IsRequired();
            e.HasOne(x => x.Term).WithMany().HasForeignKey(x => x.TermId);

            // The projection's upsert key, and the only thing standing between a re-run and a second
            // copy of every group.
            //
            // Filtered to derived rows, and that is the whole design of the column set: manual groups
            // share SourceKey = '' and SourceEntityType = 'None' and would collide with each other
            // instantly under an unfiltered index. TermId is nullable but is never null inside the
            // filter, so the filter is stated as the SourceType predicate alone rather than being left
            // to the provider — which would have appended "[TermId] IS NOT NULL" and produced an index
            // that happens to work for the wrong reason.
            e.HasIndex(x => new { x.SchoolId, x.TermId, x.SourceEntityType, x.SourceKey }).IsUnique()
                .HasFilter("[SourceType] = 'Derived'")
                .HasDatabaseName("UX_StudentGroups_Derived_Source");

            // Declared explicitly, and it must stay that way. EF creates this index automatically for
            // the SchoolId foreign key, and the moment the composite above appeared — SchoolId first —
            // EF decided the single-column one was redundant and the scaffolded migration opened with
            // a DROP of it. It is not redundant: the composite is filtered to SourceType = 'Derived',
            // and SQL Server cannot use a filtered index for a query that does not imply its
            // predicate. Every tenant-scoped read of this table (the §11 query filter is exactly
            // "WHERE SchoolId = @p") would have gone to a scan, silently, for a manual group.
            e.HasIndex(x => x.SchoolId).HasDatabaseName("IX_StudentGroups_SchoolId");
        });

        b.Entity<StudentGroupMember>(e =>
        {
            e.ToTable("StudentGroupMembers");

            e.Property(x => x.SourceType).HasMaxLength(20).IsRequired()
                .HasDefaultValue(GroupSourceType.Manual).ValueGeneratedNever();

            e.HasOne(x => x.StudentGroup).WithMany(g => g.Members)
                .HasForeignKey(x => x.StudentGroupId).IsRequired();
            e.HasOne(x => x.Student).WithMany().HasForeignKey(x => x.StudentId).IsRequired();

            e.HasIndex(x => new { x.StudentGroupId, x.StudentId }).IsUnique()
                .HasDatabaseName("UX_StudentGroupMembers_Group_Student");
        });
    }

    // §4.8 EventGroups — XOR between group and individual student.
    private static void ConfigureEventGroups(ModelBuilder b) => b.Entity<EventGroup>(e =>
    {
        e.ToTable("EventGroups", t => t.HasCheckConstraint(
            "CK_EventGroups_GroupOrStudent",
            "([StudentGroupId] IS NULL AND [StudentId] IS NOT NULL) OR " +
            "([StudentGroupId] IS NOT NULL AND [StudentId] IS NULL)"));

        e.HasOne(x => x.Event).WithMany().HasForeignKey(x => x.EventId).IsRequired();
        e.HasOne(x => x.StudentGroup).WithMany().HasForeignKey(x => x.StudentGroupId);
        e.HasOne(x => x.Student).WithMany().HasForeignKey(x => x.StudentId);

        // §4.8 declares no uniqueness, and nothing wrote this table until the Phase 3a
        // POST /events/{id}/attendees existed — so the omission had never cost anything. It would now:
        // "attach these sections" is a form an organizer submits twice when the first response is slow,
        // and idempotency that lives only in a service's read-then-insert loses to two concurrent
        // posts. The duplicate would not corrupt the denominator (ExpectedStudentIds UNIONs, so a
        // student counted twice is still one), which is exactly what makes it worth constraining here:
        // the damage is invisible in every number and shows up only as an audience list with the same
        // section in it twice.
        //
        // Two indexes rather than one over both columns, because the CHECK constraint makes exactly one
        // of the pair NULL on every row — a composite would rely on SQL Server's NULL-equals-NULL
        // inside a unique index to do the right thing for two different reasons at once. Filtered, so
        // the halves cannot collide with each other; HasFilter is stated explicitly on both for the
        // reason this file records throughout — the provider appends its own IS NOT NULL and an index
        // that happens to work for a reason nobody chose is one refactor from not working.
        e.HasIndex(x => new { x.EventId, x.StudentGroupId }).IsUnique()
            .HasFilter("[StudentGroupId] IS NOT NULL")
            .HasDatabaseName("UX_EventGroups_Event_Group");

        e.HasIndex(x => new { x.EventId, x.StudentId }).IsUnique()
            .HasFilter("[StudentId] IS NOT NULL")
            .HasDatabaseName("UX_EventGroups_Event_Student");

        // Declared explicitly, and it must stay that way — the same trap IX_StudentGroups_SchoolId
        // records above, sprung again by the two indexes just added. EF creates this index for the
        // EventId foreign key; the moment two composites appeared with EventId leading, EF called it
        // redundant and the scaffolded migration opened with a DROP of it.
        //
        // It is not redundant. Both composites are filtered, and SQL Server cannot use a filtered index
        // for a query that does not imply its predicate. "Which groups and students are attached to
        // this event?" — the read AttachAudienceAsync runs to make itself idempotent, and the one a
        // future GET /events/{id} will run to list the audience — is `WHERE EventId = @e` with no null
        // predicate at all, so it implies neither filter and would have gone to a table scan, silently,
        // on the hottest read of the audience.
        e.HasIndex(x => x.EventId).HasDatabaseName("IX_EventGroups_EventId");
    });

    // §4.9 AttendanceRecords
    private static void ConfigureAttendanceRecords(ModelBuilder b) => b.Entity<AttendanceRecord>(e =>
    {
        e.ToTable("AttendanceRecords");
        e.Property(x => x.Status).HasMaxLength(20).IsRequired();
        e.Property(x => x.CaptureMethod).HasMaxLength(20).IsRequired()
            .HasDefaultValue("Rfid").ValueGeneratedNever();
        e.Property(x => x.DeviceTapId).HasMaxLength(100);
        // Phase 4c D-34. Same width as DeviceTapId: it holds the same kind of value from the same
        // client, and two columns in one key-space that disagreed about how long a key may be would
        // reject a check-out the check-in accepted.
        e.Property(x => x.CheckOutDeviceTapId).HasMaxLength(100);
        e.Property(x => x.Notes).HasMaxLength(500);

        // Phase 4d D-30 — the live cursor. Read AttendanceRecord.RowVersion for why it is a long and
        // why it must not become a concurrency token; this is the mapping that keeps both true.
        //
        // ValueGeneratedOnAddOrUpdate() + IsConcurrencyToken(false) is deliberately spelled out instead
        // of .IsRowVersion(). They differ in exactly one bit, and it is the bit that would turn the
        // TimeInOut check-out UPDATE into an optimistic-concurrency check and start throwing
        // DbUpdateConcurrencyException on a path that works today. .IsRowVersion() is one keystroke
        // shorter and is the wrong call; a reviewer "simplifying" this back to it changes write
        // semantics, which is why the two clauses are written out where the change would be visible.
        //
        // NumberToBytesConverter<long> is big-endian, so the CLR ordering and SQL Server's binary(8)
        // ordering agree — the property that makes `WHERE RowVersion > @since` mean what the cursor
        // says it means. HasColumnType("rowversion") is what makes the database assign the value; the
        // converter only decides how it is read back.
        e.Property(x => x.RowVersion)
            .HasConversion(new NumberToBytesConverter<long>())
            .HasColumnType("rowversion")
            .ValueGeneratedOnAddOrUpdate()
            .IsConcurrencyToken(false);

        // The live endpoint's delta read: one event's rows above a cursor, in cursor order. Without it
        // the query is a scan of every attendance row the event has ever taken, once per poll, per
        // open dashboard — and the whole reason D-29 chose polling over a hub is that the poll is
        // supposed to be cheap. EventId leads because it is the equality; RowVersion follows because it
        // is the range and the sort (the composite-index column-order rule this file records under
        // UX_Attendance_Device_DeviceTapId, applied where it was free to apply).
        e.HasIndex(x => new { x.EventId, x.RowVersion })
            .HasDatabaseName("IX_Attendance_EventId_RowVersion");

        e.HasOne(x => x.Event).WithMany(v => v.AttendanceRecords).HasForeignKey(x => x.EventId).IsRequired();
        // Phase 4a design D-35: the denormalized tenant. Required and FK-backed, exactly as
        // RfidCards.SchoolId is under ADR-001 D-3 — it exists so an index can be tenant-scoped, and a
        // nullable one would put the rows it failed to classify outside the filter instead of inside a
        // wrong tenant, which is the harder failure to notice.
        e.HasOne(x => x.School).WithMany().HasForeignKey(x => x.SchoolId).IsRequired();
        e.HasOne(x => x.Occurrence).WithMany().HasForeignKey(x => x.OccurrenceId);
        e.HasOne(x => x.Student).WithMany().HasForeignKey(x => x.StudentId).IsRequired();
        e.HasOne(x => x.RfidCard).WithMany().HasForeignKey(x => x.RfidCardId);
        e.HasOne(x => x.Device).WithMany().HasForeignKey(x => x.DeviceId);
        e.HasOne(x => x.RecordedByUser).WithMany().HasForeignKey(x => x.RecordedByUserId);

        // One attendance per student per event/occurrence. SQL Server compares NULL = NULL inside a
        // unique index, so the non-recurring case (OccurrenceId NULL) collapses to one row per pair.
        //
        // HasFilter(null) is load-bearing: the SQL Server provider silently adds
        // "WHERE [OccurrenceId] IS NOT NULL" to any unique index over a nullable column, which
        // would leave every non-recurring attendance row unconstrained — the opposite of §4.9.
        e.HasIndex(x => new { x.EventId, x.StudentId, x.OccurrenceId }).IsUnique()
            .HasFilter(null)
            .HasDatabaseName("UX_Attendance_Event_Student_Occurrence");

        // Idempotent offline sync (§4.9). Filtered so the many rows without a tap id do not collide.
        //
        // SchoolId leads the key as of Phase 4a design D-35, and that is a correctness fix rather than
        // a performance one. The index used to be global while the §11 query filter on this table was
        // per-tenant, so AttendanceService.FindByDeviceTapAsync and this constraint selected different
        // row sets the moment a second school existed: a colliding tap missed the pre-check, raised
        // 2601, failed the filtered re-read, and became a permanent 500 that an offline queue retries
        // forever. Filter and constraint are now the same predicate.
        //
        // Widening a unique key can never reject data that the narrower one accepted, so the swap is
        // safe on a populated table — see the RowLevelTenancy migration for the full argument, and for
        // why the reverse (Down) is not.
        //
        // Column order, noted for whoever next touches this index. (DeviceId, SchoolId, DeviceTapId)
        // would have been strictly better and was not spotted in time: uniqueness is order-independent
        // and the idempotency pre-check is three equalities either way, so it costs nothing — but it
        // would have kept DeviceId leading, which is what the FK to Devices needs, and made
        // IX_AttendanceRecords_DeviceId below unnecessary. Deliberately NOT re-cut: re-ordering an
        // applied migration's index to save one index on a table with no write volume yet is churn on
        // the one file it is least safe to churn. Worth doing when Phase 4d's batch endpoint makes the
        // write cost real, and that is a decision with an owner rather than a TODO.
        e.HasIndex(x => new { x.SchoolId, x.DeviceId, x.DeviceTapId }).IsUnique()
            .HasFilter("[DeviceTapId] IS NOT NULL")
            .HasDatabaseName("UX_Attendance_Device_DeviceTapId");

        // The other half of the same guarantee, for the second tap of a TimeInOut pair (Phase 4c,
        // D-34). Scoped (SchoolId, DeviceId, CheckOutDeviceTapId) — deliberately the same shape as the
        // index above rather than a better one, because the two are read by one lookup and a key that
        // was scoped differently would make "is this tap id already used?" mean two things depending on
        // which half of the pair it landed in.
        //
        // HasFilter is stated explicitly for the reason this file records throughout: the provider
        // appends its own "[CheckOutDeviceTapId] IS NOT NULL" and an index that happens to work for a
        // reason nobody chose is one refactor from not working. Here the stated filter is the same
        // predicate the provider would have inferred — and it has to be, because every row written
        // before this migration and every Single-mode row after it has a NULL here, and SQL Server
        // compares NULLs as equal inside a unique index. Unfiltered, the second such row would be
        // rejected and the whole table would cap at one check-out-less attendance record.
        //
        // Why this is not a mirror of the note on the index above about column order: DeviceId still
        // does not lead, and it still does not need to — IX_AttendanceRecords_DeviceId serves the
        // foreign key, and the lookup supplies SchoolId (from the query filter), DeviceId and the tap id
        // as three equalities either way.
        e.HasIndex(x => new { x.SchoolId, x.DeviceId, x.CheckOutDeviceTapId }).IsUnique()
            .HasFilter("[CheckOutDeviceTapId] IS NOT NULL")
            .HasDatabaseName("UX_Attendance_Device_CheckOutDeviceTapId");

        e.HasIndex(x => new { x.EventId, x.Status }).HasDatabaseName("IX_Attendance_EventId_Status");
    });

    // §4.10 Devices
    private static void ConfigureDevices(ModelBuilder b) => b.Entity<Device>(e =>
    {
        e.ToTable("Devices");
        e.Property(x => x.Name).HasMaxLength(100).IsRequired();
        e.Property(x => x.DeviceType).HasMaxLength(30).IsRequired();
        e.Property(x => x.ReaderModel).HasMaxLength(DeviceText.ReaderModelMaxLength);
        e.Property(x => x.ApiKey).HasMaxLength(256);
        e.Property(x => x.IsActive).HasDefaultValue(true).ValueGeneratedNever();

        // Phase 4b (D-24): the split key. ApiKeyId is the public half and is what a presented token is
        // looked up by; ApiKeyHash is SHA-256 of the secret in lower-case hex, sized to the value
        // exactly — the same reasoning §4.12's FileHash/RowHash columns are nvarchar(64) for. A hash
        // that does not fit is not a SHA-256, and SQL Server says so instead of storing a truncated
        // one that still looks plausible in a row dump.
        e.Property(x => x.ApiKeyId).HasMaxLength(DeviceKey.KeyIdLength);
        e.Property(x => x.ApiKeyHash).HasMaxLength(DeviceKey.HashLength);

        e.HasOne(x => x.School).WithMany().HasForeignKey(x => x.SchoolId).IsRequired();

        // §4.10 declares UNIQUE on a nullable column. SQL Server treats NULLs as equal in a unique
        // index, which would allow only one key-less device; filtered is the only workable reading.
        //
        // The column itself is permanently NULL from Phase 4b onward and is kept deliberately — see
        // Device.ApiKey. The index is kept with it: dropping an index on a column nothing writes buys
        // nothing and makes the schema disagree with §4.10 for a second reason.
        e.HasIndex(x => x.ApiKey).IsUnique()
            .HasFilter("[ApiKey] IS NOT NULL")
            .HasDatabaseName("UX_Devices_ApiKey");

        // The authentication hot path: one seek per presented token. Unique because a key id resolving
        // to two devices would make "which device is this?" ambiguous at exactly the moment the answer
        // is being used to choose a tenant. Filtered for the same reason UX_Devices_ApiKey is — most
        // rows will have a key, but a device may exist briefly without one and NULL = NULL inside a
        // unique index would cap that at one.
        e.HasIndex(x => x.ApiKeyId).IsUnique()
            .HasFilter("[ApiKeyId] IS NOT NULL")
            .HasDatabaseName("UX_Devices_ApiKeyId");
    });

    // §4.11 RBAC
    private static void ConfigureRbac(ModelBuilder b)
    {
        b.Entity<User>(e =>
        {
            e.ToTable("Users");
            e.Property(x => x.Email).HasMaxLength(256).IsRequired();
            e.Property(x => x.PasswordHash).IsRequired(); // nvarchar(max) per §4.11
            e.Property(x => x.FullName).HasMaxLength(200).IsRequired();
            e.Property(x => x.Phone).HasMaxLength(30);
            e.Property(x => x.IsActive).HasDefaultValue(true).ValueGeneratedNever();

            e.HasOne(x => x.School).WithMany().HasForeignKey(x => x.SchoolId).IsRequired();

            // GLOBALLY unique on Email alone, with no SchoolId in the key. §4.11 says so, and login
            // depends on it: a password is presented before any tenant is known, so
            // UserCredentialVerifier looks a user up by e-mail and nothing else, and that lookup has
            // to have exactly one answer.
            //
            // Rescoping this to (SchoolId, Email) would arrive looking like a tenancy fix — every
            // other unique index in this schema leads with SchoolId, and RfidCards.CardUid (ADR-001
            // D-3) is an explicit precedent for narrowing a plan-level global unique to a per-school
            // one. It is not the same case. A card is scanned by a reader that already knows its
            // school; an e-mail is typed into a login box by someone the system has not identified
            // yet. Widening the key would make the login lookup ambiguous, and nothing else in the
            // suite would fail — which is why RbacSchemaTests pins it here in the database rather
            // than leaving it inferable from this comment.
            e.HasIndex(x => x.Email).IsUnique().HasDatabaseName("UX_Users_Email");
        });

        b.Entity<Role>(e =>
        {
            e.ToTable("Roles");
            e.Property(x => x.Name).HasMaxLength(50).IsRequired();
            e.Property(x => x.Description).HasMaxLength(300);
            e.Property(x => x.IsSystem).HasDefaultValue(false).ValueGeneratedNever();
            e.HasIndex(x => x.Name).IsUnique().HasDatabaseName("UX_Roles_Name");
        });

        b.Entity<Permission>(e =>
        {
            e.ToTable("Permissions");
            e.Property(x => x.Code).HasMaxLength(100).IsRequired();
            e.Property(x => x.Description).HasMaxLength(300);
            e.HasIndex(x => x.Code).IsUnique().HasDatabaseName("UX_Permissions_Code");
        });

        b.Entity<UserRole>(e =>
        {
            e.ToTable("UserRoles");
            e.HasOne(x => x.User).WithMany(u => u.UserRoles).HasForeignKey(x => x.UserId).IsRequired();
            e.HasOne(x => x.Role).WithMany(r => r.UserRoles).HasForeignKey(x => x.RoleId).IsRequired();
            e.HasIndex(x => new { x.UserId, x.RoleId }).IsUnique().HasDatabaseName("UX_UserRoles_User_Role");
        });

        b.Entity<RolePermission>(e =>
        {
            e.ToTable("RolePermissions");
            e.HasOne(x => x.Role).WithMany(r => r.RolePermissions).HasForeignKey(x => x.RoleId).IsRequired();
            e.HasOne(x => x.Permission).WithMany(p => p.RolePermissions)
                .HasForeignKey(x => x.PermissionId).IsRequired();
            e.HasIndex(x => new { x.RoleId, x.PermissionId }).IsUnique()
                .HasDatabaseName("UX_RolePermissions_Role_Permission");
        });
    }

    /// <summary>
    /// §11 login's rotating refresh tokens. See <see cref="RefreshToken"/> for the design; this
    /// records the three schema decisions it implies.
    ///
    /// <para>
    /// <b>No <c>SchoolId</c> column and no query filter, and that is not the oversight it looks
    /// like.</b> Every other tenant-owning table in this schema is filtered, and this one must not be:
    /// a refresh is presented before any tenant is resolved — resolving the tenant is one of the
    /// things it is for — so a filter here would hide every row from the only query that ever reads
    /// them, and would do it silently, as an empty result rather than an error. The tenant reachable
    /// through <c>UserId</c> is the honest answer and there is no second source of truth to keep in
    /// step. It joins <c>AuditLogs</c> on the deliberately-unfiltered list.
    /// </para>
    ///
    /// <para>
    /// <b><c>TokenHash</c> is <c>nvarchar(64)</c> and unique.</b> Sized to the value exactly, for the
    /// reason §4.12's <c>FileHash</c>/<c>RowHash</c> and §4.10's <c>ApiKeyHash</c> are: a hash that
    /// does not fit is not a SHA-256, and SQL Server says so instead of storing a truncated one that
    /// still looks plausible. Unique because two rows sharing a hash would mean two live tokens
    /// redeeming each other's family — impossible from 256 bits of CSPRNG, and worth having the
    /// database say so rather than trusting that it stays impossible. It is <c>NOT NULL</c>, so the
    /// index carries no filter; <c>RbacSchemaTests</c> asserts that against <c>sys.indexes</c> rather
    /// than against this model, because a later nullable <c>TokenHash</c> would silently acquire the
    /// provider's automatic <c>IS NOT NULL</c> and stop constraining anything — the exact shape of the
    /// attendance-index defect this codebase already paid for once.
    /// </para>
    ///
    /// <para>
    /// <b><c>FamilyId</c> is indexed because revocation walks it.</b> Detecting a replay revokes every
    /// live token in the family, which is a range delete on a table that grows with every refresh of
    /// every session — without the index that is a scan on the busiest table the auth system has, run
    /// at exactly the moment something has gone wrong.
    /// </para>
    /// </summary>
    private static void ConfigureRefreshTokens(ModelBuilder b) => b.Entity<RefreshToken>(e =>
    {
        e.ToTable("RefreshTokens");

        e.Property(x => x.TokenHash).HasMaxLength(RefreshTokenValue.HashLength).IsRequired();

        // Verbatim and truncated: display text for a session list, never compared on redemption.
        e.Property(x => x.UserAgent).HasMaxLength(400);
        e.Property(x => x.IpAddress).HasMaxLength(45); // IPv6 textual maximum, as AuditLogs.

        // Configured with no navigation property on purpose. A required navigation to User — which
        // *is* filtered — is the interaction EF warns about, and an Include through it would return
        // null under a pinned tenant that did not happen to own the user. Redemption reads the user
        // explicitly, ignoring the filter, in the one place that is allowed to.
        e.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).IsRequired();

        e.HasIndex(x => x.TokenHash).IsUnique()
            .HasFilter(null)
            .HasDatabaseName("UX_RefreshTokens_TokenHash");

        e.HasIndex(x => x.FamilyId).HasDatabaseName("IX_RefreshTokens_FamilyId");

        // "This user's sessions", and "revoke everything this user holds". Both filter on UserId and
        // order by issue time, so the composite serves each with one seek.
        e.HasIndex(x => new { x.UserId, x.IssuedAt }).HasDatabaseName("IX_RefreshTokens_UserId_IssuedAt");
    });

    /// <summary>
    /// §4.12 SIS import, completed by ADR-001 D-4 (the versioned mapping) and D-5 (a required
    /// <c>TermId</c>, skip counters and warning columns).
    ///
    /// <para>
    /// <b>The two length constants that are not arbitrary.</b> <c>FileHash</c> and <c>RowHash</c> are
    /// <c>nvarchar(64)</c> because a SHA-256 in lower-case hex is exactly 64 characters — sized to the
    /// value rather than rounded up to a comfortable number, so a hash that does not fit is a hash that
    /// is not a SHA-256, and SQL Server says so instead of storing a truncated one that still looks
    /// plausible in a row dump.
    /// </para>
    /// </summary>
    private static void ConfigureSisImport(ModelBuilder b)
    {
        // Matches the character length of a SHA-256 rendered as lower-case hex.
        const int Sha256HexLength = 64;

        b.Entity<SisImportBatch>(e =>
        {
            e.ToTable("SisImportBatches");
            e.Property(x => x.Source).HasMaxLength(20).IsRequired();
            e.Property(x => x.FileName).HasMaxLength(300);
            e.Property(x => x.SourceSheetName).HasMaxLength(128);
            e.Property(x => x.FileHash).HasMaxLength(Sha256HexLength);
            // Widened from §4.12's nvarchar(20) to fit ADR-001 D-5's CompletedWithWarnings (21) and
            // CompletedWithErrors (19). Widening a column is additive and loses nothing; the
            // alternative was abbreviating a status into something a reader has to decode.
            e.Property(x => x.Status).HasMaxLength(30).IsRequired();
            e.Property(x => x.TotalRows).HasDefaultValue(0).ValueGeneratedNever();
            e.Property(x => x.InsertedRows).HasDefaultValue(0).ValueGeneratedNever();
            e.Property(x => x.UpdatedRows).HasDefaultValue(0).ValueGeneratedNever();
            e.Property(x => x.FailedRows).HasDefaultValue(0).ValueGeneratedNever();
            e.Property(x => x.SkippedRows).HasDefaultValue(0).ValueGeneratedNever();
            e.Property(x => x.WarningRows).HasDefaultValue(0).ValueGeneratedNever();

            // Progress. Nullable, and deliberately WITHOUT the HasDefaultValue(0) the six counters
            // above carry — the difference is the point, not an inconsistency. A counter's 0 is a fact
            // about a finished run ("no rows failed"). A progress 0 would be three different facts at
            // once: the phase has not started, the phase has no countable units, and the phase has
            // done none of its units. NULL says "no progress was reported", which is the truth for
            // every row that exists today and for every batch that ran before this column did.
            //
            // No index either: a batch is fetched by primary key on every path that reads these — the
            // poller asks about one batch it already has the id of — so an index would cost writes on
            // the hot path of a run to serve a query nobody makes.
            e.Property(x => x.ProgressPhase).HasMaxLength(40);
            e.Property(x => x.FailureReason).HasMaxLength(400);

            e.HasOne(x => x.School).WithMany().HasForeignKey(x => x.SchoolId).IsRequired();
            e.HasOne(x => x.RunByUser).WithMany().HasForeignKey(x => x.RunByUserId);

            // ADR-001 D-5. Required: an import that cannot say which term it is for cannot be placed in
            // time, and every downstream query is term-scoped.
            e.HasOne(x => x.Term).WithMany().HasForeignKey(x => x.TermId).IsRequired();

            // ADR-001 D-4. Nullable — a batch may predate any profile, and a null is the honest "we do
            // not know which rules ran" where a pointer at the current profile would be the exact lie
            // D-4 exists to prevent.
            e.HasOne(x => x.ImportProfile).WithMany().HasForeignKey(x => x.ImportProfileId);

            // "Show me this term's imports, newest first" is the only listing this table has.
            e.HasIndex(x => new { x.SchoolId, x.TermId })
                .HasDatabaseName("IX_SisImportBatches_SchoolId_TermId");
        });

        b.Entity<SisImportRow>(e =>
        {
            e.ToTable("SisImportRows");
            e.Property(x => x.RawData); // nvarchar(max) per §4.12
            e.Property(x => x.Result).HasMaxLength(20).IsRequired();
            e.Property(x => x.ErrorMessage).HasMaxLength(1000);
            e.Property(x => x.RowHash).HasMaxLength(Sha256HexLength);
            e.Property(x => x.WarningCode).HasMaxLength(50);
            e.Property(x => x.WarningMessage).HasMaxLength(1000);
            e.Property(x => x.SkipReason).HasMaxLength(50);

            e.HasOne(x => x.Batch).WithMany(t => t.Rows).HasForeignKey(x => x.BatchId).IsRequired();
            e.HasOne(x => x.Student).WithMany().HasForeignKey(x => x.StudentId);

            // One row per worksheet row per batch. Unique because the pipeline stages a batch once and
            // a duplicate row number would make "row 214" ambiguous in the one table whose purpose is
            // to answer questions about row 214.
            e.HasIndex(x => new { x.BatchId, x.RowNumber }).IsUnique()
                .HasFilter(null)
                .HasDatabaseName("UX_SisImportRows_Batch_RowNumber");

            // GET /sis/import/{id}/rows?result=Failed — the one query an operator runs after an import.
            e.HasIndex(x => new { x.BatchId, x.Result })
                .HasDatabaseName("IX_SisImportRows_BatchId_Result");
        });

        b.Entity<SisImportRowEntity>(e =>
        {
            e.ToTable("SisImportRowEntities");
            e.Property(x => x.EntityType).HasMaxLength(40).IsRequired();
            e.Property(x => x.Action).HasMaxLength(20).IsRequired();

            e.HasOne(x => x.SisImportRow).WithMany(r => r.Entities)
                .HasForeignKey(x => x.SisImportRowId).IsRequired();

            // EntityId is deliberately not a foreign key — see the type remarks. It points into one of
            // ten tables and must survive its target being deleted.
            e.HasIndex(x => new { x.SisImportRowId, x.EntityType, x.EntityId }).IsUnique()
                .HasFilter(null)
                .HasDatabaseName("UX_SisImportRowEntities_Row_Type_Entity");

            // The reverse question — "which import rows touched this course?" — which is what makes the
            // trail usable from the entity's side rather than only from the batch's.
            e.HasIndex(x => new { x.EntityType, x.EntityId })
                .HasDatabaseName("IX_SisImportRowEntities_Entity");
        });

        b.Entity<SisImportProfile>(e =>
        {
            e.ToTable("SisImportProfiles");
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.NameKey).HasMaxLength(AcademicKey.MaxLength).IsRequired();
            e.Property(x => x.Source).HasMaxLength(20).IsRequired();
            e.Property(x => x.Description).HasMaxLength(1000);
            e.Property(x => x.IsActive).HasDefaultValue(false).ValueGeneratedNever();

            e.HasOne(x => x.School).WithMany().HasForeignKey(x => x.SchoolId).IsRequired();

            // The version is part of the key, not a column that gets bumped in place: that is what
            // makes a version immutable in practice, and it is the whole of D-4's value.
            e.HasIndex(x => new { x.SchoolId, x.NameKey, x.Version }).IsUnique()
                .HasFilter(null)
                .HasDatabaseName("UX_SisImportProfiles_School_Name_Version");

            // At most one active version per mapping, for the reason Terms.IsCurrent is filtered the
            // same way: two active versions means a new batch picks one arbitrarily, and two imports of
            // the same file would be explained by different rules with nothing recording why.
            e.HasIndex(x => new { x.SchoolId, x.NameKey }).IsUnique()
                .HasFilter("[IsActive] = 1")
                .HasDatabaseName("UX_SisImportProfiles_School_Name_Active");
        });

        b.Entity<SisImportProfileColumn>(e =>
        {
            e.ToTable("SisImportProfileColumns");
            e.Property(x => x.SourceColumn).HasMaxLength(150).IsRequired();
            e.Property(x => x.SourceColumnKey).HasMaxLength(AcademicKey.MaxLength).IsRequired();
            e.Property(x => x.TargetField).HasMaxLength(100).IsRequired();
            e.Property(x => x.NormalizationRule).HasMaxLength(100).IsRequired();
            e.Property(x => x.IsRequired).HasDefaultValue(false).ValueGeneratedNever();
            e.Property(x => x.Ordinal).HasDefaultValue(0).ValueGeneratedNever();

            e.HasOne(x => x.Profile).WithMany(p => p.Columns)
                .HasForeignKey(x => x.ProfileId).IsRequired();

            // TargetField is in the key because one source column legitimately feeds several targets —
            // COLLEGE_NAME feeds both College.Name (verbatim) and College.NameKey (normalized), and
            // COURSE_CODE, PROGRAM and SECTION_NAME each do the same. What must not repeat is the same
            // column feeding the same target.
            e.HasIndex(x => new { x.ProfileId, x.SourceColumnKey, x.TargetField }).IsUnique()
                .HasFilter(null)
                .HasDatabaseName("UX_SisImportProfileColumns_Profile_Column_Target");
        });
    }

    // §4.13 AuditLogs & SystemSettings
    private static void ConfigureSystem(ModelBuilder b)
    {
        b.Entity<AuditLog>(e =>
        {
            e.ToTable("AuditLogs");
            e.Property(x => x.Action).HasMaxLength(100).IsRequired();
            e.Property(x => x.EntityType).HasMaxLength(100);
            e.Property(x => x.IpAddress).HasMaxLength(45); // IPv6 textual maximum
            e.Property(x => x.Changes); // nvarchar(max) JSON before/after

            // The tenant column §11's filter decision was deferred for. Nullable and unfiltered: a
            // NULL means "not attributed to a school", which is a real state (a system-generated
            // entry) and must stay visible under every tenant when the filter is eventually written.
            // See AuditLog.SchoolId — the column lands now so that rows written from here on can
            // carry a tenant; adding it at the same time as the filter would leave every existing
            // row with nothing to backfill from.
            e.HasOne(x => x.School).WithMany().HasForeignKey(x => x.SchoolId);

            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId);
            e.HasIndex(x => new { x.EntityType, x.EntityId }).HasDatabaseName("IX_AuditLogs_Entity");
            e.HasIndex(x => x.CreatedAt).HasDatabaseName("IX_AuditLogs_CreatedAt");
        });

        b.Entity<SystemSetting>(e =>
        {
            e.ToTable("SystemSettings");
            e.Property(x => x.Key).HasMaxLength(150).IsRequired();
            e.Property(x => x.Value); // nvarchar(max)
            e.Property(x => x.DataType).HasMaxLength(30);
            e.Property(x => x.Description).HasMaxLength(500);

            e.HasOne(x => x.School).WithMany().HasForeignKey(x => x.SchoolId);
            // NULL SchoolId is the global scope; SQL Server's NULL = NULL keeps it to one row per
            // key. HasFilter(null) suppresses the provider's automatic "SchoolId IS NOT NULL",
            // which would leave global settings free to duplicate a key.
            e.HasIndex(x => new { x.SchoolId, x.Key }).IsUnique()
                .HasFilter(null)
                .HasDatabaseName("UX_SystemSettings_SchoolId_Key");
        });
    }
}
