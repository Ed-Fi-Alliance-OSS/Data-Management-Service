using Microsoft.Extensions.Logging;
using System.Text.Json.Nodes;

namespace DmsOpenApiGenerator.Services;

public class OpenApiGenerator(ILogger<OpenApiGenerator> logger)
{
    private readonly ILogger<OpenApiGenerator> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    public void Generate(string coreSchemaPath, string? extensionSchemaPath, string outputPath)
    {
        _logger.LogInformation("Starting OpenAPI generation...");

        if (string.IsNullOrWhiteSpace(coreSchemaPath) || string.IsNullOrWhiteSpace(extensionSchemaPath) || string.IsNullOrWhiteSpace(outputPath))
        {
            _logger.LogError("Invalid input paths. Ensure all paths are provided.");
            throw new ArgumentException("Core schema, extension schema, and output paths are required.");
        }

        _logger.LogDebug("Loading core schema from: {CoreSchemaPath}", coreSchemaPath);
        var coreSchema = JsonNode.Parse(File.ReadAllText(coreSchemaPath))
                         ?? throw new InvalidOperationException("Invalid core schema file.");


        _logger.LogDebug("Loading extension schema from: {ExtensionSchemaPath}", extensionSchemaPath);
        JsonNode?[] extensionSchema = JsonNode.Parse(File.ReadAllText(extensionSchemaPath)) is JsonArray jsonArray
            ? jsonArray.ToArray()
            : throw new InvalidOperationException("Invalid extension schema file.");

        _logger.LogDebug("Combining core and extension schemas.");
        var combinedSchema = CombineSchemas(coreSchema, extensionSchema);

        _logger.LogDebug("Writing combined schema to: {OutputPath}", outputPath);
        File.WriteAllText(outputPath, combinedSchema.ToJsonString());

        _logger.LogInformation("OpenAPI generation completed successfully.");
    }

    private JsonNode CombineSchemas(JsonNode coreSchema, JsonNode[] extensionSchema)
    {
        ApiSchemaDocument coreApiSchemaDocument = new(coreSchema, _logger);

        // Get the core OpenAPI spec as a copy since we are going to modify it
        JsonNode openApiSpecification =
            coreApiSchemaDocument.FindCoreOpenApiSpecification()?.DeepClone()
            ?? throw new InvalidOperationException("Expected CoreOpenApiSpecification node to exist.");

        // Get each extension OpenAPI fragment to insert into core OpenAPI spec
        foreach (JsonNode extensionApiSchemaRootNode in extensionSchema)
        {
            ApiSchemaDocument extensionApiSchemaDocument = new(extensionApiSchemaRootNode, _logger);
            JsonNode extensionFragments =
                extensionApiSchemaDocument.FindOpenApiExtensionFragments()
                ?? throw new InvalidOperationException("Expected OpenApiExtensionFragments node to exist.");

            InsertExts(
                extensionFragments.SelectRequiredNodeFromPath("$.exts", _logger).AsObject(),
                openApiSpecification
            );

            InsertNewPaths(
                extensionFragments.SelectRequiredNodeFromPath("$.newPaths", _logger).AsObject(),
                openApiSpecification
            );

            InsertNewSchemas(
                extensionFragments.SelectRequiredNodeFromPath("$.newSchemas", _logger).AsObject(),
                openApiSpecification
            );
        }

        return openApiSpecification;
    }

    private void InsertExts(JsonObject extList, JsonNode openApiSpecification)
    {
        foreach ((string componentSchemaName, JsonNode? extObject) in extList)
        {
            if (extObject == null)
            {
                throw new InvalidOperationException(
                    $"OpenAPI extension fragment has empty exts schema name '{componentSchemaName}'. Extension fragment validation failed?"
                );
            }

            // Get the component.schema location for _ext insert
            JsonObject locationForExt =
                openApiSpecification
                    .SelectNodeFromPath($"$.components.schemas.{componentSchemaName}.properties", _logger)
                    ?.AsObject()
                ?? throw new InvalidOperationException(
                    $"OpenAPI extension fragment expects Core to have '$.components.schemas.EdFi_{componentSchemaName}.properties'. Extension fragment validation failed?"
                );

            // If _ext has already been added by another extension, we don't support a second one
            if (locationForExt["_ext"] != null)
            {
                throw new InvalidOperationException(
                    $"OpenAPI extension fragment tried to add a second _ext to '$.components.schemas.EdFi_{componentSchemaName}.properties', which is not supported. Extension fragment validation failed?"
                );
            }

            locationForExt.Add("_ext", extObject.DeepClone());
        }
    }

    private void InsertNewPaths(JsonObject newPaths, JsonNode openApiSpecification)
    {
        foreach ((string pathName, JsonNode? pathObject) in newPaths)
        {
            if (pathObject == null)
            {
                throw new InvalidOperationException(
                    $"OpenAPI extension fragment has empty newPaths path name '{pathName}'. Extension fragment validation failed?"
                );
            }

            JsonObject locationForPaths = openApiSpecification
                .SelectRequiredNodeFromPath("$.paths", _logger)
                .AsObject();

            // If pathName has already been added by another extension, we don't support a second one
            if (locationForPaths[pathName] != null)
            {
                throw new InvalidOperationException(
                    $"OpenAPI extension fragment tried to add a second path '$.paths.{pathName}', which is not supported. Extension fragment validation failed?"
                );
            }

            locationForPaths.Add(pathName, pathObject.DeepClone());
        }
    }

    private void InsertNewSchemas(JsonObject newSchemas, JsonNode openApiSpecification)
    {
        foreach ((string schemaName, JsonNode? schemaObject) in newSchemas)
        {
            if (schemaObject == null)
            {
                throw new InvalidOperationException(
                    $"OpenAPI extension fragment has empty newSchemas path name '{schemaName}'. Extension fragment validation failed?"
                );
            }

            JsonObject locationForSchemas = openApiSpecification
                .SelectRequiredNodeFromPath("$.components.schemas", _logger)
                .AsObject();

            // If schemaName has already been added by another extension, we don't support a second one
            if (locationForSchemas[schemaName] != null)
            {
                throw new InvalidOperationException(
                    $"OpenAPI extension fragment tried to add a second schema '$.components.schemas.{schemaName}', which is not supported. Extension fragment validation failed?"
                );
            }

            locationForSchemas.Add(schemaName, schemaObject.DeepClone());
        }
    }
}

internal record struct ProjectNamespace(string Value);
