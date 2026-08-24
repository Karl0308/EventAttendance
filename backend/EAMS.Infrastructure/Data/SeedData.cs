using EAMS.Application.Abstractions;
using EAMS.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace EAMS.Infrastructure.Data;

/// <summary>
/// Dev-only convenience data. The guard is the school row: on a persistent database the second and
/// every later start finds it and returns without writing, so seeding stays a no-op once applied.
///
/// <para>
/// <b>This is also the whole of Phase 4b's answer to "how do I develop against a gated endpoint?"</b>
/// (D-28). The alternative — a configuration switch that turns device authentication off — was
/// rejected: an off-switch is a thing that can be set in the wrong environment, and it would make the
/// gated and ungated builds behave differently in a way no test covers. A seeded device with a
/// well-known key exercises the *real* handler, the real claims and the real policy, and it rides the
/// existing environment gate, so the well-known credential cannot exist outside a development host at
/// all. The three <c>DeviceAuthenticationTests.The_seeded_development_key_*</c> tests pin all of it —
/// works in Development, absent in Staging, absent in Production.
/// </para>
/// </summary>
internal static class SeedData
{
    /// <summary>
    /// The development kiosk's key, in plaintext, deliberately.
    ///
    /// <para>
    /// <b>A hard-coded credential in source is normally a finding, and this one is not, for exactly one
    /// reason: it can only ever exist on a Development host.</b> <c>Program.cs</c> calls
    /// <c>InitializeEamsDatabaseAsync(seed: app.Environment.IsDevelopment())</c>, so the device row this
    /// key belongs to is never written anywhere else — which makes the string worthless anywhere it
    /// could do harm. <b>If that gate is ever loosened, this constant becomes a real credential leak and
    /// must go in the same change.</b>
    /// </para>
    ///
    /// <para>
    /// <b>The gate was <c>!IsProduction()</c> when this constant was first written, and that was
    /// wrong.</b> It admits <c>Staging</c> — a real, network-reachable host — so the sentence above
    /// would have been false of exactly the environment where it mattered most. Recorded rather than
    /// quietly corrected, because "not Production" reads as a synonym for "development only" and is
    /// not one.
    /// </para>
    ///
    /// <para>
    /// It is shaped like a real key (<c>eams_dk_&lt;12&gt;_&lt;64&gt;</c>) rather than being a magic
    /// short string, so the parser, the hash comparison and the handler all take the same path they
    /// take in production — a shortcut here would be a shortcut around the thing under development.
    /// The repeating <c>0de0</c> pattern makes it obvious in a log which key was used.
    /// </para>
    ///
    /// <para>
    /// <b>Both halves are lower-case hexadecimal, and that is a constraint rather than a style.</b>
    /// The first draft of this constant read <c>0dev00000001</c>, which is not hex —
    /// <see cref="DeviceKey"/> rejected it as malformed, the seed wrote a device with an empty key id,
    /// and the kiosk simply did not authenticate. It failed silently, in exactly the way a development
    /// convenience is least likely to be noticed failing, which is why <see cref="InitializeAsync"/>
    /// now refuses to seed a malformed one.
    /// </para>
    /// </summary>
    public const string DevelopmentKioskApiKey =
        "eams_dk_0de0de0de0de_" +
        "0de00de00de00de00de00de00de00de00de00de00de00de00de00de00de00de0";

    /// <summary>The seeded kiosk's name, so tests and tooling can find it without a magic string.</summary>
    public const string DevelopmentKioskName = "Development Kiosk";

    /// <summary>
    /// The card serial of the seeded student the capture tests tap, named here for the same reason as
    /// the kiosk above: so a test refers to the seed rather than re-typing one of its values.
    ///
    /// <para>
    /// <b>This constant exists because the copy of it was silently orphaned once.</b> The seed's UIDs
    /// changed from hex to decimal serials on 2026-07-30 (register D-43) and
    /// <c>DeviceAuthenticationTests</c> went on asserting the old literal — a red suite whose cause was
    /// in a different project from the failure, and which no reviewer of either file could have seen by
    /// reading it. A shared constant makes that drift a compile error instead.
    /// </para>
    /// </summary>
    public const string DevelopmentCardUid = "0012503303";

