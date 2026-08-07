namespace EAMS.Api.Authorization;

/// <summary>
/// The claim types every principal in this system carries, whichever scheme produced it.
///
/// <para>
/// <b>They are short JWT-style names, not the WS-Federation URIs <c>ClaimTypes.*</c> hands out, and
/// that is the point.</b> Phase 6 adds <c>.AddJwtBearer("Bearer")</c> beside the device scheme, and a
/// JWT's payload keys arrive verbatim as claim types — so a policy written against
/// <c>http://schemas.xmlsoap.org/ws/2005/05/identity/claims/…</c> would match the device scheme and
/// silently not match the JWT one. Naming them here, once, is what makes "the two schemes emit the
/// same claim <em>types</em>" a checkable statement rather than an intention.
/// </para>
/// </summary>
public static class EamsClaimTypes
{
    /// <summary>
    /// The principal's identity. <c>device:{id}</c> for a device key; a user id for Phase 6's JWT.
    /// Prefixed rather than bare so a device id and a user id can never be confused for each other by
    /// anything that reads only this claim.
    /// </summary>
    public const string Subject = "sub";

    /// <summary>
    /// The tenant. Read by <c>ClaimsSchoolContext</c> and therefore by every §11 global query filter —
    /// this single claim is what makes the multi-tenant guard real rather than pinned.
    /// </summary>
    public const string SchoolId = "school_id";

    /// <summary>
    /// One §4.11 permission code. Repeatable: a JWT principal will carry many, a device key carries
    /// exactly one. Policies are expressed as <c>RequireClaim(Permission, code)</c>, so a device and a
    /// user reaching the same endpoint are indistinguishable to the authorization layer — which is the
    /// property that lets Phase 6 <em>extend</em> this rather than replace it.
    /// </summary>
    public const string Permission = "perm";

    /// <summary>
    /// The authenticated device's <c>Devices.Id</c>, unprefixed. Device-specific and deliberately not
    /// read by any policy — the authorization layer must stay ignorant of what kind of principal it is
    /// looking at. Read only by <c>ClaimsDeviceContext</c>, for the D-26 cross-check.
    /// </summary>
    public const string DeviceId = "device_id";

    /// <summary>Builds the <see cref="Subject"/> value for a device.</summary>
    public static string DeviceSubject(Guid deviceId) => $"device:{deviceId}";
}

/// <summary>
/// <b>Every §4.11 permission code this API declares — the registry, not a subset.</b> One home per
/// value, enforced in both directions by <c>PermissionRegistryTests</c>: a code that appears at a
/// <see cref="HasPermissionNotEnforcedAttribute"/> call site and not here is a <b>test failure</b>,
/// and so is one that appears here and at no call site.
///
/// <para>
/// <b>A test failure and not a compile error, and the distinction is the whole reason the tests
/// exist.</b> The attribute takes a <c>string</c>, so a bare literal at a call site compiles
/// perfectly — and because the attribute enforces nothing (ADR-001 D-6), a wrong one never fails a
/// request either. Nothing about a typo'd permission code is visible to the compiler or to the
/// runtime; the tests are the only thing that sees it.
/// </para>
///
/// <para>
/// <b>Why it stopped being "only the enforced ones".</b> Until Phase 3b-2 this class held
/// <see cref="AttendanceCapture"/> alone and every other code was a bare string literal at its
/// attribute. That made the audit list ADR-001 D-6 promised — "Phase 6 renames the attribute and the
/// compiler points at every endpoint" — depend on the codes themselves being spelled identically
/// thirty-odd times by hand. A typo (<c>student.read</c> for <c>students.read</c>) would compile,
/// pass every test, and quietly mint a permission that no role grants; the failure would surface on
/// the day enforcement went live, on whichever endpoint was least looked at. Naming them once is what
/// turns "these are the codes" from a claim into something checkable.
/// </para>
///
/// <para>
/// <b>Being here is not a claim that a code is enforced.</b> <see cref="AttendanceCapture"/> is the
/// only one that is, and it is enforced because it is also a policy name — see its remarks. The rest
/// are declarations of intent on the list Phase 6 walks.
/// </para>
/// </summary>
public static class EamsPermissions
{
    /// <summary>
    /// §11: "kiosks authenticate with a long-lived device API key scoped to <c>attendance.capture</c>
    /// only". The one permission a device key carries, and the policy name of the four gated
    /// endpoints — policy name and claim value are deliberately the same string, so a policy that
    /// exists but demands nothing is not expressible.
    /// </summary>
    public const string AttendanceCapture = "attendance.capture";

