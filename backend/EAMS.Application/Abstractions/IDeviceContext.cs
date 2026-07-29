namespace EAMS.Application.Abstractions;

/// <summary>
/// Which <em>device</em> is making the current request. The third member of the seam family that
/// ADR-001 D-6 installed — <see cref="ISchoolContext"/> for the tenant, <see cref="ICurrentUser"/> for
/// the human, this for the kiosk — and it exists for the same reason they do: the answer comes from
/// the request's principal, and no request body may supply it.
///
/// <para>
/// <b>The rule this interface exists to enforce (Phase 4a design, D-26).</b>
/// <c>TapRequest.DeviceId</c> stays on the wire, but it is a <em>cross-check</em>, never the source of
/// truth. A device that authenticates has already said which device it is, in a way it cannot lie
/// about; a body field is a claim it can. So:
/// <list type="bullet">
///   <item>absent in the body → filled from the principal;</item>
///   <item>present and matching → accepted;</item>
///   <item>present and <b>mismatched → 400 <c>DeviceMismatch</c></b>, never a silent ignore.</item>
/// </list>
/// Silently overwriting a caller's field is the same failure the students write surface refuses with
/// <c>FieldIsDerived</c>: a client that echoes back what it sent would believe a value it never saved.
/// </para>
///
/// <para>
/// <b><see cref="DeviceId"/> is nullable and null means "no device is authenticated".</b> That is the
/// ordinary state on every endpoint except the four the device key gates — an organizer's manual
/// override has no device, and neither does a migration or a background task.
/// </para>
///
/// <para>
/// <b>There is deliberately no <c>SchoolId</c> here, and its absence is a decision.</b> The device's
/// principal does carry a <c>school_id</c> claim, so exposing it would be one line — and it would
/// create a second route to a fact that already has two authorities: <see cref="ISchoolContext"/> for
/// what a request may see, and <c>Devices.SchoolId</c> for what a device belongs to. D-27 deliberately
/// takes the latter from the database row rather than from the claim, because a write-side ownership
/// check that trusts the same token it is checking proves nothing. A third accessor would eventually
/// get a caller, and the interesting question — "which of these three is authoritative here?" — is one
/// nobody should have to ask. It was drafted with the property, and removed once nothing used it.
/// </para>
/// </summary>
public interface IDeviceContext
{
    /// <summary>The authenticated device's <c>Devices.Id</c>, or <c>null</c> when none is.</summary>
    Guid? DeviceId { get; }
}
