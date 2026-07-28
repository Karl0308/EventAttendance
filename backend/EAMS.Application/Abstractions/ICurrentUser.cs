namespace EAMS.Application.Abstractions;

/// <summary>
/// Who is making the current request. The deliberate twin of <see cref="ISchoolContext"/>: same shape,
/// same nullable-until-Phase-6 contract, same "replace one DI registration" upgrade path.
///
/// <para>
/// <b>Why this seam is installed before there is anything to put in it.</b> Of everything ADR-001 D-6
/// deferred, attribution is the only piece that is <em>irreversible</em>. A missing query filter or a
/// missing permission check can be added later and applies from that moment on; a missing
/// <c>RecordedByUserId</c> cannot. Technical Plan §6.4 calls the manual-override endpoint "audited",
/// and every override written between now and Phase 6 lands with a NULL there. When authentication
/// arrives, those rows can never be attributed — nobody recorded who did it, and no amount of later
/// work reconstructs it. Wiring the seam now costs one interface and one field assignment and means
/// the day real identities exist, every row from that day forward carries one, with no change to the
/// write path.
/// </para>
///
/// <para>
/// <b><see cref="UserId"/> is nullable and null means "nobody is authenticated".</b> That is a
/// property of the pre-auth build, not a permission model, exactly as
/// <see cref="ISchoolContext.CurrentSchoolId"/>'s null means "do not filter". A NULL
/// <c>RecordedByUserId</c> is therefore honest — it says the row was written by an unauthenticated
/// caller — where a synthetic "system" user id would be a fabricated audit entry, which is worse than
/// an absent one.
/// </para>
///
/// <para>
/// Phase 6 replaces the registration with a claims-reading implementation. Nothing on the write path
/// changes.
/// </para>
/// </summary>
public interface ICurrentUser
{
    /// <summary>
    /// The authenticated user's <c>Users.Id</c>, or <c>null</c> while no user is resolved (the whole
    /// of the pre-auth build). Read once per write, at the point the row is composed.
    /// </summary>
    Guid? UserId { get; }
}
