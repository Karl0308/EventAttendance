using EAMS.Api.Controllers;
using EAMS.Application.Abstractions;
using EAMS.Domain;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace EAMS.Tests.Unit;

/// <summary>
/// The outcome→status translations for the two surfaces this phase added, guarded exactly as
/// <see cref="StudentsControllerMappingTests"/> guards §6.2's — see
/// <see cref="AttendanceControllerMappingTests"/> for why a <c>_ =&gt; Ok(...)</c> fall-through is the
/// specific shape of bug this prevents.
///
/// <para>
/// In the unit suite because both mappings are pure functions of an enum: no host, no database, and
/// they stay in the loop a developer actually runs.
/// </para>
/// </summary>
public class ClassificationAssignmentMappingTests
{
    // --------------------------------------------------- /students/{id}/classifications

    [Fact]
    public void Every_declared_assignment_outcome_is_mapped()
    {
        foreach (var outcome in Enum.GetValues<StudentClassificationWriteOutcome>())
        {
            var status = StudentClassificationsController.StatusCodeFor(outcome);

            Assert.True(
                status is >= 200 and < 500,
                $"{outcome} maps to {status}. Every declared StudentClassificationWriteOutcome needs a " +
                "deliberate status code — a new one must be added to " +
                "StudentClassificationsController.StatusCodeFor.");
        }
    }

    /// <summary>
    /// The property that matters more than any individual code: <b>a write that did not happen must not
    /// answer 2xx.</b> A caller that reads a 200 stops asking, so a refused assignment reported as
    /// success leaves an operator believing somebody was reclassified.
    /// </summary>
    [Theory]
    [InlineData(StudentClassificationWriteOutcome.StudentNotFound)]
    [InlineData(StudentClassificationWriteOutcome.ClassificationNotFound)]
    [InlineData(StudentClassificationWriteOutcome.CrossSchool)]
    [InlineData(StudentClassificationWriteOutcome.NotAssigned)]
    [InlineData(StudentClassificationWriteOutcome.ClassificationRetired)]
    [InlineData(StudentClassificationWriteOutcome.ClassificationMerged)]
    [InlineData(StudentClassificationWriteOutcome.ConcurrentAssignment)]
    public void Every_assignment_outcome_other_than_saved_is_a_4xx(
        StudentClassificationWriteOutcome outcome)
    {
        var status = StudentClassificationsController.StatusCodeFor(outcome);

        Assert.True(
            status is >= 400 and < 500,
            $"{outcome} maps to {status}. Nothing but Saved may answer 2xx, and nothing here may answer " +
            "5xx: every one of these is a decision the service made about a caller's request, not a " +
            "fault. ConcurrentAssignment in particular is a lost race the caller can simply repeat.");
    }

    [Fact]
    public void Saved_is_the_only_assignment_success() =>
        Assert.Equal(
            StatusCodes.Status200OK,
            StudentClassificationsController.StatusCodeFor(StudentClassificationWriteOutcome.Saved));

    /// <summary>
    /// <b>The 404/409 split, pinned by name.</b> The four 404s name something a URL claims exists; the
    /// three 409s reject a well-formed request that the state of the vocabulary or of this person's axis
    /// forbids, and each would be accepted after a reactivation, a different choice, or a re-read.
    /// </summary>
    [Fact]
    public void Missing_things_are_404_and_state_conflicts_are_409()
    {
        foreach (var missing in new[]
                 {
                     StudentClassificationWriteOutcome.StudentNotFound,
                     StudentClassificationWriteOutcome.ClassificationNotFound,
                     StudentClassificationWriteOutcome.CrossSchool,
                     StudentClassificationWriteOutcome.NotAssigned,
                 })
        {
            Assert.Equal(
                StatusCodes.Status404NotFound,
                StudentClassificationsController.StatusCodeFor(missing));
        }

        foreach (var conflict in new[]
                 {
                     StudentClassificationWriteOutcome.ClassificationRetired,
                     StudentClassificationWriteOutcome.ClassificationMerged,
                     StudentClassificationWriteOutcome.ConcurrentAssignment,
                 })
        {
            Assert.Equal(
                StatusCodes.Status409Conflict,
                StudentClassificationsController.StatusCodeFor(conflict));
        }
    }

