using Microsoft.Extensions.Logging;
using System.Text.Json.Nodes;

namespace DmsOpenApiGenerator.Services;

internal class ApiSchemaDocument(JsonNode _apiSchemaRootNode, ILogger _logger)
{
    /// <summary>
    /// Returns the mapped projectNamespace from the given project name
    /// </summary>
    public ProjectNamespace GetMappedProjectName(string projectName)
    {
        return new ProjectNamespace(
            _apiSchemaRootNode.SelectNodeFromPathAs<string>(
                $"$.projectNameMapping[\"{projectName}\"]",
                _logger
            ) ?? string.Empty
        );
    }

    public JsonNode? FindCoreOpenApiSpecification()
    {
        bool isExtensionProject = _apiSchemaRootNode.SelectRequiredNodeFromPathAs<bool>(
            "$.projectSchemas['ed-fi'].isExtensionProject",
            _logger
        );

        if (isExtensionProject)
        {
            return null;
        }

        return _apiSchemaRootNode.SelectRequiredNodeFromPath(
            "$.projectSchemas['ed-fi'].coreOpenApiSpecification",
            _logger
        );
    }

    public JsonNode? FindOpenApiExtensionFragments()
    {
        // DMS-497 will fix: TPDM is hardcoded until we remove projectSchemas from ApiSchema.json - making one project per file
        bool isExtensionProject = _apiSchemaRootNode.SelectRequiredNodeFromPathAs<bool>(
            "$.projectSchemas['tpdm'].isExtensionProject",
            _logger
        );

        if (!isExtensionProject)
        {
            return null;
        }

        // DMS-497 will fix: TPDM is hardcoded
        return _apiSchemaRootNode.SelectRequiredNodeFromPath(
            "$.projectSchemas['tpdm'].openApiExtensionFragments",
            _logger
        );
    }
}
