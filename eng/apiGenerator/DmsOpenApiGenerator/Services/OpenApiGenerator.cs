using System.Text.Json.Nodes;

namespace DmsOpenApiGenerator.Services;

public class OpenApiGenerator
{
    public void Generate(string coreSchemaPath, string? extensionSchemaPath, string outputPath)
    {
        var loader = new ApiSchemaFileLoader();

        // Load schemas
        loader.LoadCoreSchema(coreSchemaPath);
        if (extensionSchemaPath != null)
        {
            loader.LoadExtensionSchemas(extensionSchemaPath);

            // Simulate CreateDocument method
            var openApiDocument = CreateOpenApiDocument();

            // Save the OpenAPI document to the specified output path
            File.WriteAllText(outputPath, openApiDocument.ToJsonString());
        }
    }

    private JsonNode CreateOpenApiDocument()
    {
        // Integrate logic to merge core and extension schemas
        var openApiDocument = new JsonObject
        {
            ["openapi"] = "3.0.0",
            ["info"] = new JsonObject
            {
                ["title"] = "Generated API",
                ["version"] = "1.0.0"
            },
            ["paths"] = new JsonObject()
        };

        return openApiDocument;
    }
}
