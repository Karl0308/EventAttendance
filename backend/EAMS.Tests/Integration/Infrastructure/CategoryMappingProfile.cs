using EAMS.Domain;
using EAMS.Infrastructure.Data;
using EAMS.Infrastructure.Sis;

namespace EAMS.Tests.Integration.Infrastructure;

/// <summary>
/// The import profile the dormant classification importer is exercised through.
///
/// <para>
/// <b>Why a fixture exists at all.</b> Student classification was withdrawn from the product surface
/// (ADR-009, D-76) and the built-in mapping (<see cref="SisImportProfileTemplate.BuiltInVersion"/>, and every
/// built-in version since ADR-009) maps no <c>StudentClassification.*</c> column. The importer's category code, the tables and the
/// roster template's vocabulary section are kept so the feature can come back, and untested code is not
/// safe to bring back — so those tests run against an operator-authored profile that maps the four
/// category columns, which is exactly how the feature would return (a new profile version, not a code
/// change).
/// </para>
///
/// <para>
/// <b>It seeds the built-in version as well, inactive, and that is load-bearing rather than tidy.</b>
/// <c>FindCurrentAsync</c> only defers to a newer operator profile when the built-in row for the
/// <em>current</em> built-in version already exists; seeding the newer profile alone would leave the
/// importer creating the built-in version itself and pinning to that.
/// </para>
/// </summary>
internal static class CategoryMappingProfile
{
    /// <summary>One above the built-in version: the operator profile that maps the category columns.</summary>
    public static int Version => SisImportProfileTemplate.BuiltInVersion + 1;

    /// <summary>
    /// Seeds the built-in version (inactive) and a newer active version carrying the built-in entries
    /// plus the four category rows. Returns the id of the newer, category-mapping profile.
    /// </summary>
    public static async Task<Guid> SeedCategoryMappingProfileAsync(
        EamsDbContext db, Guid schoolId, bool frozenCourseMapping = false)
    {
        // frozenCourseMapping: start from the frozen version 4 copy (CourseMappingProfile) instead of the
        // live template, for tests that also assert on courses, instructors or their warnings.
        var builtIn = frozenCourseMapping
            ? CourseMappingProfile.Entries.ToList()
            : SisImportProfileTemplate.Entries
                .Select(e => (e.SourceColumn, e.TargetField, e.NormalizationRule, e.IsRequired))
                .ToList();

        // Whatever category rows the built-in entries carry are dropped first, so the four below are the
        // only ones: a duplicate (profile, source column, target) row is a unique-index violation.
        var withCategories = builtIn
            .Where(m => !m.TargetField.StartsWith(SisImportProfileTemplate.ClassificationTargetPrefix, StringComparison.Ordinal))
            .Concat(SisRosterColumns.ClassificationColumns.Select(c => (
                SourceColumn: c.Column,
                TargetField: SisImportProfileTemplate.ClassificationTargetFor(c.Axis),
                NormalizationRule: "RosterClassification.Resolve",
                IsRequired: false)))
            .ToList();

        await SeedProfileAsync(db, schoolId, SisImportProfileTemplate.BuiltInVersion, isActive: false, builtIn);
        return await SeedProfileAsync(db, schoolId, Version, isActive: true, withCategories);
    }

    /// <summary>Writes one profile version and its column rows, in mapping order.</summary>
    public static async Task<Guid> SeedProfileAsync(
        EamsDbContext db, Guid schoolId, int version, bool isActive,
        IReadOnlyList<(string SourceColumn, string TargetField, string NormalizationRule, bool IsRequired)> mapping)
    {
        var profile = new SisImportProfile
        {
            SchoolId = schoolId,
            Name = SisImportProfileTemplate.ProfileName,
            NameKey = AcademicKey.NormalizeOrUnspecified(SisImportProfileTemplate.ProfileName),
            Version = version,
            Source = SisImportSource.Excel,
            IsActive = isActive,
            Description = $"Seeded version {version}.",
        };
        db.SisImportProfiles.Add(profile);

        for (var i = 0; i < mapping.Count; i++)
        {
            db.SisImportProfileColumns.Add(new SisImportProfileColumn
            {
                Profile = profile,
                SourceColumn = mapping[i].SourceColumn,
                SourceColumnKey = SisRosterColumns.HeaderKey(mapping[i].SourceColumn),
                TargetField = mapping[i].TargetField,
                NormalizationRule = mapping[i].NormalizationRule,
                IsRequired = mapping[i].IsRequired,
                Ordinal = i,
            });
        }

        await db.SaveChangesAsync();
        return profile.Id;
    }
}
