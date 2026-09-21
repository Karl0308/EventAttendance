namespace EAMS.Application.Abstractions;

/// <summary>
/// Task 5 (QA MDVault #470 B1, #472 Q1/Q2): the downloadable roster template the school fills in and
/// uploads back through <see cref="ISisImportService.UploadAsync"/>.
///
/// <para>
/// <b>Generated, never a committed file.</b> Its headers are the source columns of the import profile
/// the school's next upload will be pinned to (ADR-001 D-4), read from the same rows the importer reads,
/// plus any header the reader needs to recognise the roster sheet that the profile omits. A newer active
/// profile version changes the columns; the headers the reader requires and the fixed headers the importer
/// reads its core values from are code, and a template cannot change them.
/// </para>
/// </summary>
public interface ISisImportTemplateService
{
    /// <summary>
    /// Builds the <c>.xlsx</c> for the caller's school. Returns <c>null</c> when no school resolves.
    /// The stream is positioned at its start and owned by the caller.
    /// </summary>
    Task<Stream?> BuildAsync(CancellationToken ct = default);
}
