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

    /// <summary>
    /// Finds and reads all ApiSchema*.json files in the given directory path.
    /// Returns the parsed files as JsonNodes
    /// </summary>
    public (List<JsonNode>? Nodes, List<ApiSchemaFailure> Failures) ReadApiSchemaFiles(string directoryPath)
    {
        List<JsonNode> fileContents = [];
        List<ApiSchemaFailure> failures = [];

        try
        {
            IEnumerable<string> matchingFilePaths = Directory
                .EnumerateFiles(directoryPath, "ApiSchema*.json", SearchOption.AllDirectories)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase);

            foreach (string filePath in matchingFilePaths)
            {
                _logger.LogInformation("Loading ApiSchema.json file: {FilePath}", filePath);
                try
                {
                    // Read all text from the file into a string.
                    string fileContent = File.ReadAllText(filePath);

                    try
                    {
                        JsonNode? parsedFileContent = JsonNode.Parse(fileContent);
                        if (parsedFileContent == null)
                        {
                            ApiSchemaFailure failure = new(
                                "ParseError",
                                $"Unable to parse ApiSchema file at '{filePath}' - parsed to null"
                            );
                            failures.Add(failure);
                            _logger.LogError(failure.Message);
                        }
                        else
                        {
                            fileContents.Add(parsedFileContent);
                        }
                    }
                    catch (Exception ex)
                    {
                        ApiSchemaFailure failure = new(
                            "ParseError",
                            $"Unable to parse ApiSchema file at '{filePath}'",
                            null,
                            ex
                        );
                        failures.Add(failure);
                        _logger.LogError(ex, failure.Message);
                    }
                }
                catch (IOException ex)
                {
                    ApiSchemaFailure failure = new(
                        "FileSystem",
                        $"Error reading file '{filePath}'",
                        null,
                        ex
                    );
                    failures.Add(failure);
                    _logger.LogError(ex, failure.Message);
                }
                catch (UnauthorizedAccessException ex)
                {
                    ApiSchemaFailure failure = new(
                        "AccessDenied",
                        $"Access denied to file '{filePath}'",
                        null,
                        ex
                    );
                    failures.Add(failure);
                    _logger.LogError(ex, failure.Message);
                }
            }
        }
        catch (DirectoryNotFoundException ex)
        {
            ApiSchemaFailure failure = new("FileSystem", $"Directory not found: '{directoryPath}'", null, ex);
            failures.Add(failure);
            _logger.LogError(ex, failure.Message);
            return (null, failures);
        }
        catch (UnauthorizedAccessException ex)
        {
            ApiSchemaFailure failure = new(
                "AccessDenied",
                $"Access denied to directory '{directoryPath}'",
                null,
                ex
            );
            failures.Add(failure);
            _logger.LogError(ex, failure.Message);
            return (null, failures);
        }

        if (failures.Count > 0)
        {
            return (null, failures);
        }

        return (fileContents, []);
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
        if (appSettings.Value.ApiSchemaPath == null)
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

        var (apiSchemaNodes, readFailures) = ReadApiSchemaFiles(apiSchemaPath);
        if (readFailures.Count > 0)
        {
            return new(null, readFailures);
        }

        if (apiSchemaNodes == null || apiSchemaNodes.Count == 0)
        {
            ApiSchemaFailure failure = new(
                "FileSystem",
                $"No API schema files found in directory {apiSchemaPath}"
            );
            _logger.LogError(failure.Message);
            return new(null, [failure]);
        }

        try
        {
            JsonNode? coreApiSchemaNode = apiSchemaNodes.Find(node =>
                !node.SelectRequiredNodeFromPathAs<bool>("$.projectSchema.isExtensionProject", _logger)
            );

            if (coreApiSchemaNode == null)
            {
                ApiSchemaFailure failure = new(
                    "Configuration",
                    "No core API schema found (all schemas are marked as extensions)"
                );
                _logger.LogError(failure.Message);
                return new(null, [failure]);
            }

            JsonNode[] extensionApiSchemaNodes = apiSchemaNodes
                .Where(node =>
                    node.SelectRequiredNodeFromPathAs<bool>("$.projectSchema.isExtensionProject", _logger)
                )
                .ToArray();

            return new ApiSchemaLoadResult(
                new ApiSchemaDocumentNodes(coreApiSchemaNode, extensionApiSchemaNodes),
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
            if (_apiSchemaNodes == null)
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

                if (schemaNodes == null)
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