    /// <summary>
    /// The seeded term's <see cref="Term.Code"/>, named for the same reason as
    /// <see cref="DevelopmentCardUid"/>: a test that locates the seeded term will otherwise re-type the
    /// literal, and a later edit to the seed would leave that copy silently orphaned. The value matches
    /// the term already present on the existing dev database, so a freshly seeded machine and one
    /// carried over agree on it.
    /// </summary>
    public const string DevelopmentTermCode = "2025-2026-1";

    /// <summary>
    /// The Development SuperAdmin's login. A constant for the reason
    /// <see cref="DevelopmentCardUid"/> is one, plus a second: <c>UserProvisioningService</c> reads it
    /// to recognise a <c>create-admin</c> collision with the seeded account and say so, instead of
    /// telling a developer that an address they have never typed is already taken.
    /// </summary>
    public const string DevelopmentSuperAdminEmail = "dev-admin@usa.edu.ph";

    /// <summary>
    /// The configuration key the Development SuperAdmin's password comes from.
    ///
    /// <para>
    /// <b>There is no constant holding the password, and that asymmetry with
    /// <see cref="DevelopmentKioskApiKey"/> is the point.</b> A well-known device key is acceptable
    /// there for exactly one reason — it is scoped to <c>attendance.capture</c> on a Development-only
    /// row, so it is worthless anywhere it could do harm. A SuperAdmin password is not scoped to
    /// anything: it is the credential that mints device keys and redefines which semester the
    /// institution is in. Copying that precedent here would put a working administrator password in a
    /// public repository, and the environment gate would be the only thing between it and a real
    /// installation.
    /// </para>
    ///
    /// <para>
    /// So the password is configuration, and <b>when it is absent the seed does not happen</b> — it is
    /// never defaulted, never generated, never derived. In development it belongs in user secrets:
    /// <c>dotnet user-secrets set "Seed:DevelopmentSuperAdminPassword" "&lt;something long&gt;"
    /// --project backend/EAMS.Api</c>.
    /// </para>
    /// </summary>
    public const string DevelopmentSuperAdminPasswordKey = "Seed:DevelopmentSuperAdminPassword";

