using System.Text.Json;
using System.Text.Json.Serialization;

namespace EAMS.Application.Dtos;

// The Phase 3b-1 write surface for Technical Plan §6.2. The read-side StudentDto and CardDto stay in
// Dtos.cs with the rest of the core slice.

/// <summary>
/// The body of <c>POST /students</c> and <c>PUT /students/{id}</c>.
///
/// <para>
/// <b><c>Course</c>, <c>YearLevel</c> and <c>Section</c> are deliberately absent</b>, and their
/// absence is the whole point of this type. They are the ADR-001 D-2 derived display cache: a student
/// enrolled in two sections cannot be described by a single-valued triple — 12 of the 52 students in
/// the real roster are — so the academic tables are the source of truth and these columns are refreshed
/// from them. A writable field here would be a second, diverging answer to "what section is this
/// student in", and <c>EamsDbContext</c>'s cache guard exists because <c>db.Students.Update(entity)</c>
/// — the shape a REST PUT handler takes — is the exact path it was built to catch.
/// </para>
///
/// <para>
/// <b>Absent is not enough on its own, which is what <see cref="UnmappedFields"/> is for.</b> Without
/// it, a client that reads a <see cref="StudentDto"/>, edits the name and PUTs the whole object back
/// — the ordinary shape of an edit form — would have its <c>course</c>/<c>section</c> silently dropped
/// by the deserializer and would believe they were saved. Capturing what we did not model lets the
/// service refuse the request by name instead.
/// </para>
///
/// <para>
/// <b><c>SchoolId</c> and <c>IsDeleted</c> are absent too.</b> The tenant comes from
/// <c>ISchoolContext</c>, never from the request (see <c>EventWriteRequest</c> for the same rule);
/// deletion is <c>DELETE /students/{id}</c>, so there is one door into the flag rather than two.
/// <c>SisExternalId</c>, <c>LastSyncedAt</c> and <c>AlternateEmail</c> are absent because they belong
/// to the §10 import pipeline — a manual edit must not be able to claim a student came from the SIS,
/// and a PUT that omitted them would blank an importer's work.
/// </para>
/// </summary>
/// <param name="Status">
/// One of <c>Active</c> / <c>Inactive</c> / <c>Graduated</c> (§4.3). Null or blank takes §4.3's
/// <c>Active</c> default, so a create form that has no status field still produces a valid row.
/// </param>
public record StudentWriteRequest(
    string StudentNumber,
    string FirstName,
    string? MiddleName,
    string LastName,
    string? Email,
    string? Gender,
    string? PhotoUrl,
    string? Status)
{
    /// <summary>
    /// Every JSON member the client sent that this contract does not model.
    ///
    /// <para>
    /// It exists for exactly one reason: to make a supplied <c>course</c>, <c>yearLevel</c> or
    /// <c>section</c> <em>visible</em> to the service so it can be refused (ADR-001 D-2). Silently
    /// ignoring unknown members is the deserializer's default and is the wrong answer here — the
    /// caller would get a 200 and believe a derived field had been written.
    /// </para>
    ///
    /// <para>
    /// Nothing else reads it. Members other than the three cache columns are ignored exactly as
    /// before, because a client echoing <c>id</c>, <c>fullName</c> or <c>cards</c> back at a PUT is
    /// ordinary and harmless.
    /// </para>
    /// </summary>
    [JsonExtensionData]
    public IDictionary<string, JsonElement>? UnmappedFields { get; init; }
}

/// <summary>
/// The body of <c>POST /students/{id}/cards</c> — §6.2's <c>{cardUid, label}</c>.
///
/// <para>
/// The UID is normalized before anything is compared or stored (<c>CardUid.Normalize</c>): readers
/// emit <c>04:a7:b8:c9</c>, <c>04-A7-B8-C9</c> and <c>04 a7 b8 c9</c> for one card, and the stored form
/// is the only one the filtered unique index can defend.
/// </para>
/// </summary>
public record StudentCardRequest(string CardUid, string? Label);
