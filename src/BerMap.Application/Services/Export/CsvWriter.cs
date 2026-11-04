using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace BerMap.Application.Services.Export;

/// <summary>
/// RFC 4180 CSV for spreadsheet users. Values that a spreadsheet would run as
/// a formula (leading =, +, -, @, tab or CR) are prefixed with a single quote,
/// because names and details come from discovered systems and other users.
/// </summary>
public static class CsvWriter
{
    /// <summary>UTF-8 byte order mark; Excel needs it to read non-ASCII text correctly.</summary>
    public static ReadOnlySpan<byte> Utf8Bom => new byte[] { 0xEF, 0xBB, 0xBF };

    public static string Row(IEnumerable<string?> cells) =>
        string.Join(',', cells.Select(Cell));

    public static string Cell(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        if (value[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
            value = "'" + value;

        var needsQuotes = value.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0;
        return needsQuotes ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
    }

    public static byte[] ToUtf8WithBom(StringBuilder csv)
    {
        var body = Encoding.UTF8.GetBytes(csv.ToString());
        var result = new byte[Utf8Bom.Length + body.Length];
        Utf8Bom.CopyTo(result);
        body.CopyTo(result, Utf8Bom.Length);
        return result;
    }
}
