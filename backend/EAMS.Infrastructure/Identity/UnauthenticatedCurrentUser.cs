using EAMS.Application.Abstractions;

namespace EAMS.Infrastructure.Identity;

/// <summary>
/// The pre-auth <see cref="ICurrentUser"/>: there are no claims to read, so there is no user, so it
/// returns <c>null</c>.
///
/// <para>
/// It returns null rather than a synthetic "system" identity on purpose. A NULL
/// <c>AttendanceRecords.RecordedByUserId</c> is an accurate statement — "written before authentication
/// existed" — and Phase 6 can find those rows and say so. A placeholder GUID would instead be a
/// fabricated audit entry that looks exactly like a real one, on the endpoint §6.4 specifically calls
/// audited.
/// </para>
///
/// <para>
/// Deliberately has no way to set the value, unlike <c>DevelopmentSchoolContext</c>'s pin. There is
/// nothing to pin it from; Phase 6 replaces the registration rather than mutating this.
/// </para>
/// </summary>
internal sealed class UnauthenticatedCurrentUser : ICurrentUser
{
    public Guid? UserId => null;
}
