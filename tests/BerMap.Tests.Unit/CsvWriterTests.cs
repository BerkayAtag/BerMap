using System.Text;
using BerMap.Application.Services.Export;

namespace BerMap.Tests.Unit;

public class CsvWriterTests
{
    [Theory]
    [InlineData(null, "\"\"")]
    [InlineData("", "\"\"")]
    [InlineData("plain", "\"plain\"")]
    [InlineData("a,b", "\"a,b\"")]
    [InlineData("say \"hi\"", "\"say \"\"hi\"\"\"")]
    [InlineData("line1\nline2", "\"line1\nline2\"")]
    public void Cell_IsAlwaysQuoted(string? value, string expected)
    {
        Assert.Equal(expected, CsvWriter.Cell(value));
    }

    [Theory]
    [InlineData("=1+1", "\"'=1+1\"")]
    [InlineData("+1", "\"'+1\"")]
    [InlineData("-1", "\"'-1\"")]
    [InlineData("@SUM(A1)", "\"'@SUM(A1)\"")]
    [InlineData("\tx", "\"'\tx\"")]
    [InlineData("\rx", "\"'\rx\"")]
    public void Cell_PrefixesFormulaStarts(string value, string expected)
    {
        Assert.Equal(expected, CsvWriter.Cell(value));
    }

    [Fact]
    public void Cell_KeepsSemicolonAndTabSeparatedPayloadsInOneQuotedCell()
    {
        // Under a ';' list separator (Turkish regional settings) an unquoted
        // value would be split and the part after ';' run as a formula.
        Assert.Equal("\"zz;=cmd|' /C calc'!A0\"", CsvWriter.Cell("zz;=cmd|' /C calc'!A0"));
        Assert.Equal("\"zz\t=1+1\"", CsvWriter.Cell("zz\t=1+1"));
    }

    [Fact]
    public void Row_JoinsQuotedCells_AndFileStartsWithBom()
    {
        var row = CsvWriter.Row(new[] { "createdAt", null, "=x" });
        Assert.Equal("\"createdAt\",\"\",\"'=x\"", row);

        var bytes = CsvWriter.ToUtf8WithBom(new StringBuilder(row));
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);
        Assert.Equal(row, Encoding.UTF8.GetString(bytes[3..]));
    }
}
