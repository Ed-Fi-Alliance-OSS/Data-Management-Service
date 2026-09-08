// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.ApiSchema.Helpers;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.External.Model;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EdFi.DataManagementService.Core.ApiSchema;

/// <summary>
/// Result of loading API schema, containing either the loaded nodes or failures
/// </summary>
public record ApiSchemaLoadResult(ApiSchemaDocumentNodes? Nodes, List<ApiSchemaFailure> Failures);

/// <summary>
/// Loads and parses ApiSchemas from files.
/// Schema loading occurs once at startup; runtime reload is not supported.
/// </summary>
internal class ApiSchemaProvider(
    ILogger<ApiSchemaProvider> _logger,
    IOptions<AppSettings> appSettings,
    IApiSchemaValidator _apiSchemaValidator
) : IApiSchemaProvider
{
    private const string BundledApiSchemaDirectoryName = "ApiSchema";

    // Cached API schema nodes loaded from files
    private ApiSchemaDocumentNodes? _apiSchemaNodes;

    // Unique identifier for the loaded schema (stable for process lifetime)
    private readonly Guid _schemaLoadId = Guid.NewGuid();

    // Lock object to ensure thread-safe access during schema loading
    private readonly object _loadLock = new();

    // Validation state
    private bool _isSchemaValid = true;
    private List<ApiSchemaFailure> _apiSchemaFailures = [];

    private (JsonNode? Node, ApiSchemaFailure? Failure) ReadApiSchemaFile(string filePath)
    {
        _logger.LogInformation("Loading ApiSchema.json file: {FilePath}", filePath);

        try
        {
            string fileContent = File.ReadAllText(filePath);

            try
            {
                JsonNode? parsedFileContent = JsonNode.Parse(fileContent);
                if (parsedFileContent is null)
                {
                    ApiSchemaFailure failure = new(
                        "ParseError",
                        $"Unable to parse ApiSchema file at '{filePath}' - parsed to null"
                    );
                    _logger.LogError(failure.Message);
                    return (null, failure);
                }

                return (parsedFileContent, null);
            }
            catch (Exception ex)
            {
                ApiSchemaFailure failure = new(
                    "ParseError",
                    $"Unable to parse ApiSchema file at '{filePath}'",
                    null,
                    ex
                );
                _logger.LogError(ex, failure.Message);
                return (null, failure);
            }
        }
        catch (IOException ex)
        {
            ApiSchemaFailure failure = new("FileSystem", $"Error reading file '{filePath}'", null, ex);
            _logger.LogError(ex, failure.Message);
            return (null, failure);
        }
        catch (UnauthorizedAccessException ex)
        {
            ApiSchemaFailure failure = new("AccessDenied", $"Access denied to file '{filePath}'", null, ex);
            _logger.LogError(ex, failure.Message);
            return (null, failure);
        }
    }

    /// <summary>
    /// Loads core and extension ApiSchema JsonNodes from the configured source
    /// </summary>
    private ApiSchemaLoadResult LoadSchemaFromSource()
    {
        _logger.LogInformation("Loading API schemas from configured source...");

        if (appSettings.Value.UseApiSchemaPath)
        {
            return LoadSchemaFromFileSystem();
        }

        return LoadSchemaFromBundledFiles();
    }

    /// <summary>
    /// Loads schemas from the file system
    /// </summary>
    private ApiSchemaLoadResult LoadSchemaFromFileSystem()
    {
        if (appSettings.Value.ApiSchemaPath is null)
        {
            ApiSchemaFailure failure = new("Configuration", "No ApiSchemaPath configuration is set");
            _logger.LogError(failure.Message);
            return new(null, [failure]);
        }

        return LoadSchemaFromDirectory(appSettings.Value.ApiSchemaPath);
    }

    /// <summary>
    /// Loads schemas bundled with the application.
    /// </summary>
    private ApiSchemaLoadResult LoadSchemaFromBundledFiles()
    {
        var bundledApiSchemaPath = Path.Combine(
            Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory),
            BundledApiSchemaDirectoryName
        );

        return LoadSchemaFromDirectory(bundledApiSchemaPath);
    }

    private ApiSchemaLoadResult LoadSchemaFromDirectory(string apiSchemaPath)
    {
        if (!Directory.Exists(apiSchemaPath))
        {
            ApiSchemaFailure failure = new("FileSystem", $"The directory {apiSchemaPath} does not exist");
            _logger.LogError(failure.Message);
            return new(null, [failure]);
        }

        var manifestPath = Path.Combine(apiSchemaPath, ApiSchemaAssetManifestReader.ManifestFileName);
        if (File.Exists(manifestPath))
        {
            return LoadSchemaFromManifest(Path.GetFullPath(apiSchemaPath), manifestPath);
        }

        ApiSchemaFailure missingManifestFailure = new(
            "Configuration",
            $"Required bootstrap manifest file '{ApiSchemaAssetManifestReader.ManifestFileName}' was not found in ApiSchema workspace '{apiSchemaPath}'."
        );
        _logger.LogError(missingManifestFailure.Message);
        return new(null, [missingManifestFailure]);
    }

    private ApiSchemaLoadResult LoadSchemaFromManifest(string workspaceRoot, string manifestPath)
    {
        ApiSchemaAssetManifest manifest;
        try
        {
            manifest = ApiSchemaAssetManifestReader.ReadFromFile(workspaceRoot, manifestPath);
        }
        catch (ApiSchemaAssetManifestException ex)
        {
            ApiSchemaFailure failure = new(ex.FailureType, ex.Message, null, ex);
            _logger.LogError(ex, failure.Message);
            return new(null, [failure]);
        }

        List<JsonNode> apiSchemaNodes = [];
        var pathResolver = new ApiSchemaWorkspacePathResolver(workspaceRoot);
        List<ApiSchemaFailure> failures = [];
        foreach ((ApiSchemaProject project, int index) in manifest.Projects.Select((p, i) => (p, i)))
        {
            string resolvedSchemaPath;
            try
            {
                resolvedSchemaPath = pathResolver.ResolveManifestRelativePath(project.SchemaPath);
            }
            catch (InvalidOperationException ex)
            {
                failures.Add(
                    new ApiSchemaFailure(
                        "Configuration",
                        $"Bootstrap manifest project {DescribeManifestProject(project, index)} has invalid schemaPath "
                            + $"'{project.SchemaPath}': {ex.Message}",
                        null,
                        ex
                    )
                );
                continue;
            }

            if (!File.Exists(resolvedSchemaPath))
            {
                failures.Add(
                    new ApiSchemaFailure(
                        "FileSystem",
                        $"Bootstrap manifest project {DescribeManifestProject(project, index)} declares schemaPath "
                            + $"'{project.SchemaPath}', "
                            + $"but the resolved schema file '{resolvedSchemaPath}' does not exist."
                    )
                );
                continue;
            }

            var (node, readFailure) = ReadApiSchemaFile(resolvedSchemaPath);
            if (readFailure is not null)
            {
                failures.Add(readFailure);
            }
            else if (node is not null)
            {
                var identityFailure = ValidateSchemaProjectIdentity(project, index, node);
                if (identityFailure is not null)
                {
                    failures.Add(identityFailure);
                    continue;
                }

                apiSchemaNodes.Add(node);
            }
        }

        if (failures.Count > 0)
        {
            return new(null, failures);
        }

        return CreateApiSchemaLoadResult(
            apiSchemaNodes,
            $"Bootstrap manifest file '{ApiSchemaAssetManifestReader.ManifestFileName}' did not select any API schema files."
        );
    }

    private ApiSchemaFailure? ValidateSchemaProjectIdentity(
        ApiSchemaProject project,
        int index,
        JsonNode node
    )
    {
        return CompareSchemaProjectField(project, index, "projectName", project.ProjectName, ReadSchemaString)
            ?? CompareSchemaProjectField(
                project,
                index,
                "projectEndpointName",
                project.ProjectEndpointName,
                ReadSchemaString
            )
            ?? CompareSchemaProjectField(
                project,
                index,
                "isExtensionProject",
                FormatBoolean(project.IsExtensionProject),
                ReadSchemaBoolean
            );

        string? ReadSchemaString(string fieldName)
        {
            return TryReadProjectSchemaValue<string>(node, fieldName, out var value) ? value : null;
        }

        string? ReadSchemaBoolean(string fieldName)
        {
            return TryReadProjectSchemaValue<bool>(node, fieldName, out var value)
                ? FormatBoolean(value)
                : null;
        }
    }

    private static string FormatBoolean(bool value)
    {
        return value ? "true" : "false";
    }

    private ApiSchemaFailure? CompareSchemaProjectField(
        ApiSchemaProject project,
        int index,
        string fieldName,
        string declaredValue,
        Func<string, string?> readSchemaValue
    )
    {
        var schemaValue = readSchemaValue(fieldName);
        if (schemaValue is not null && declaredValue.Equals(schemaValue, StringComparison.Ordinal))
        {
            return null;
        }

        var schemaValueDescription = schemaValue ?? "<missing or invalid>";
        var failure = new ApiSchemaFailure(
            "Configuration",
            $"Bootstrap manifest project {DescribeManifestProject(project, index)} declares {fieldName} "
                + $"'{declaredValue}', but ApiSchema JSON projectSchema.{fieldName} is "
                + $"'{schemaValueDescription}' for schemaPath '{project.SchemaPath}'."
        );
        _logger.LogError(failure.Message);
        return failure;
    }

    private static bool TryReadProjectSchemaValue<T>(JsonNode node, string fieldName, out T value)
    {
        value = default!;

        if (node["projectSchema"] is not JsonObject projectSchema)
        {
            return false;
        }

        var fieldNode = projectSchema[fieldName];
        if (fieldNode is null)
        {
            return false;
        }

        try
        {
            value = fieldNode.GetValue<T>();
            return value is not null;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static string DescribeManifestProject(ApiSchemaProject project, int index)
    {
        return ApiSchemaAssetManifestReader.DescribeProject(project, index);
    }

    private ApiSchemaLoadResult CreateApiSchemaLoadResult(
        IReadOnlyList<JsonNode>? apiSchemaNodes,
        string noSchemaFilesMessage
    )
    {
        if (apiSchemaNodes is null || apiSchemaNodes.Count == 0)
        {
            ApiSchemaFailure failure = new("FileSystem", noSchemaFilesMessage);
            _logger.LogError(failure.Message);
            return new(null, [failure]);
        }

        try
        {
            var classifiedApiSchemaNodes = apiSchemaNodes
                .Select(node => new
                {
                    Node = node,
                    IsExtensionProject = node.SelectRequiredNodeFromPathAs<bool>(
                        "$.projectSchema.isExtensionProject",
                        _logger
                    ),
                })
                .ToList();

            List<JsonNode> coreApiSchemaNodes = classifiedApiSchemaNodes
                .Where(node => !node.IsExtensionProject)
                .Select(node => node.Node)
                .ToList();

            if (coreApiSchemaNodes.Count != 1)
            {
                ApiSchemaFailure failure = new(
                    "Configuration",
                    $"Expected exactly one core API schema where projectSchema.isExtensionProject is false; "
                        + $"found {coreApiSchemaNodes.Count}."
                );
                _logger.LogError(failure.Message);
                return new(null, [failure]);
            }

            JsonNode[] extensionApiSchemaNodes = classifiedApiSchemaNodes
                .Where(node => node.IsExtensionProject)
                .Select(node => node.Node)
                .ToArray();

            return new ApiSchemaLoadResult(
                new ApiSchemaDocumentNodes(coreApiSchemaNodes[0], extensionApiSchemaNodes),
                []
            );
        }
        catch (Exception ex)
        {
            ApiSchemaFailure failure = new("ParseError", "Failed to process API schema files", null, ex);
            _logger.LogError(ex, failure.Message);
            return new(null, [failure]);
        }
    }

    /// <summary>
    /// Validates all schemas (core and extensions) and returns validation failures
    /// </summary>
    private List<ApiSchemaFailure> ValidateAllSchemas(ApiSchemaDocumentNodes schemaNodes)
    {
        List<ApiSchemaFailure> failures = [];

        // Validate core schema
        List<SchemaValidationFailure> coreValidationErrors = _apiSchemaValidator.Validate(
            schemaNodes.CoreApiSchemaRootNode
        );

        if (coreValidationErrors.Count > 0)
        {
            _logger.LogError("Core Api schema validation failed.");

            foreach (var error in coreValidationErrors)
            {
                _logger.LogError(
                    "[Core Schema] {FailurePath} - {FailureMessages}",
                    error.FailurePath.Value,
                    string.Join(", ", error.FailureMessages)
                );

                failures.Add(
                    new ApiSchemaFailure(
                        "Validation",
                        $"[Core Schema] {string.Join(", ", error.FailureMessages)}",
                        error.FailurePath
                    )
                );
            }
        }

        // Validate extension schemas

        for (int i = 0; i < schemaNodes.ExtensionApiSchemaRootNodes.Length; i++)
        {
            List<SchemaValidationFailure> extensionValidationErrors = _apiSchemaValidator.Validate(
                schemaNodes.ExtensionApiSchemaRootNodes[i]
            );

            if (extensionValidationErrors.Count > 0)
            {
                _logger.LogError("Extension Api schema {Index} validation failed.", i);

                foreach (var error in extensionValidationErrors)
                {
                    _logger.LogError(
                        "[Extension Schema {Index}] {FailurePath} - {FailureMessages}",
                        i,
                        error.FailurePath.Value,
                        string.Join(", ", error.FailureMessages)
                    );

                    failures.Add(
                        new ApiSchemaFailure(
                            "Validation",
                            $"[Extension Schema {i}] {string.Join(", ", error.FailureMessages)}",
                            error.FailurePath
                        )
                    );
                }
            }
        }

        return failures;
    }

    /// <summary>
    /// Returns core and extension ApiSchema JsonNodes
    /// </summary>
    public ApiSchemaDocumentNodes GetApiSchemaNodes()
    {
        lock (_loadLock)
        {
            if (_apiSchemaNodes is null)
            {
                // Initial load
                var loadResult = LoadSchemaFromSource();
                var schemaNodes = loadResult.Nodes;
                var loadFailures = loadResult.Failures;

                if (loadFailures.Count > 0)
                {
                    _apiSchemaFailures = loadFailures;
                    _isSchemaValid = false;

                    throw new InvalidOperationException(
                        "API schema loading failed. Check ApiSchemaFailures for details."
                    );
                }

                if (schemaNodes is null)
                {
                    ApiSchemaFailure failure = new(
                        "Configuration",
                        "Schema loading returned null without failures"
                    );
                    _apiSchemaFailures = [failure];
                    _isSchemaValid = false;
                    throw new InvalidOperationException(failure.Message);
                }

                // Validate schemas
                _apiSchemaFailures = ValidateAllSchemas(schemaNodes);
                _isSchemaValid = _apiSchemaFailures.Count == 0;

                if (!_isSchemaValid)
                {
                    throw new InvalidOperationException(
                        "API schema validation failed. Cannot proceed with invalid schema."
                    );
                }

                _apiSchemaNodes = schemaNodes;
                _logger.LogInformation("Initial API schema load completed successfully.");
            }
            return _apiSchemaNodes;
        }
    }

    /// <summary>
    /// Gets the unique identifier for the loaded schema
    /// </summary>
    public Guid SchemaLoadId => _schemaLoadId;

    /// <summary>
    /// Gets whether the currently loaded API schema is valid
    /// </summary>
    public bool IsSchemaValid
    {
        get
        {
            lock (_loadLock)
            {
                return _isSchemaValid;
            }
        }
    }

    /// <summary>
    /// Gets the failures from the last schema operation
    /// </summary>
    public List<ApiSchemaFailure> ApiSchemaFailures
    {
        get
        {
            lock (_loadLock)
            {
                return _apiSchemaFailures;
            }
        }
    }
}