    [Fact]
    public void An_undeclared_assignment_outcome_throws_rather_than_reporting_success() =>
        Assert.Throws<ArgumentOutOfRangeException>(
            () => StudentClassificationsController.StatusCodeFor(
                (StudentClassificationWriteOutcome)999));

    // ------------------------------------------------------------------------- /cards

    [Fact]
    public void Every_declared_card_search_outcome_is_mapped()
    {
        foreach (var outcome in Enum.GetValues<CardSearchOutcome>())
        {
            var status = CardsController.StatusCodeFor(outcome);

            Assert.True(
                status is >= 200 and < 500,
                $"{outcome} maps to {status}. Every declared CardSearchOutcome needs a deliberate " +
                "status code — a new one must be added to CardsController.StatusCodeFor.");
        }
    }

    /// <summary>
    /// <b>An unusable fragment is a 400 and not an empty 200</b>, which is the one decision this tiny
    /// mapping makes: an empty page would read as "no such card", a fact about the roster, when the
    /// truth is a fact about the request — and the request in question is the one that would otherwise
    /// have matched every card in the school.
    /// </summary>
    [Fact]
    public void An_unusable_fragment_is_400_and_a_ran_search_is_200()
    {
        Assert.Equal(
            StatusCodes.Status200OK, CardsController.StatusCodeFor(CardSearchOutcome.Matched));

        Assert.Equal(
            StatusCodes.Status400BadRequest,
            CardsController.StatusCodeFor(CardSearchOutcome.FragmentUnusable));
    }

    [Fact]
    public void An_undeclared_card_search_outcome_throws_rather_than_reporting_success() =>
        Assert.Throws<ArgumentOutOfRangeException>(
            () => CardsController.StatusCodeFor((CardSearchOutcome)999));

    // ------------------------------------------------------- the domain seam the importer shares

    /// <summary>
    /// <b><see cref="ClassificationAssignment.For"/> copies the axis off the parent and cannot be made
    /// to do anything else.</b>
    ///
    /// <para>
    /// It is the whole of the denormalization invariant, and it is here — a pure function, in the unit
    /// suite — because Phase 1b's importer is about to become its second caller. The importer cannot
    /// reuse the service (it saves once per batch, not once per person), so this is the piece that has
    /// to be shared, and a test that runs without a database is the one that will still be run when
    /// somebody changes it.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(ClassificationAxis.Student)]
    [InlineData(ClassificationAxis.Personnel)]
    [InlineData(ClassificationAxis.Friars)]
    [InlineData(ClassificationAxis.Special)]
    public void An_assignment_takes_its_axis_from_the_classification(string axis)
    {
        var classification = new Classification { Id = Guid.NewGuid(), Axis = axis, Name = "X" };
        var studentId = Guid.NewGuid();

        var row = ClassificationAssignment.For(studentId, classification);

        Assert.Equal(studentId, row.StudentId);
        Assert.Equal(classification.Id, row.ClassificationId);
        Assert.Equal(axis, row.Axis);
    }

    /// <summary>
    /// <b>Both "not offered" states are covered, and the merged one is covered <em>independently</em> of
    /// the retired one.</b>
    ///
    /// <para>
    /// <c>CK_Classifications_MergedIsRetired</c> means a merged row is always retired in the database,
    /// so a predicate that only checked <c>IsActive</c> would be accidentally correct today. This asserts
    /// the merged case against a row that is still marked active — a state the constraint forbids and
    /// that therefore cannot arise in production — precisely so the accident is not what is being
    /// tested.
    /// </para>
    /// </summary>
    [Fact]
    public void A_retired_or_merged_classification_is_not_assignable()
    {
        Assert.True(ClassificationAssignment.IsAssignable(
            new Classification { IsActive = true }));

        Assert.False(ClassificationAssignment.IsAssignable(
            new Classification { IsActive = false }));

        Assert.False(
            ClassificationAssignment.IsAssignable(new Classification
            {
                IsActive = true,
                MergedIntoClassificationId = Guid.NewGuid(),
            }),
            "A merged-away classification was reported assignable because it was still marked active. " +
            "CK_Classifications_MergedIsRetired makes that pair impossible in the database, which is " +
            "exactly why the predicate must not depend on it.");
    }
}