    /// <summary>
    /// The Development SuperAdmin — the account a developer logs into the admin SPA with once §11's
    /// login endpoint exists.
    ///
    /// <para>
    /// <b>Its own guard, on its own row, following <see cref="SeedTermAsync"/>.</b> Every dev database
    /// on this project already holds a school, so anything that rides
    /// <see cref="InitializeAsync"/>'s early return runs on a database nobody has — that failure has
    /// already happened once here, with <c>Terms</c>, and the import page was dead on every carried-over
    /// machine for it. This checks for the user by e-mail and nothing else.
    /// </para>
    ///
    /// <para>
    /// <b>It creates nothing itself.</b> The row goes through <see cref="IUserProvisioningService"/>,
    /// the same service the <c>create-admin</c> console command calls, so the password policy, the
    /// e-mail normalization, the duplicate refusal, the role grant and the audit row are one
    /// implementation rather than two that drift — and the one that would drift is the Production
    /// bootstrap path, which is the one nobody exercises until they need it. The same rule
    /// <c>IntegrationTest.IssueDeviceKeyAsync</c> follows for device keys.
    /// </para>
    /// </summary>
    /// <param name="environmentName">
    /// Re-checked here rather than trusted from the caller. The gate already exists in
    /// <c>InitializeEamsDatabaseAsync</c>; this is a second, independent refusal, because a seeded
    /// administrator on a Staging or Production host is not the kind of mistake that should need one
    /// call site to stay correct. It throws rather than returning quietly — a caller that reached this
    /// outside Development has a bug that must not be papered over.
    /// </param>
    /// <param name="configuredPassword">
    /// From <see cref="DevelopmentSuperAdminPasswordKey"/>. Absent means the seed is skipped and the
    /// reason is logged; there is no fallback.
    /// </param>
    public static async Task SeedDevelopmentSuperAdminAsync(
        EamsDbContext db,
        IUserProvisioningService provisioning,
        string environmentName,
        string? configuredPassword,
        ILogger logger,
        CancellationToken ct = default)
    {
        if (!EamsEnvironments.IsDevelopment(environmentName))
        {
            throw new InvalidOperationException(
                $"The Development SuperAdmin seed was reached in environment '{environmentName}'. " +
                "It creates an administrator account from a configured password and must only ever " +
                "run in Development — see SeedData.DevelopmentSuperAdminPasswordKey.");
        }

        if (string.IsNullOrWhiteSpace(configuredPassword))
        {
            logger.LogInformation(
                "No Development SuperAdmin was seeded: '{Key}' is not configured. Set it in user " +
                "secrets to get one — there is deliberately no default and no generated password, " +
                "because a hard-coded administrator credential in source is a real credential and a " +
                "generated one would change on every restart.",
                DevelopmentSuperAdminPasswordKey);
            return;
        }

        // The guard, on this seed's own row. IgnoreQueryFilters because Users is tenant-filtered and
        // the tenant is not pinned until after seeding — a filtered check would report the user
        // absent and this would try to create it on every start.
        var exists = await db.Users.AsNoTracking().IgnoreQueryFilters()
            .AnyAsync(u => u.Email == DevelopmentSuperAdminEmail, ct);

        if (exists) return;

        var result = await provisioning.CreateAsync(
            new UserProvisioningRequest(
                DevelopmentSuperAdminEmail,
                "Development SuperAdmin",
                RoleName: EamsRoleNames.SuperAdmin),
            configuredPassword,
            actor: $"seed:{nameof(SeedDevelopmentSuperAdminAsync)}",
            ct);

        if (result.Outcome == UserProvisioningOutcome.Created)
        {
            logger.LogInformation(
                "Seeded the Development SuperAdmin '{Email}' from '{Key}'. Development only.",
                DevelopmentSuperAdminEmail, DevelopmentSuperAdminPasswordKey);
            return;
        }

        // Warned, not thrown: the commonest cause is a configured password shorter than the policy,
        // and a host that refuses to start over a development convenience is worse than one that
        // starts and says why the convenience is missing.
        logger.LogWarning(
            "The Development SuperAdmin was not seeded ({Outcome}): {Message} The password comes " +
            "from '{Key}'.",
            result.Outcome, result.Message, DevelopmentSuperAdminPasswordKey);
    }

