using System.Text.Json.Nodes;

namespace DmsOpenApiGenerator.Tests;

public class ApiSchemaBuilder
{
    private readonly JsonNode _apiSchemaRootNode;

    public JsonNode RootNode => _apiSchemaRootNode;

    private JsonNode? _currentProjectNode = null;

    public ApiSchemaBuilder()
    {
        _apiSchemaRootNode = new JsonObject
        {
            ["projectNameMapping"] = new JsonObject(),
            ["projectSchemas"] = new JsonObject(),
        };
    }

    public ApiSchemaBuilder WithStartProject(string projectName = "ed-fi", string projectVersion = "5.0.0")
    {
        if (_currentProjectNode != null)
        {
            throw new InvalidOperationException();
        }

        _currentProjectNode = new JsonObject
        {
            ["abstractResources"] = new JsonObject(),
            ["caseInsensitiveEndpointNameMapping"] = new JsonObject(),
            ["description"] = $"{projectName} description",
            ["isExtensionProject"] = projectName.ToLower() != "ed-fi",
            ["projectName"] = projectName,
            ["projectVersion"] = projectVersion,
            ["resourceNameMapping"] = new JsonObject(),
            ["resourceSchemas"] = new JsonObject(),
        };

        _apiSchemaRootNode["projectNameMapping"]![projectName] = projectName.ToLower();
        _apiSchemaRootNode["projectSchemas"]![projectName.ToLower()] = _currentProjectNode;
        return this;
    }

    public ApiSchemaBuilder WithCoreOpenApiSpecification(JsonNode schemas, JsonNode paths)
    {
        if (_currentProjectNode == null)
        {
            throw new InvalidOperationException();
        }

        _currentProjectNode["coreOpenApiSpecification"] = new JsonObject
        {
            ["components"] = new JsonObject { ["schemas"] = schemas },
            ["paths"] = paths,
        };
        return this;
    }

    public ApiSchemaBuilder WithEndProject()
    {
        if (_currentProjectNode == null)
        {
            throw new InvalidOperationException();
        }

        _currentProjectNode = null;
        return this;
    }

    internal JsonNode AsRootJsonNode()
    {
        return RootNode.DeepClone();
    }

    public ApiSchemaBuilder WithOpenApiExtensionFragments(
        JsonNode exts,
        JsonNode newPaths,
        JsonNode newSchemas
    )
    {
        if (_currentProjectNode == null)
        {
            throw new InvalidOperationException();
        }

        _currentProjectNode["openApiExtensionFragments"] = new JsonObject
        {
            ["exts"] = exts,
            ["newPaths"] = newPaths,
            ["newSchemas"] = newSchemas,
        };
        return this;
    }
}
