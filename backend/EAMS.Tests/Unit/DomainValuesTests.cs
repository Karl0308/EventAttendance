using EAMS.Domain;
using Xunit;

namespace EAMS.Tests.Unit;

/// <summary>
/// The §4 closed value sets. These exist because <c>status=Banana</c> reached the database: there
/// was nowhere to look up what a valid status <em>was</em>, so nothing validated against it.
///
/// <para>
/// The behaviour worth pinning is not "rejects nonsense" — it is the pair of properties the rest of
/// the system leans on. Anything outside the set is rejected <em>before</em> it can reach a
/// <c>nvarchar(20)</c> column and come back as a truncation error, and anything inside it is handed
/// back in canonical spelling, because <c>EventService</c> counts its summary buckets with ordinal,
/// case-sensitive C# <c>==</c> and a stored <c>"present"</c> would be counted by none of them.
/// </para>
/// </summary>
public class DomainValuesTests
{
    [Theory]
    [InlineData("Present")]
    [InlineData("Late")]
    [InlineData("Absent")]
    [InlineData("Excused")]
    public void Every_documented_attendance_status_is_accepted_unchanged(string status)
    {
        Assert.True(AttendanceStatus.TryNormalize(status, out var canonical));
        Assert.Equal(status, canonical);
    }

    [Theory]
    [InlineData("present", "Present")]
    [InlineData("LATE", "Late")]
    [InlineData("eXcUsEd", "Excused")]
    [InlineData("  Absent  ", "Absent")]
    public void A_documented_status_in_any_casing_normalizes_to_its_canonical_spelling(
        string sent, string expected)
    {
        Assert.True(AttendanceStatus.TryNormalize(sent, out var canonical));
        Assert.Equal(expected, canonical);
    }

    [Theory]
    [InlineData("Banana")]
    [InlineData("Presnt")]
    [InlineData("Present ish")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Anything_outside_the_set_is_rejected(string? status)
    {
        Assert.False(AttendanceStatus.TryNormalize(status, out var canonical));
        Assert.Equal("", canonical);
    }

    /// <summary>
    /// The over-length case, which was the other half of the same defect: <c>Status</c> is
    /// <c>nvarchar(20)</c>, so an unvalidated long string became "String or binary data would be
    /// truncated" from SQL Server — a 500 for what is a 400. It is rejected here for the ordinary
    /// reason (it is not in the set), so no separate length rule is needed and none was added.
    /// </summary>
    [Fact]
    public void A_status_longer_than_the_column_is_rejected_before_it_can_reach_sql_server()
    {
        var overlong = new string('x', 500);

        Assert.False(AttendanceStatus.TryNormalize(overlong, out _));
    }

    /// <summary>
    /// The sets are what a caller enumerates to build a message or a dropdown, so their contents are
    /// part of the contract — not an implementation detail of the validator.
    /// </summary>
    [Fact]
    public void The_published_sets_match_the_technical_plan()
    {
        Assert.Equal(new[] { "Present", "Late", "Absent", "Excused" }, AttendanceStatus.All);
        Assert.Equal(new[] { "Rfid", "Manual", "Import" }, CaptureMethod.All);
        Assert.Equal(new[] { "Draft", "Open", "Closed", "Cancelled" }, EventStatus.All);
        Assert.Equal(new[] { "Single", "TimeInOut" }, AttendanceMode.All);
    }

    /// <summary>
    /// Each set validates only its own values. Cheap to assert and it pins the reason these are four
    /// types rather than one shared list: the compiler stops a status being checked against the
    /// event-status set.
    /// </summary>
    [Fact]
    public void The_sets_do_not_accept_each_others_values()
    {
        Assert.False(AttendanceStatus.TryNormalize(EventStatus.Open, out _));
        Assert.False(EventStatus.TryNormalize(AttendanceStatus.Present, out _));
        Assert.False(AttendanceMode.TryNormalize(CaptureMethod.Manual, out _));
    }
}
