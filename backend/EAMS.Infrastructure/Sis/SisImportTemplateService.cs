using EAMS.Application.Abstractions;
using EAMS.Domain;
using EAMS.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace EAMS.Infrastructure.Sis;

/// <summary>
/// Task 5's downloadable roster template. Reads the profile the school's next upload will be pinned to
/// and the school's classification vocabulary, and hands both to <see cref="SisImportTemplateWorkbook"/>.
///
/// <para>
/// <b>A separate service rather than a fifth method on <c>SisImportService</c></b>, because the two need
/// different things: the importer takes its school from the term it is given, while the template has no
/// term and must ask <see cref="ISchoolContext"/>. It shares the one rule that matters —
/// <see cref="SisImportProfileResolution"/> — rather than the class.
/// </para>
/// </summary>
internal sealed class SisImportTemplateService : ISisImportTemplateService
{
    private readonly EamsDbContext _db;
    private readonly ISchoolContext _school;

    public SisImportTemplateService(EamsDbContext db, ISchoolContext school)
    {
        _db = db;
        _school = school;
    }

    public async Task<Stream?> BuildAsync(CancellationToken ct = default)
    {
        var schoolId = await _db.ResolveSchoolIdAsync(_school, ct);
        if (schoolId is null) return null;

        var profile = await SisImportProfileResolution.FindCurrentAsync(_db, schoolId.Value, ct);

        IReadOnlyList<SisImportTemplateWorkbook.ProfileRow> rows;
        string profileName;
        int profileVersion;

        if (profile is null)
        {
            // Never uploaded to: the next upload writes the built-in version from these very entries,
            // so they are the rows that upload will be pinned to.
            rows =
            [
                .. SisImportProfileTemplate.Entries.Select(e => new SisImportTemplateWorkbook.ProfileRow(
                    e.SourceColumn, SisRosterColumns.HeaderKey(e.SourceColumn), e.TargetField, e.IsRequired)),
            ];
            profileName = SisImportProfileTemplate.ProfileName;
            profileVersion = SisImportProfileTemplate.BuiltInVersion;
        }
        else
        {
            rows = await _db.SisImportProfileColumns.IgnoreQueryFilters()
                .AsNoTracking()
                .Where(c => c.ProfileId == profile.Id)
                .OrderBy(c => c.Ordinal)
                .Select(c => new SisImportTemplateWorkbook.ProfileRow(
                    c.SourceColumn, c.SourceColumnKey, c.TargetField, c.IsRequired))
                .ToListAsync(ct);
            profileName = profile.Name;
            profileVersion = profile.Version;
        }

        // Only what an import can actually assign: a retired or merged-away entry is refused by the
        // importer (ClassificationAssignment.IsAssignable), so listing it as accepted would be a lie.
        var vocabulary = await _db.Classifications.IgnoreQueryFilters()
            .AsNoTracking()
            .Where(c => c.SchoolId == schoolId.Value && c.IsActive && c.MergedIntoClassificationId == null)
            .OrderBy(c => c.Name)
            .Select(c => new SisImportTemplateWorkbook.VocabularyEntry(c.Axis, c.Name))
            .ToListAsync(ct);

        return SisImportTemplateWorkbook.Build(profileName, profileVersion, rows, vocabulary);
    }
}
