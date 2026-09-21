using EAMS.Domain;
using EAMS.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace EAMS.Infrastructure.Sis;

/// <summary>
/// Which ADR-001 D-4 profile version the NEXT upload for a school will be pinned to — asked by the
/// importer (<c>SisImportService.EnsureBuiltInProfileAsync</c>) and by the roster template, and answered
/// here once so the two cannot disagree.
///
/// <para>
/// <b>Why the template needs the importer's answer and not simply "the active row".</b> The importer does
/// not take whatever is active: an active version OLDER than
/// <see cref="SisImportProfileTemplate.BuiltInVersion"/> is refused (it would reinstate the 2026-07-30
/// REGNO-as-card defect), while a NEWER operator-authored one is deferred to. A template built from "the
/// active row" would, in the first case, advertise headers the importer is about to stop reading.
/// </para>
/// </summary>
internal static class SisImportProfileResolution
{
    /// <summary>The built-in profile's natural-key name, normalized as the profile table stores it.</summary>
    public static string BuiltInNameKey { get; } =
        AcademicKey.NormalizeOrUnspecified(SisImportProfileTemplate.ProfileName);

    /// <summary>
    /// The profile an upload for <paramref name="schoolId"/> would be pinned to, or <c>null</c> when the
    /// built-in version has never been written for that school — in which case the next upload creates it
    /// from <see cref="SisImportProfileTemplate.Entries"/>, so those entries are the answer.
    /// Read-only: it never creates or supersedes a row.
    /// </summary>
    public static async Task<SisImportProfile?> FindCurrentAsync(
        EamsDbContext db, Guid schoolId, CancellationToken ct)
    {
        var nameKey = BuiltInNameKey;
        var version = SisImportProfileTemplate.BuiltInVersion;

        var existing = await db.SisImportProfiles.IgnoreQueryFilters()
            .FirstOrDefaultAsync(
                p => p.SchoolId == schoolId && p.NameKey == nameKey && p.Version == version, ct);

        if (existing is null) return null;

        // Present but superseded by something newer an operator authored: that is their decision and
        // this method does not fight it. Reactivating would silently overrule a live mapping.
        if (existing.IsActive) return existing;

        // `p.Version > version` is load-bearing, not a tidy-up. Without it this accepts ANY active
        // version — including an OLDER one — which is the exact defect the version-matched lookup exists
        // to prevent, arriving through the fallback instead of through the lookup. A school whose
        // operator activated version 1 would have every NEW upload read the card serial out of the REGNO
        // column and reinstate the 2026-07-30 correction's defect, silently and on live data. Deferring
        // to a newer operator-authored version is deliberate; deferring to an older one is the bug.
        var newer = await db.SisImportProfiles.IgnoreQueryFilters()
            .Where(p => p.SchoolId == schoolId && p.NameKey == nameKey && p.IsActive
                     && p.Version > version)
            .OrderByDescending(p => p.Version)
            .FirstOrDefaultAsync(ct);

        // Falls back to the built-in row itself when the only active version is older — the importer
        // promises the built-in profile, and an older active version must not be allowed to satisfy it.
        return newer ?? existing;
    }
}
