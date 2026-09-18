using System.Globalization;
using System.Text;
using System.Text.Json;

namespace acu_cli;

/// <summary>
/// YAML conversion for agent-friendly payloads, implemented without a YAML dependency.
///
/// Output: Acumatica's field wrappers are flattened — {"InventoryID": {"value": "X"}}
/// becomes "InventoryID: X"; a field error becomes a sibling "InventoryIDError: ...".
/// Input: a YAML subset (block mappings, block sequences, scalars, comments) or plain
/// JSON. Bare fields are re-wrapped into the API's {"value": ...} format; explicit
/// {"value": ...} objects pass through untouched.
/// </summary>
internal static class YamlFormat
{
    // -------------------------------------------------- JSON (API) -> YAML (output)

    public static string ToYaml(JsonElement element)
    {
        var builder = new StringBuilder();
        WriteNode(builder, Flatten(element), indent: 0, firstLinePrefix: null);
        return builder.ToString().TrimEnd();
    }

    public static void PrintYaml(JsonElement element) => Console.WriteLine(ToYaml(element));

    private static void WriteNode(StringBuilder sb, object? node, int indent, string? firstLinePrefix)
    {
        switch (node)
        {
            case Dictionary<string, object?> mapping:
                WriteMapping(sb, mapping, indent, firstLinePrefix);
                return;
            case List<object?> sequence:
                WriteSequence(sb, sequence, indent, firstLinePrefix);
                return;
            default:
                sb.Append(CreateIndent(indent)).Append(firstLinePrefix ?? "").Append(FormatScalar(node)).AppendLine();
                return;
        }
    }

    private static void WriteMapping(StringBuilder sb, Dictionary<string, object?> mapping, int indent, string? firstLinePrefix)
    {
        if (mapping.Count == 0)
        {
            sb.Append(CreateIndent(indent)).Append(firstLinePrefix ?? "").AppendLine("{}");
            return;
        }

        var first = true;
        foreach (var entry in mapping)
        {
            var prefix = first && firstLinePrefix is not null ? firstLinePrefix : CreateIndent(indent);
            first = false;

            switch (entry.Value)
            {
                case Dictionary<string, object?> nested when nested.Count > 0:
                    sb.Append(prefix).Append(FormatKey(entry.Key)).AppendLine(":");
                    WriteMapping(sb, nested, indent + 2, null);
                    break;
                case List<object?> list when list.Count > 0:
                    sb.Append(prefix).Append(FormatKey(entry.Key)).AppendLine(":");
                    WriteSequence(sb, list, indent + 2, null);
                    break;
                default:
                    sb.Append(prefix).Append(FormatKey(entry.Key)).Append(": ")
                      .Append(FormatScalar(entry.Value is Dictionary<string, object?> { Count: 0 } ? null : entry.Value))
                      .AppendLine();
                    break;
            }
        }
    }

    private static void WriteSequence(StringBuilder sb, List<object?> sequence, int indent, string? firstLinePrefix)
    {
        if (sequence.Count == 0)
        {
            sb.Append(CreateIndent(indent)).Append(firstLinePrefix ?? "").AppendLine("[]");
            return;
        }

        var first = true;
        foreach (var item in sequence)
        {
            var prefix = first && firstLinePrefix is not null ? firstLinePrefix : CreateIndent(indent) + "- ";
            first = false;
            WriteNode(sb, item, indent + 2, prefix);
        }
    }

    private static string CreateIndent(int indent) => new(' ', indent);

    // -------------------------------------------------- flattening (value/error)

    private static object? Flatten(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => ToPlainObject(element),
        JsonValueKind.Array => element.EnumerateArray().Select(Flatten).ToList(),
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number => NumberToGraph(element),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        _ => element.GetRawText(),
    };

    private static object NumberToGraph(JsonElement element)
    {
        var raw = element.GetRawText();
        if (raw.Contains('.') || raw.Contains('e') || raw.Contains('E'))
            return element.GetDecimal();
        return element.TryGetInt64(out var value) ? value : element.GetDecimal();
    }