    /// <summary>§6.2. Browsing the roster: <c>GET /students</c> and <c>GET /students/{id}</c>.</summary>
    ///
    /// <remarks>
    /// <para>
    /// <b>It also guards <c>GET /student-groups</c>, and that is D-45 rather than an oversight.</b>
    /// Technical Plan §7.1 assigns <c>students.read</c> to the frontend's <c>/groups</c> page, so the
    /// plan had already answered the question that a separate <c>groups.read</c> code was minted to
    /// re-open. The plan is source of truth for the permission map; the minted code is gone rather
    /// than kept as a synonym, because two codes for one page is exactly the drift a registry exists
    /// to stop.
    /// </para>
    /// </remarks>
    public const string StudentsRead = "students.read";

    /// <summary>§6.2. Creating, editing, soft-deleting a student and assigning or revoking a card.</summary>
    public const string StudentsWrite = "students.write";

    /// <summary>§6.3. Reading events, their summary and their roster.</summary>
    public const string EventsRead = "events.read";

    /// <summary>§6.3. Creating and editing events, moving their status, and attaching an audience.</summary>
    public const string EventsWrite = "events.write";

    /// <summary>§6.4. Reading recorded attendance, including the live dashboard poll.</summary>
    public const string AttendanceRead = "attendance.read";

    /// <summary>
    /// §6.4. The organizer override — <c>POST /attendance/manual</c>. Distinct from
    /// <see cref="AttendanceCapture"/> on purpose: a kiosk records what a card presented, an organizer
    /// records what they decided, and a device key must never be able to do the second.
    /// </summary>
    public const string AttendanceWrite = "attendance.write";

    /// <summary>Phase 4b's device registry — <c>GET /devices</c> and <c>GET /devices/{id}</c>.</summary>
    public const string DevicesRead = "devices.read";

    /// <summary>
    /// Phase 4b. Registering, editing and deactivating a device, and rotating its key. §11's most
    /// consequential admin code: it mints capture credentials.
    /// </summary>
    public const string DevicesWrite = "devices.write";

    /// <summary>§10's roster import — profiles, dry runs and commits.</summary>
    public const string SisImport = "sis.import";

    /// <summary>
    /// ADR-001 D-1's academic reference reads — terms, colleges, programmes, courses, offerings.
    ///
    /// <para>
    /// <b>Minted by Phase 3b-2 and kept (D-44).</b> §6's tables predate the academic layer and assign
    /// it no routes, so there was nothing to inherit and this is an addition rather than a
    /// contradiction — unlike the <c>groups.read</c> that D-45 deleted, which contradicted §7.1. The
    /// nearest signal was reusing <see cref="StudentsRead"/>, which would have meant "anyone who can
    /// browse the roster can browse its structure": defensible, and a question Phase 6 should get to
    /// answer on its own rather than have pre-answered by a code that did not exist.
    /// </para>
    /// </summary>
    public const string AcademicRead = "academic.read";

    /// <summary>
    /// D-53's term administration — <c>POST</c>/<c>PUT /academic/terms</c> and
    /// <c>PATCH /academic/terms/{id}/current</c>. The only write anywhere in the academic layer.
    ///
    /// <para>
    /// <b>A second code rather than reusing <see cref="AcademicRead"/>, and a second one rather than
    /// reusing <see cref="SisImport"/>.</b> Reading the academic structure is what every audience picker
    /// and every import screen does; authoring the term that scopes all of it is what one administrator
    /// does at the start of a semester, and the whole product hangs off getting it right — a term made
    /// current by the wrong person points every subsequent import and every default at the wrong
    /// semester. <c>sis.import</c> was the nearest existing fit, since the operator who creates a term
    /// is the operator who then imports against it, but it would make "can upload a roster" and "can
    /// redefine which semester the institution is in" one permission, which is a question Phase 6 should
    /// get to answer rather than have pre-answered here.
    /// </para>
    ///
    /// <para>
    /// It grants no deletion, because D-53 defines none: a term with a batch imported against it cannot
    /// be removed without data loss, and retiring one is clearing its current flag.
    /// </para>
    /// </summary>
    public const string AcademicWrite = "academic.write";
}
