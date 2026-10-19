using EAMS.Api.Reports;
using Xunit;

namespace EAMS.Tests.Unit;

/// <summary>
/// Pins <see cref="EventReportCsv.Cell"/> directly: always quoted, quotes doubled, and a formula trigger
/// neutralised BEFORE quoting so the apostrophe is inside the quotes (Excel only honours a quote that
/// opens the field).
/// </summary>
public class EventReportCsvCellTests
{
    [Theory]
    [InlineData("", "\"\"")]
    [InlineData("a b", "\"a b\"")]
    [InlineData("=1", "\"'=1\"")]
    [InlineData("a\"b", "\"a\"\"b\"")]
    [InlineData("@x", "\"'@x\"")]
    public void Cell_always_quotes_and_neutralises_inside_the_quotes(string input, string expected)
    {
        Assert.Equal(expected, EventReportCsv.Cell(input));
    }
}
