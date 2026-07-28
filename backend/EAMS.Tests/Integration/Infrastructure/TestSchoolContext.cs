using EAMS.Application.Abstractions;

namespace EAMS.Tests.Integration.Infrastructure;

/// <summary>
/// A settable <see cref="ISchoolContext"/>. The production implementation pins its tenant once at
/// startup and is deliberately write-once; tests need to move the tenant between assertions to show
/// the global query filter both hides another school's rows and hides nothing when only one exists.
///
/// <para>
/// <c>null</c> means "do not filter", matching the interface's documented contract — so a test that
/// leaves it unset sees every row, and the tenant behaviour under test is always the one a test
/// asked for explicitly.
/// </para>
/// </summary>
public sealed class TestSchoolContext : ISchoolContext
{
    public Guid? CurrentSchoolId { get; set; }
}
