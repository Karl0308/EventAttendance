using EAMS.Domain;
using Xunit;

namespace EAMS.Tests.Unit;

/// <summary>
/// D-50's closed registry of filterable audience fields, guarded in the shape
/// <see cref="DomainValuesTests"/> established for the §4 value sets — and for a sharper reason than
/// they have.
///
/// <para>
/// <b>The §4 sets are closed so a bad value cannot reach a narrow column. This one is closed so three
/// specific columns cannot be reached at all.</b> <c>Students.Course</c>, <c>Students.YearLevel</c> and
/// <c>Students.Section</c> are the ADR-001 D-2 display cache: single-valued, and wrong for the twelve
/// of fifty-two real students who sit in more than one section. Every member of
/// <see cref="AudienceField.All"/> resolves through <c>StudentTermRecords</c> or <c>Enrollments</c>
/// instead, so what this file actually pins is the <em>size</em> of the list — a sixth member added
/// here without an arm in <c>EventService.MatchingPlacements</c> is a filter row that throws, and a
/// sixth member pointing at a cache column is the defect the registry exists to make impossible.
/// </para>
///
/// <para>
/// In the unit suite because <see cref="AudienceField.TryNormalize"/> is a pure function over a static
/// list: no host, no database, and it stays in the loop a developer actually runs.
/// </para>
/// </summary>
public class AudienceFieldTests
{
    /// <summary>
    /// The list is contract, not an implementation detail: it is what the resolver's refusal message
    /// enumerates and what a filter builder populates its field picker from. Asserted as a whole,
    /// in order, so <b>both</b> directions fail loudly — a field quietly added is as much a change to
    /// the published surface as a field quietly removed.
    /// </summary>
    [Fact]
    public void The_registry_is_exactly_the_five_documented_fields() =>
        Assert.Equal(
            new[] { "College", "Program", "YearLevel", "Section", "Course" },
            AudienceField.All);

    // Note that `Course`, `YearLevel` and `Section` are registered *names* that happen to match three
    // ADR-001 D-2 cache columns. That they resolve through the academic tables rather than through those
    // columns is not assertable from here — it is a property of the query EventService composes, and
    // EventAudienceResolveTests proves it over HTTP against a fixture whose cache column deliberately
    // disagrees with its enrolments.

    [Theory]
    [InlineData("College")]
    [InlineData("Program")]
    [InlineData("YearLevel")]
    [InlineData("Section")]
    [InlineData("Course")]
    public void Every_documented_field_is_accepted_unchanged(string field)
    {
        Assert.True(AudienceField.TryNormalize(field, out var canonical));
        Assert.Equal(field, canonical);
    }

    /// <summary>
    /// Casing normalizes for the two reasons <see cref="AudienceField.TryNormalize"/> records, and the
    /// first is the one with teeth: <c>MatchingPlacements</c> switches on these with ordinal, case-
    /// sensitive <c>==</c>, so a <c>"section"</c> that reached the switch unnormalized would land in the
    /// throwing arm — a 500 for a request a browser sends every day.
    /// </summary>
    [Theory]
    [InlineData("section", "Section")]
    [InlineData("SECTION", "Section")]
    [InlineData("yearlevel", "YearLevel")]
    [InlineData("YEARLEVEL", "YearLevel")]
    [InlineData("cOlLeGe", "College")]
    [InlineData("  Program  ", "Program")]
    [InlineData("course", "Course")]
    public void A_documented_field_in_any_casing_normalizes_to_its_canonical_spelling(
        string sent, string expected)
    {
        Assert.True(AudienceField.TryNormalize(sent, out var canonical));
        Assert.Equal(expected, canonical);
    }

    /// <summary>
    /// Everything outside the registry is refused. <c>"Status"</c>, <c>"Gender"</c> and
    /// <c>"HasCard"</c> are in this list deliberately: all three were considered for the registry and
    /// deferred, so they are the likeliest fields for a client to try, and each must come back a 400
    /// rather than a filter row that silently stops filtering.
    /// </summary>
    [Theory]
    [InlineData("Banana")]
    [InlineData("Sections")]
    [InlineData("Year Level")]
    [InlineData("Status")]
    [InlineData("Gender")]
    [InlineData("HasCard")]
    [InlineData("StudentNumber")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Anything_outside_the_registry_is_refused(string? field)
    {
        Assert.False(AudienceField.TryNormalize(field, out var canonical));
        Assert.Equal("", canonical);
    }

    /// <summary>
    /// The three published ceilings, pinned because several separate things read them and are only
    /// correct if they agree: the resolver's <c>Take</c>, the <c>studentIdLimit</c> it echoes to the
    /// caller, the truncation flag, and — for <see cref="AudienceResolutionLimits.MaxFilterRows"/> —
    /// the refusal's own <c>detail</c>, which quotes the number back to a caller trimming rows. A change
    /// to any of the three is a change to the wire contract.
    ///
    /// <para>
    /// Two of them bound the answer and one bounds the question, so only the first two relate to each
    /// other. <c>MaxFilterRows</c> gets the one relational claim its own documentation makes: it is at
    /// least the size of the field registry, since a filter set naming every filterable field once is
    /// the smallest complete filter a builder can construct and a ceiling under that would refuse it.
    /// It is deliberately <em>not</em> related to the two response bounds — an oversized answer is
    /// still an answer, an oversized question never becomes one.
    /// </para>
    /// </summary>
    [Fact]
    public void The_published_ceilings_are_the_documented_numbers()
    {
        Assert.Equal(5_000, AudienceResolutionLimits.MaxStudentIds);
        Assert.Equal(25, AudienceResolutionLimits.SampleSize);
        Assert.Equal(20, AudienceResolutionLimits.MaxFilterRows);
        Assert.True(
            AudienceResolutionLimits.SampleSize < AudienceResolutionLimits.MaxStudentIds,
            "The sample is a prefix of studentIds, so a sample larger than the id ceiling would be a " +
            "list that cannot be a prefix of the one it previews.");
        Assert.True(
            AudienceResolutionLimits.MaxFilterRows >= AudienceField.All.Count,
            "One row per filterable field is the smallest filter set that names them all, so a row " +
            "ceiling below the size of the registry would refuse a request the registry invites.");
    }
}
