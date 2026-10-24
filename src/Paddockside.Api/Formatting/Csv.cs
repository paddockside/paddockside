using System.Text;

namespace Paddockside.Api.Formatting;

/// <summary>Spreadsheet downloads: quoted cells, CRLF lines, and a byte-order mark so Excel reads the file as UTF-8.</summary>
public static class Csv
{
    /// <summary>A CSV cell: quoted, and never starting with a character a spreadsheet would run as a formula.</summary>
    public static string Cell(string? value)
    {
        var text = value ?? "";
        if (text.Length > 0 && "=+-@\t\r".Contains(text[0])) text = "'" + text;
        return $"\"{text.Replace("\"", "\"\"")}\"";
    }

    /// <summary>Rows of cells (each already made with <see cref="Cell"/> where it holds free text) as a download.</summary>
    public static IResult File(IEnumerable<IEnumerable<string>> rows, string fileName)
    {
        var csv = new StringBuilder();
        foreach (var row in rows) csv.AppendJoin(',', row).Append("\r\n");
        return Results.File(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(csv.ToString())).ToArray(), "text/csv", fileName);
    }
}
