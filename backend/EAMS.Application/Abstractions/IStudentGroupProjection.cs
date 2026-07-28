namespace EAMS.Application.Abstractions;

/// <summary>
/// Materializes the academic structure of one term into §4.7 <c>StudentGroups</c> rows.
///
/// <para>
/// <b>What it is for.</b> §4.8 <c>EventGroups</c> and §12's "expected attendees" denominator are built
/// on <c>StudentGroups</c>, and they must keep working exactly as they do today. Rather than teach
/// them to understand colleges, programmes, sections and offerings, those four are projected
/// <em>into</em> the table they already read. An organizer picks "BSFS 2-A (2025-2026-1)" from the
/// same list as "SSC Officers"; nothing downstream learns that one of them is derived.
/// </para>
///
/// <para>
/// <b>It is a set-diff, and it is idempotent.</b> Running it twice in a row changes nothing the second
/// time — that is asserted, not assumed, because an import is re-run routinely (ADR-001 D-5 makes
/// batches re-runnable) and a projection that accumulated duplicate members would corrupt every
/// denominator built on it.
/// </para>
///
/// <para>
/// <b>It never touches manually-added members.</b> Membership rows carry their own provenance; the
/// diff only ever removes rows it owns. An adviser hand-added to a section's group survives every
/// subsequent run.
/// </para>
///
/// <para>
/// <b>It never deletes groups.</b> A group whose academic source has gone away keeps its row and loses
/// its derived members. Deleting it would break any historical <c>EventGroups</c> row pointing at it
/// (every FK in this model is <c>Restrict</c>, so it would not even succeed) and would erase the
/// record of who was invited to a past event.
/// </para>
/// </summary>
public interface IStudentGroupProjection
{
    /// <summary>
    /// Reconciles every derived group for one term against the academic tables.
    /// </summary>
    /// <param name="termId">The term to project. Must exist; an unknown id throws.</param>
    Task<GroupProjectionResult> SyncTermAsync(Guid termId, CancellationToken ct = default);
}

/// <summary>
/// What one projection run changed. All four are zero on a re-run over unchanged data, which is the
/// executable statement of the idempotency contract.
/// </summary>
/// <param name="GroupsCreated">Derived groups that did not exist before this run.</param>
/// <param name="GroupsUpdated">
/// Existing derived groups whose name, type, or source entity changed. Stamping <c>LastSyncedAt</c>
/// is deliberately not counted — it happens on every run, so counting it would make the result
/// non-zero even when nothing changed and the number would stop meaning anything.
/// </param>
/// <param name="MembersAdded">Memberships added, always as derived rows.</param>
/// <param name="MembersRemoved">
/// Derived memberships removed because the student no longer qualifies. Manual memberships are never
/// counted here because they are never removed.
/// </param>
public record GroupProjectionResult(
    int GroupsCreated, int GroupsUpdated, int MembersAdded, int MembersRemoved)
{
    /// <summary>True when the run found nothing to do — the expected result of a second consecutive run.</summary>
    public bool IsNoOp =>
        GroupsCreated == 0 && GroupsUpdated == 0 && MembersAdded == 0 && MembersRemoved == 0;
}