    private static Dictionary<string, object?> ToPlainObject(JsonElement element)
    {
        var result = new Dictionary<string, object?>();
        foreach (var property in element.EnumerateObject())
        {
            FlattenProperty(result, property.Name, property.Value);
        }
        return result;
    }

    private static void FlattenProperty(Dictionary<string, object?> result, string name, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object && IsFieldWrapper(value))
        {
            var hasValue = false;
            foreach (var inner in value.EnumerateObject())
            {
                if (inner.Name == "value")
                {
                    result[name] = Flatten(inner.Value);
                    hasValue = true;
                }
                else if (inner.Name == "error"
                    && inner.Value.ValueKind == JsonValueKind.String
                    && !string.IsNullOrEmpty(inner.Value.GetString()))
                {
                    result[name + "Error"] = inner.Value.GetString();
                }
            }
            if (!hasValue)
                result[name] = null;
            return;
        }

        result[name] = Flatten(value);
    }

    private static bool IsFieldWrapper(JsonElement value)
    {
        var hasValue = false;
        var count = 0;
        foreach (var property in value.EnumerateObject())
        {
            if (property.Name != "value" && property.Name != "error")
                return false;
            if (property.Name == "value")
                hasValue = true;
            count++;
        }
        return count > 0 && (hasValue || count == 1);
    }

    // -------------------------------------------------- input -> JSON (API body)

    /// <summary>
    /// Converts a YAML (or JSON) request body into the Acumatica JSON format: bare
    /// scalars become {"value": scalar}, explicit wrapper objects pass through, and
    /// container objects (like an action's `entity`) are recursed into.
    /// </summary>
    public static string YamlToAcuJson(string body)
    {
        var graph = ParseInput(body);
        return JsonSerializer.Serialize(WrapFields(graph));
    }

    private static object? ParseInput(string body)
    {
        var trimmed = body.TrimStart();
        if (trimmed.StartsWith('{') || trimmed.StartsWith('['))
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                return JsonElementToGraph(doc.RootElement);
            }
            catch (JsonException ex)
            {
                throw new CliException($"The JSON request body could not be parsed: {ex.Message}");
            }
        }

        try
        {
            return new YamlParser(body).Parse();
        }
        catch (FormatException ex)
        {
            throw new CliException(ex.Message);
        }
    }

    private static object? JsonElementToGraph(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => element.EnumerateObject()
            .ToDictionary(p => p.Name, p => JsonElementToGraph(p.Value)),
        JsonValueKind.Array => element.EnumerateArray().Select(JsonElementToGraph).ToList(),
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number => NumberToGraph(element),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        _ => element.GetRawText(),
    };

    private static object? WrapFields(object? node)
    {
        switch (node)
        {
            case Dictionary<string, object?> mapping:
            {
                var result = new Dictionary<string, object?>();
                foreach (var entry in mapping)
                {
                    if (IsWrapperGraph(entry.Value))
                        result[entry.Key] = entry.Value; // explicit {"value": ...} / {"value": ..., "error": ...}
                    else
                        result[entry.Key] = WrapFields(entry.Value);
                }
                return result;
            }
            case List<object?> list:
                return list.Select(WrapFields).ToList();
            case null:
                return new Dictionary<string, object?> { ["value"] = null };
            default:
                return new Dictionary<string, object?> { ["value"] = node };
        }
    }

    /// <summary>An object with only "value"/"error" keys and at least a "value" is an explicit wrapper.</summary>
    private static bool IsWrapperGraph(object? node)
    {
        if (node is not Dictionary<string, object?> mapping || mapping.Count == 0)
            return false;
        var hasValue = false;
        foreach (var key in mapping.Keys)
        {
            if (key != "value" && key != "error")
                return false;
            if (key == "value")
                hasValue = true;
        }
        return hasValue;
    }

    // -------------------------------------------------- YAML scalar formatting

    private static string FormatKey(string key) => NeedsQuoting(key) ? Quote(key) : key;

    private static string FormatScalar(object? value)
    {
        if (value is null or Dictionary<string, object?> { Count: 0 })
            return "null";
        if (value is bool b)
            return b ? "true" : "false";
        if (value is string s)
            return NeedsQuoting(s) ? Quote(s) : s;
        if (value is long or int or decimal)
            return Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
        return value.ToString() ?? "null";
    }

    private static bool NeedsQuoting(string value)
    {
        if (value.Length == 0)
            return true;
        if (value != value.Trim())
            return true;
        if (value.StartsWith('"') || value.EndsWith('"'))
            return true;
        if (value.Contains('\n') || value.Contains('\r') || value.Contains('\t'))
            return true;
        if (value.Contains(": ") || value.EndsWith(":") || value.Contains(" #"))
            return true;

        switch (value.ToLowerInvariant())
        {
            case "true" or "false" or "null" or "~" or "yes" or "no" or "on" or "off":
                return true;
        }

        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
            return true;
        if (decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out _))
            return true;

        if ("?&*!|>%@`{}[],#".Contains(value[0]) || value[0] == '-')
            return true; // values starting with an indicator character or dash
        if (value[0] == '-' && value.Length > 1)
            return true;

        return value.StartsWith("- ") || value.StartsWith("? ");
    }

    private static string Quote(string value)
    {
        var sb = new StringBuilder("\"");
        foreach (var c in value)
        {
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"': sb.Append("\\\""); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (char.IsControl(c))
                        sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else
                        sb.Append(c);
                    break;
            }
        }
        return sb.Append('"').ToString();
    }
}

