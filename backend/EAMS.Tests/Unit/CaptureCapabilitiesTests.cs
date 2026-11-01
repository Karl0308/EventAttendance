using EAMS.Application.Abstractions;
using Microsoft.Extensions.Primitives;
using Xunit;

namespace EAMS.Tests.Unit;

/// <summary>
/// ADR-015 D-100's header syntax. The risky direction is switching v2 <b>on</b>, so only the exact token
/// does, after RFC 9110 list handling. Everything else reads as a client that said nothing, and nothing
/// here can refuse a request.
/// </summary>
public class CaptureCapabilitiesTests
{
    [Theory]
    [InlineData("personnel")]
    [InlineData("personnel ")] // the trailing space is list whitespace, trimmed before the match
    [InlineData(" personnel")]
    [InlineData("\tpersonnel\t")]
    [InlineData("future, personnel")]
    [InlineData("personnel,future")]
    [InlineData(",personnel,")]
    [InlineData(" , personnel , ,")]
    public void The_exact_token_switches_personnel_on(string value) =>
        Assert.True(CaptureCapabilities.Parse(new StringValues(value)).Personnel);

    [Theory]
    [InlineData("personel")]
    [InlineData("PERSONNEL")]
    [InlineData("PERSONNEL ")]
    [InlineData("Personnel")]
    [InlineData("personnel-v2")]
    [InlineData("person nel")]
    [InlineData("personnel;q=1")]
    [InlineData("unknown")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(",")]
    [InlineData(", ,")]
    public void Anything_else_reads_as_no_capability(string value) =>
        Assert.Equal(CaptureCapabilities.None, CaptureCapabilities.Parse(new StringValues(value)));

    /// <summary>The two-line form is what a proxy may produce, and RFC 9110 says it is one list.</summary>
    [Fact]
    public void Repeated_field_lines_merge_into_one_list()
    {
        string[] lines = ["future", "other, personnel"];

        Assert.True(CaptureCapabilities.Parse(new StringValues(lines)).Personnel);
    }

    [Fact]
    public void Repeated_field_lines_of_unknown_tokens_stay_off()
    {
        string[] lines = ["future", "PERSONNEL"];

        Assert.False(CaptureCapabilities.Parse(new StringValues(lines)).Personnel);
    }

    /// <summary>What <c>Request.Headers[...]</c> returns for a request that never sent the header.</summary>
    [Fact]
    public void An_absent_header_is_none() =>
        Assert.Equal(CaptureCapabilities.None, CaptureCapabilities.Parse(StringValues.Empty));

    [Fact]
    public void A_null_or_blank_line_is_skipped_not_thrown_on()
    {
        Assert.Equal(CaptureCapabilities.None, CaptureCapabilities.Parse(null));
        Assert.True(CaptureCapabilities.Parse([null, "", "personnel"]).Personnel);
    }
}
