using System.Text.Encodings.Web;
using System.Text.Json;

namespace acu_cli;

/// <summary>Output helpers: pretty JSON and a compact read-only table for entity records.</summary>
internal static class Output
{
    public static readonly JsonSerializerOptions PrettyJsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static void PrintJson(JsonElement element) =>
        Console.WriteLine(JsonSerializer.Serialize(element, PrettyJsonOptions));

    /// <summary>
    /// Renders an entity (object) or entity list (array of objects) as a table.
    /// Acumatica fields look like {"value": ...}; the value is shown, nested arrays
    /// are summarized as [N].
    /// </summary>
    public static void PrintTable(JsonElement element)
    {
        var rows = element.ValueKind == JsonValueKind.Array
            ? element.EnumerateArray().ToList()
            : [element];
        rows = rows.Where(r => r.ValueKind == JsonValueKind.Object).ToList();

        if (rows.Count == 0)
        {
            Console.WriteLine("(no results)");
            return;
        }

        var columns = new List<string>();
        foreach (var row in rows)
        {
            foreach (var property in row.EnumerateObject())
            {
                if (!columns.Contains(property.Name))
                    columns.Add(property.Name);
            }
        }

        var cells = rows
            .Select(row => columns
                .Select(column => row.TryGetProperty(column, out var value) ? Cell(value) : "")
                .ToArray())
            .ToList();

        const int maxWidth = 38;
        var widths = columns
            .Select((name, index) => Math.Min(maxWidth, Math.Max(
                name.Length,
                cells.Max(row => Math.Min(row[index].Length, maxWidth)))))
            .ToArray();

        Console.WriteLine(RowText(columns.Select((c, i) => (c, i)), widths));
        Console.WriteLine(string.Join("  ", widths.Select(w => new string('-', w))));
        foreach (var row in cells)
        {
            Console.WriteLine(RowText(row.Select((cell, i) => (cell, i)), widths));
        }

        static string RowText(IEnumerable<(string Text, int Index)> cells, int[] widths) =>
            string.Join("  ", cells.Select(cell => Truncate(cell.Text, widths[cell.Index]).PadRight(widths[cell.Index])));
    }

    private static string Cell(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => element.TryGetProperty("value", out var value)
            ? Cell(value)
            : (element.EnumerateObject().Any() ? element.GetRawText() : ""),
        JsonValueKind.Array => $"[{element.GetArrayLength()}]",
        JsonValueKind.String => element.GetString() ?? "",
        JsonValueKind.Null => "",
        _ => element.GetRawText(),
    };

    private static string Truncate(string value, int maxWidth) =>
        value.Length <= maxWidth ? value : value[..(maxWidth - 1)] + "\u2026";
}