/// <summary>
/// A minimal YAML block parser covering the subset needed for Acumatica request bodies:
/// nested mappings, block sequences of mappings, quoted and plain scalars, and comments.
/// Anything outside the subset fails with a clear error (use JSON input instead).
/// </summary>
internal sealed class YamlParser
{
    private readonly record struct Line(int Indent, string Content, int Number);

    private readonly List<Line> _lines;
    private int _index;

    public YamlParser(string text)
    {
        _lines = [];
        var number = 0;
        foreach (var rawLine in text.Split('\n'))
        {
            number++;
            var line = rawLine.TrimEnd('\r');
            if (line.Contains('\t'))
                throw new FormatException($"YAML error on line {number}: tabs are not supported; use spaces.");
            var content = StripComment(line);
            if (string.IsNullOrWhiteSpace(content))
                continue;
            var indent = 0;
            while (indent < content.Length && content[indent] == ' ')
                indent++;
            _lines.Add(new Line(indent, content[indent..], number));
        }
    }

    public object? Parse()
    {
        if (_lines.Count == 0)
            return null;
        var result = ParseBlock(_lines[0].Indent);
        if (_index < _lines.Count)
            throw Error(_lines[_index], "unexpected content (bad indentation?)");
        return result;
    }

    private object? ParseBlock(int indent)
    {
        return IsSequenceStart(_lines[_index]) ? ParseSequence(indent) : ParseMapping(indent);
    }

    private static bool IsSequenceStart(Line line) =>
        line.Content == "-" || line.Content.StartsWith("- ");

    private Dictionary<string, object?> ParseMapping(int indent)
    {
        var result = new Dictionary<string, object?>();
        while (_index < _lines.Count)
        {
            var line = _lines[_index];
            if (line.Indent < indent)
                break;
            if (line.Indent > indent)
                throw Error(line, "unexpected indentation");
            if (IsSequenceStart(line))
                throw Error(line, "unexpected list item in a mapping");

            var (key, remainder) = SplitKeyValue(line);
            if (remainder.Length == 0)
            {
                // "key:" — the value is a nested block, or null.
                _index++;
                var value = TryParseNested(indent);
                if (result.ContainsKey(key))
                    throw Error(line, $"duplicate key '{key}'");
                result[key] = value;
            }
            else
            {
                _index++;
                if (result.ContainsKey(key))
                    throw Error(line, $"duplicate key '{key}'");
                result[key] = ParseScalar(remainder, line);
            }
        }
        return result;
    }

