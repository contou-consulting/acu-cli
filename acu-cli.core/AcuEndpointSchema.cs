using System.Text.Json;

namespace AcuCli.Core;

/// <summary>A field of an entity as described by the endpoint's swagger.json.</summary>
public sealed record AcuSchemaField(string Name, string Type, bool IsArray);

/// <summary>An action exposed by an entity (invoked via POST /{entity}/{action}).</summary>
public sealed record AcuSchemaAction(string Name);

/// <summary>An entity of the endpoint with its fields, actions and supported operations.</summary>
public sealed record AcuSchemaEntity(
    string Name,
    string? Screen,
    IReadOnlyList<string> Operations,
    IReadOnlyList<AcuSchemaField> Fields,
    IReadOnlyList<AcuSchemaAction> Actions);

/// <summary>
/// Parsed view of an endpoint's OpenAPI (swagger.json) document. Custom endpoints follow
/// the same architecture, so this parses any endpoint, not just the default one.
/// </summary>
public sealed class AcuEndpointSchema
{
    public required string Endpoint { get; init; }
    public required string Version { get; init; }
    public required IReadOnlyList<AcuSchemaEntity> Entities { get; init; }

    public AcuSchemaEntity? FindEntity(string name) =>
        Entities.FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));

    public static AcuEndpointSchema Parse(string swaggerJson, string endpoint, string version)
    {
        using var doc = JsonDocument.Parse(swaggerJson);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("paths", out var paths))
            throw new AcuApiException("The swagger document has no paths.");

        var schemas = root.TryGetProperty("components", out var components)
            && components.TryGetProperty("schemas", out var schemasValue)
                ? schemasValue
                : default;

        var screens = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (root.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Array)
        {
            foreach (var tag in tags.EnumerateArray())
            {
                if (tag.TryGetProperty("name", out var name) && tag.TryGetProperty("description", out var desc))
                {
                    var screen = desc.GetString();
                    if (!string.IsNullOrWhiteSpace(screen))
                        screens[name.GetString() ?? ""] = screen;
                }
            }
        }

        // Group paths by entity: first path segment after the leading '/'. Each entity is
        // built once from its full set of paths.
        var entities = new Dictionary<string, AcuSchemaEntity>(StringComparer.OrdinalIgnoreCase);
        foreach (var pathProperty in paths.EnumerateObject())
        {
            var path = pathProperty.Name;
            if (!path.StartsWith('/'))
                continue;
            var entityName = path.Trim('/').Split('/').FirstOrDefault(s => !string.IsNullOrWhiteSpace(s));
            if (entityName is null || entities.ContainsKey(entityName))
                continue;
            entities[entityName] = BuildEntity(entityName, paths, schemas, screens);
        }

        var ordered = entities.Values
            .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new AcuEndpointSchema { Endpoint = endpoint, Version = version, Entities = ordered };
    }

    private static AcuSchemaEntity BuildEntity(
        string entityName,
        JsonElement paths,
        JsonElement? schemas,
        Dictionary<string, string> screens)
    {
        var operations = new List<string>();
        var actions = new List<string>();

        if (paths.TryGetProperty($"/{entityName}", out var collectionOps))
        {
            if (collectionOps.TryGetProperty("get", out _)) operations.Add("get");
            if (collectionOps.TryGetProperty("put", out _)) operations.Add("put");
            if (collectionOps.TryGetProperty("patch", out _)) operations.Add("patch");
            if (collectionOps.TryGetProperty("post", out _)) operations.Add("post");
        }
        if (paths.TryGetProperty($"/{entityName}/{{ids}}", out var byKeysOps))
        {
            if (byKeysOps.TryGetProperty("get", out _)) operations.Add("get-by-keys");
            if (byKeysOps.TryGetProperty("delete", out _)) operations.Add("delete");
        }
        if (paths.TryGetProperty($"/{entityName}/{{ids}}/files/{{filename}}", out _))
            operations.Add("attach-file");

        // Actions are the concrete POST operations next to the {actionName} template path.
        var prefix = $"/{entityName}/";
        foreach (var pathProperty in paths.EnumerateObject())
        {
            var path = pathProperty.Name;
            if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                || !pathProperty.Value.TryGetProperty("post", out _))
            {
                continue;
            }
            var actionName = path[prefix.Length..];
            if (actionName.Length == 0
                || actionName.StartsWith('{')
                || actionName.Contains('/')
                || actionName.Contains('$'))
            {
                continue;
            }
            actions.Add(actionName);
        }

        var fields = new List<AcuSchemaField>();
        if (schemas is { } schemaMap && schemaMap.TryGetProperty(entityName, out var schema))
        {
            var properties = UnwrapSchema(schema);
            if (properties.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in properties.EnumerateObject())
                {
                    var (type, isArray) = DescribeType(property.Value);
                    fields.Add(new AcuSchemaField(property.Name, type, isArray));
                }
            }
        }

        screens.TryGetValue(entityName, out var screen);
        return new AcuSchemaEntity(
            entityName,
            screen,
            operations,
            fields.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToList(),
            actions.Distinct(StringComparer.OrdinalIgnoreCase)
                   .OrderBy(a => a, StringComparer.OrdinalIgnoreCase)
                   .Select(a => new AcuSchemaAction(a))
                   .ToList());
    }

    /// <summary>Descends allOf chains to the object that carries the properties.</summary>
    private static JsonElement UnwrapSchema(JsonElement schema)
    {
        if (schema.ValueKind == JsonValueKind.Object
            && schema.TryGetProperty("allOf", out var allOf)
            && allOf.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in allOf.EnumerateArray())
            {
                if (entry.ValueKind == JsonValueKind.Object
                    && entry.TryGetProperty("properties", out var properties))
                {
                    return properties;
                }
            }
        }
        if (schema.TryGetProperty("properties", out var direct))
            return direct;
        return default;
    }

    private static (string Type, bool IsArray) DescribeType(JsonElement property)
    {
        if (property.ValueKind != JsonValueKind.Object)
            return ("unknown", false);

        if (property.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String)
        {
            if (type.GetString() == "array")
            {
                if (property.TryGetProperty("items", out var items)
                    && items.TryGetProperty("$ref", out var itemsRef))
                {
                    return (RefName(itemsRef.GetString()), true);
                }
                return ("object", true);
            }
            var typeName = type.GetString() ?? "object";
            return (typeName, false);
        }

        if (property.TryGetProperty("$ref", out var reference))
            return (RefName(reference.GetString()), false);

        return ("object", false);
    }

    private static string RefName(string? reference) =>
        reference is null ? "object" : reference.Split('/').LastOrDefault() ?? "object";
}
