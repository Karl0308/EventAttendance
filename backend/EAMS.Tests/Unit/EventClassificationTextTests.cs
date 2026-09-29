using EAMS.Domain;
using Xunit;

namespace EAMS.Tests.Unit;

/// <summary>
/// The <c>EventClassifications</c> column rules — the pure half of the vocabulary, verified without a
/// database. The HTTP behaviour these underpin is <c>EventClassificationAdminApiTests</c>' subject.
/// </summary>
public class EventClassificationTextTests
{
    [Theory]
    [InlineData("Institutional Events")]
    [InlineData("Departmental Events")]
    [InlineData("A")]
    [InlineData("Sports Fest 2026")]
    public void A_well_formed_name_is_valid(string name) =>
        Assert.True(EventClassificationText.IsValidName(name));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(" Institutional")]      // leading whitespace is refused, not trimmed
    [InlineData("Departmental ")]       // trailing whitespace is refused, not trimmed
    [InlineData("///")]                  // no letter or digit, so no key
    public void A_name_that_breaks_a_rule_is_refused(string? name) =>
        Assert.False(EventClassificationText.IsValidName(name));

    [Fact]
    public void A_name_longer_than_the_column_is_refused() =>
        Assert.False(EventClassificationText.IsValidName(new string('x', EventClassificationText.NameMaxLength + 1)));

    [Fact]
    public void A_name_at_the_column_limit_is_valid() =>
        Assert.True(EventClassificationText.IsValidName(new string('x', EventClassificationText.NameMaxLength)));

    [Theory]
    [InlineData(null)]                   // a null description is valid — the field is optional
    [InlineData("")]
    [InlineData("Captures attendance for all university stakeholders.")]
    public void A_description_within_the_column_is_valid(string? description) =>
        Assert.True(EventClassificationText.IsValidDescription(description));

    [Fact]
    public void A_description_longer_than_the_column_is_refused() =>
        Assert.False(
            EventClassificationText.IsValidDescription(
                new string('x', EventClassificationText.DescriptionMaxLength + 1)));

    [Theory]
    [InlineData("Departmental Events", "DEPARTMENTALEVENTS")]
    [InlineData("departmental-events", "DEPARTMENTALEVENTS")]
    [InlineData("  Departmental   Events  ", "DEPARTMENTALEVENTS")]
    public void Names_collide_on_their_normalized_key(string name, string expectedKey) =>
        Assert.Equal(expectedKey, EventClassificationText.KeyFor(name));

    [Fact]
    public void The_three_seeded_names_are_recognised_by_their_key()
    {
        foreach (var seed in EventClassificationSeedValues.All)
        {
            Assert.True(
                EventClassificationSeedValues.IsSeededKey(EventClassificationText.KeyFor(seed.Name)),
                $"'{seed.Name}' is a seeded value but IsSeededKey did not recognise its key.");
        }

        Assert.False(EventClassificationSeedValues.IsSeededKey(EventClassificationText.KeyFor("Ad-hoc Event")));
    }

    [Fact]
    public void The_seed_list_has_no_duplicate_keys()
    {
        var keys = EventClassificationSeedValues.All
            .Select(s => EventClassificationText.KeyFor(s.Name))
            .ToList();

        Assert.Equal(keys.Count, keys.Distinct(StringComparer.Ordinal).Count());
    }
}
