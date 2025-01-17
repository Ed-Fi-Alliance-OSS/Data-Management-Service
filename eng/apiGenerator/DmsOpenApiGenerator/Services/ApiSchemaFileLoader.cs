using System.Text.Json.Nodes;

namespace DmsOpenApiGenerator.Services;

public class ApiSchemaFileLoader
{
    public JsonNode LoadCoreSchema(string coreSchemaPath)
    {
        if (!File.Exists(coreSchemaPath))
            throw new FileNotFoundException($"Core schema file not found: {coreSchemaPath}");

        var content = File.ReadAllText(coreSchemaPath);
        return JsonNode.Parse(content) ?? throw new InvalidOperationException("Failed to parse core schema file.");
    }

    public JsonNode[] LoadExtensionSchemas(string extensionSchemaPath)
    {
        if (string.IsNullOrWhiteSpace(extensionSchemaPath) || !File.Exists(extensionSchemaPath))
            return Array.Empty<JsonNode>();

        var content = File.ReadAllText(extensionSchemaPath);
        var parsedNode = JsonNode.Parse(content);

        return parsedNode != null ? new[] { parsedNode } : Array.Empty<JsonNode>();
    }
}