    private List<object?> ParseSequence(int indent)
    {
        var result = new List<object?>();
        while (_index < _lines.Count)
        {
            var line = _lines[_index];
            if (line.Indent != indent || !IsSequenceStart(line))
                break;

            var content = line.Content == "-" ? "" : line.Content[2..];
            _index++;

            if (content.Length == 0)
            {
                // "- " alone: the item is a nested block.
                result.Add(TryParseNested(indent));
                continue;
            }

            if (!IsKeyValue(content))
            {
                result.Add(ParseScalar(content, line));
                continue;
            }

            // "- key: ..." starts a mapping item inline; its remaining keys are indented
            // to at least the dash column + 2.
            var itemIndent = indent + 2;
            var firstLine = new Line(itemIndent, content, line.Number);
            _lines.Insert(_index, firstLine);
            result.Add(ParseMapping(itemIndent));
        }
        return result;
    }

    private object? TryParseNested(int parentIndent)
    {
        if (_index >= _lines.Count)
            return null;
        var next = _lines[_index];
        if (next.Indent <= parentIndent)
            return IsSequenceStart(next) && next.Indent == parentIndent
                ? ParseSequence(parentIndent) // sequences may sit at the same indent as their key
                : null;
        return ParseBlock(next.Indent);
    }

    private static bool IsKeyValue(string content)
    {
        var colon = content.IndexOf(':');
        return colon >= 0
            && (colon + 1 == content.Length || content[colon + 1] == ' ');
    }

    private static (string Key, string Remainder) SplitKeyValue(Line line)
    {
        var content = line.Content;
        var colon = content.IndexOf(':');
        if (colon < 0 || (colon + 1 < content.Length && content[colon + 1] != ' '))
            throw Error(line, "expected 'key: value'");
        var key = UnquoteKey(content[..colon].Trim());
        var remainder = colon + 1 < content.Length ? content[(colon + 1)..].Trim() : "";
        return (key, remainder);
    }

    private static object? ParseScalar(string raw, Line line)
    {
        raw = raw.Trim();
        if (raw.Length == 0)
            return null;

        if (raw.StartsWith('"'))
        {
            if (raw.Length < 2 || !raw.EndsWith('"'))
                throw Error(line, "unterminated double-quoted scalar");
            return raw[1..^1]
                .Replace("\\\\", "\0")
                .Replace("\\\"", "\"")
                .Replace("\\n", "\n")
                .Replace("\\t", "\t")
                .Replace('\0', '\\');
        }
        if (raw.StartsWith('\''))
        {
            if (raw.Length < 2 || !raw.EndsWith('\''))
                throw Error(line, "unterminated single-quoted scalar");
            return raw[1..^1].Replace("''", "'");
        }

        return raw switch
        {
            "null" or "Null" or "NULL" or "~" => null,
            "true" or "True" => true,
            "false" or "False" => false,
            _ => ParseNumber(raw),
        };

        static object? ParseNumber(string value)
        {
            if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer))
                return integer;
            if (decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var number))
                return number;
            return value;
        }
    }

    private static string UnquoteKey(string key)
    {
        if (key.Length >= 2 && key.StartsWith('"') && key.EndsWith('"'))
            return key[1..^1];
        if (key.Length >= 2 && key.StartsWith('\'') && key.EndsWith('\''))
            return key[1..^1];
        return key;
    }

    private static string StripComment(string line)
    {
        var inSingle = false;
        var inDouble = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '\'' && !inDouble)
                inSingle = !inSingle;
            else if (c == '"' && !inSingle)
                inDouble = !inDouble;
            else if (c == '#' && !inSingle && !inDouble && (i == 0 || line[i - 1] == ' '))
                return line[..i];
        }
        return line;
    }

    private static FormatException Error(Line line, string message) =>
        new($"YAML error on line {line.Number}: {message}");
}