    public static async Task InitializeAsync(EamsDbContext db, CancellationToken ct = default)
    {
        var school = await db.Schools.FirstOrDefaultAsync(ct);
        if (school is null)
        {
            school = new School
            {
                Name = "University of San Agustin",
                Code = "USA",
                Address = "General Luna St, Iloilo City",
                ContactEmail = "cicss@usa.edu.ph",
                TimeZone = "Asia/Manila",
            };
            db.Schools.Add(school);
        }

        await SeedTermAsync(db, school, ct);

        // Everything below is the first-run bulk. It stays gated on the school having just been
        // created, because these rows are a coherent fixture — students with cards, events, a kiosk,
        // and taps that reference all three — and re-adding any of it onto a database an operator has
        // already worked in would duplicate rows rather than repair them. SeedTermAsync above is the
        // exception on purpose: see its own comment for why a term must arrive even on a carried-over
        // database.
        if (db.Entry(school).State != EntityState.Added)
        {
            await db.SaveChangesAsync(ct);
            return;
        }

        // `No` is the REGNO — Students.StudentNumber. `Uid` is the RFID card serial — RfidCards.CardUid.
        // They are DIFFERENT VALUES for different things (client correction, 2026-07-30, register D-43):
        // the REGNO identifies the person on the roster, the serial is what a reader actually scans.
        //
        // These serials are MOCK. The client's roster carries no RFID column yet, so until their next
        // export arrives this is the only way to exercise a tap end to end. They are shaped like the real
        // sample (0012503326) rather than invented freely, because the shape is the part that breaks
        // things: ten decimal digits, no separators, and LEADING ZEROS THAT MATTER. Anything that parses
        // one of these as a number turns 0012503301 into 12503301 and the card stops resolving — see
        // RosterText.FormatNumericCell for the same hazard arriving via an Excel numeric cell.
        //
        // Two of the eight deliberately vary the zero count (three leading zeros, then one) so a reader
        // or serializer that only ever sees the common "00" shape cannot pass by luck. Seeding is
        // Development-only, so none of this can reach a real database.
        var seed = new (string No, string First, string? Middle, string Last, string Course, string Year, string Section, string Gender, string Uid)[]
        {
            ("2023-0001", "Maria",   "Reyes",   "Santos",     "BSIT", "3rd Year", "A", "Female", "0012503301"),
            ("2023-0002", "Juan",    "Cruz",    "Dela Cruz",  "BSIT", "3rd Year", "A", "Male",   "0012503302"),
            ("2023-0003", "Andrea",  null,      "Lim",        "BSCS", "2nd Year", "B", "Female", DevelopmentCardUid),
            ("2023-0004", "Miguel",  "Tan",     "Gonzales",   "BSCS", "2nd Year", "B", "Male",   "0012503304"),
            ("2023-0005", "Sofia",   "Villa",   "Ramos",      "BSIT", "1st Year", "C", "Female", "0012503305"),
            ("2023-0006", "Gabriel", null,      "Flores",     "BSA",  "4th Year", "A", "Male",   "0012503306"),
            ("2023-0007", "Isabella","Marie",   "Aquino",     "BSN",  "2nd Year", "A", "Female", "0001234567"),
            ("2023-0008", "Diego",   "Luis",    "Mendoza",    "BSIT", "3rd Year", "A", "Male",   "0987654321"),
        };

        var students = new List<Student>();
        foreach (var s in seed)
        {
            var student = new Student
            {
                SchoolId = school.Id,
                StudentNumber = s.No,
                FirstName = s.First,
                MiddleName = s.Middle,
                LastName = s.Last,
                Email = $"{s.No}@usa.edu.ph",
                Course = s.Course,
                YearLevel = s.Year,
                Section = s.Section,
                Gender = s.Gender,
                // ADR-001 "Accepted Context": a synthetic value, not a real Mastersoft key.
                SisExternalId = $"SIS-{s.No}",
            };
            // SchoolId is denormalized onto the card (ADR-001 D-3) and must match the owner's.
            student.Cards.Add(new RfidCard { SchoolId = school.Id, CardUid = s.Uid, Label = "Primary ID" });
            students.Add(student);
        }
        db.Students.AddRange(students);

        var now = DateTime.UtcNow;
        var openEvent = new Event
        {
            SchoolId = school.Id,
            Name = "University Convocation 2026",
            Description = "Opening convocation for the academic year.",
            Location = "USA Gymnasium",
            StartAt = now.AddMinutes(-30),
            EndAt = now.AddHours(2),
            AttendanceMode = AttendanceMode.Single,
            GraceMinutes = 15,
            Status = EventStatus.Open,
        };
        var pastEvent = new Event
        {
            SchoolId = school.Id,
            Name = "IT Week Seminar",
            Description = "Guest lecture on cloud computing.",
            Location = "AVR 2",
            StartAt = now.AddDays(-3),
            EndAt = now.AddDays(-3).AddHours(3),
            AttendanceMode = AttendanceMode.Single,
            GraceMinutes = 10,
            Status = EventStatus.Closed,
        };
        db.Events.AddRange(openEvent, pastEvent);

        // The §4.10 device the local mobile/kiosk client authenticates as. See DevelopmentKioskApiKey
        // for why a plaintext key in source is acceptable here and nowhere else.
        //
        // Only the *hash* of the secret half is stored, exactly as a registered device's is — the seed
        // takes the same path DeviceService.ApplyNewKey does rather than a privileged shortcut, so a
        // change to the format breaks this row loudly instead of leaving a device that authenticates
        // by a rule production does not use. ApiKey (§4.10's original single-column key) is left null,
        // like every other row.
        if (!DeviceKey.TryParse(DevelopmentKioskApiKey, out var kioskKeyId, out var kioskSecret))
        {
            // Loud rather than silent, because the silent version already happened once: a key id with
            // a non-hex character parsed to nothing, the device was seeded with an empty key id, and
            // the only symptom was a 401 nobody could explain. A development convenience that does not
            // work must say so at the moment it is created, not at the moment it is used.
            throw new InvalidOperationException(
                $"{nameof(DevelopmentKioskApiKey)} is not a well-formed device key. Both halves must " +
                "be lower-case hexadecimal — see DeviceKey.TryParse.");
        }

        db.Devices.Add(new Device
        {
            SchoolId = school.Id,
            Name = DevelopmentKioskName,
            DeviceType = DeviceTypes.Kiosk,
            ReaderModel = "Development stand-in",
            ApiKeyId = kioskKeyId,
            ApiKeyHash = DeviceKey.HashSecret(kioskSecret),
            ApiKeyIssuedAt = now,
        });

        // A few taps already recorded on the open event.
        db.AttendanceRecords.AddRange(
            new AttendanceRecord
            {
                SchoolId = school.Id,
                EventId = openEvent.Id, StudentId = students[0].Id,
                RfidCardId = students[0].Cards.First().Id,
                CheckInAt = now.AddMinutes(-25), Status = AttendanceStatus.Present,
                CaptureMethod = CaptureMethod.Rfid,
                DeviceTapId = Guid.NewGuid().ToString(),
            },
            new AttendanceRecord
            {
                SchoolId = school.Id,
                EventId = openEvent.Id, StudentId = students[1].Id,
                RfidCardId = students[1].Cards.First().Id,
                CheckInAt = now.AddMinutes(-5), Status = AttendanceStatus.Late,
                CaptureMethod = CaptureMethod.Rfid,
                DeviceTapId = Guid.NewGuid().ToString(),
            }
        );

        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// A term, because the importer takes a <c>TermId</c> as an *input* (ADR-001 D-5) and never creates
    /// one. Without this row the roster-import page's term picker is empty and the page correctly
    /// refuses to stage a batch — i.e. the first thing a new developer tries is dead.
    ///
    /// <para>
    /// <b>D-53 gave the product a way to author a term, and that did not make this row redundant.</b>
    /// A term can now be created on the SPA's <c>/terms</c> page (<c>TermAdminService</c>;
    /// <c>AcademicController</c> itself stays read-only for the four importer-owned families). That is
    /// the right recovery for an install, and the wrong one for a developer's first five minutes — the
    /// point of a seed is that the import page works before anyone has been told what a term is.
    /// </para>
    ///
    /// <para>
    /// <b>This is guarded on <c>Terms</c> rather than riding the school guard, because riding it failed
    /// in exactly the way that matters.</b> The term block was added to the seed after dev databases
    /// already held a school, so <c>if (Schools.Any()) return;</c> short-circuited before ever reaching
    /// it: those databases carry a school, students, events and a device, and zero terms — a state no
    /// clean run can produce — and the import page is dead on every one of them. A seed step that only
    /// ever runs on a database nobody has is not a seed step. Anything added here later wants its own
    /// guard for the same reason.
    /// </para>
    ///
    /// <para>
    /// <c>StartsOn</c>/<c>EndsOn</c> stay null: the SIS export has no term-date columns at all, so a real
    /// term does not have them either (<c>AcademicReferenceService.ListTermsAsync</c> orders on
    /// <c>Code</c> precisely because those dates are usually absent). Inventing dates here would make the
    /// seed the only term in the system that carries them, which is a worse fixture than one that
    /// matches.
    /// </para>
    /// </summary>
    private static async Task SeedTermAsync(EamsDbContext db, School school, CancellationToken ct)
    {
        // Any term at all, not just this one: an operator who created their own is not missing the
        // fixture this exists to provide, and adding a second IsCurrent row would violate
        // UX_Terms_SchoolId_Current. A school added moments ago has no rows to query, so the local
        // check covers the fresh-database case without a round trip against an unsaved key.
        var hasTerm = db.Entry(school).State == EntityState.Added
            ? false
            : await db.Terms.AnyAsync(t => t.SchoolId == school.Id, ct);
        if (hasTerm) return;

        db.Terms.Add(new Term
        {
            SchoolId = school.Id,
            Code = DevelopmentTermCode,
            SchoolYear = "2025-2026",
            Semester = "1st Semester",
            // At most one current term per school (UX_Terms_SchoolId_Current). Guarded above on the
            // school having no term of any kind, so this cannot collide with an operator-created one.
            IsCurrent = true,
        });
    }
}
