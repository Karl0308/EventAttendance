using EAMS.Api.Controllers;
using EAMS.Application.Abstractions;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace EAMS.Tests.Unit;

/// <summary>
/// The §6.2 outcome→status translation, guarded exactly as
/// <see cref="EventsControllerMappingTests"/> guards §6.3's — see
/// <see cref="AttendanceControllerMappingTests"/> for why a <c>_ =&gt; Ok(...)</c> fall-through is the
/// specific shape of bug this prevents.
///
/// <para>
/// In the unit suite because the mapping is a pure function of the enum: no host, no database, and it
/// stays in the loop a developer actually runs.
/// </para>
/// </summary>
public class StudentsControllerMappingTests
{
    [Fact]
    public void Every_declared_student_write_outcome_is_mapped()
    {
        foreach (var outcome in Enum.GetValues<StudentWriteOutcome>())
        {
            var status = StudentsController.StatusCodeFor(outcome);
            Assert.True(
                status is >= 200 and < 500,
                $"{outcome} maps to {status}. Every declared StudentWriteOutcome needs a deliberate " +
                "status code — a new one must be added to StudentsController.StatusCodeFor.");
        }
    }

    /// <summary>
    /// The property that matters more than any individual code: <b>a write that did not happen must
    /// not answer 2xx.</b> A caller that reads a 200 stops asking, so a refused student edit reported
    /// as success leaves an operator believing a roster correction landed.
    /// </summary>
    [Theory]
    [InlineData(StudentWriteOutcome.NotFound)]
    [InlineData(StudentWriteOutcome.ValidationFailed)]
    [InlineData(StudentWriteOutcome.FieldIsDerived)]
    [InlineData(StudentWriteOutcome.DuplicateStudentNumber)]
    [InlineData(StudentWriteOutcome.CardUidInUse)]
    [InlineData(StudentWriteOutcome.CardNotFound)]
    [InlineData(StudentWriteOutcome.NoSchoolResolved)]
    public void Every_outcome_other_than_saved_is_a_4xx(StudentWriteOutcome outcome)
    {
        var status = StudentsController.StatusCodeFor(outcome);

        Assert.True(
            status is >= 400 and < 500,
            $"{outcome} maps to {status}. Nothing but Saved may answer 2xx, and nothing here may " +
            "answer 5xx: every one of these is a decision the service made about a caller's request, " +
            "not a fault.");
    }

    [Fact]
    public void Saved_is_the_only_success() =>
        Assert.Equal(StatusCodes.Status200OK, StudentsController.StatusCodeFor(StudentWriteOutcome.Saved));

    /// <summary>
    /// The named build-plan requirement: a supplied ADR-001 D-2 cache column is a <b>400</b>. It is a
    /// malformed request under every circumstance — no state of the resource would accept it — which is
    /// what separates it from the two 409s, where the same payload becomes legal the moment the
    /// conflicting row is deleted or deactivated.
    /// </summary>
    [Fact]
    public void A_derived_field_is_400_and_a_uniqueness_clash_is_409()
    {
        Assert.Equal(StatusCodes.Status400BadRequest,
            StudentsController.StatusCodeFor(StudentWriteOutcome.FieldIsDerived));
        Assert.Equal(StatusCodes.Status400BadRequest,
            StudentsController.StatusCodeFor(StudentWriteOutcome.ValidationFailed));

        Assert.Equal(StatusCodes.Status409Conflict,
            StudentsController.StatusCodeFor(StudentWriteOutcome.DuplicateStudentNumber));
        Assert.Equal(StatusCodes.Status409Conflict,
            StudentsController.StatusCodeFor(StudentWriteOutcome.CardUidInUse));
        Assert.Equal(StatusCodes.Status409Conflict,
            StudentsController.StatusCodeFor(StudentWriteOutcome.NoSchoolResolved));
    }

    [Fact]
    public void An_undeclared_outcome_throws_rather_than_reporting_success() =>
        Assert.Throws<ArgumentOutOfRangeException>(
            () => StudentsController.StatusCodeFor((StudentWriteOutcome)999));
}
