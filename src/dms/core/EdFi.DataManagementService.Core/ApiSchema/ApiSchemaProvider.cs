// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Reflection;
using System.Runtime.Loader;
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
/// Status of API schema operations with success indicator and any failures
/// </summary>
public record ApiSchemaLoadStatus(bool Success, List<ApiSchemaFailure> Failures);

/// <summary>
/// Loads and parses ApiSchemas from files and uploads.
/// </summary>
internal class ApiSchemaProvider(
    ILogger<ApiSchemaProvider> _logger,
    IOptions<AppSettings> appSettings,
    IApiSchemaValidator _apiSchemaValidator
) : IApiSchemaProvider
{
    // Cached API schema nodes loaded from files or assemblies
    private ApiSchemaDocumentNodes? _apiSchemaNodes;

    // Unique identifier for the current reload instance
    private Guid _reloadId = Guid.NewGuid();

    // Lock object to ensure thread-safe access during schema reloads
    private readonly object _reloadLock = new();

    // Validation state
    private bool _isSchemaValid = true;
    private List<ApiSchemaFailure> _apiSchemaFailures = [];

    /// <summary>
    /// Loads the resource with the given resourceName from the assembly as a JsonNode
    /// </summary>
    private static JsonNode LoadFromAssembly(string resourceName, Assembly assembly)
    {
        using Stream stream =
            assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                $"Could not load assembly-bundled ApiSchema file '{resourceName}'"
            );

        using StreamReader reader = new(stream);

        string? jsonContent = reader.ReadToEnd();

        return JsonNode.Parse(jsonContent)
            ?? throw new InvalidOperationException(
                $"Unable to parse assembly-bundled ApiSchema file '{resourceName}'"
            );
    }

    /// <summary>
    /// Finds and reads all *.ApiSchema.json files in the given directory path.
    /// Returns the parsed files as JsonNodes
    /// </summary>
    public (List<JsonNode>? Nodes, List<ApiSchemaFailure> Failures) ReadApiSchemaFiles(string directoryPath)
    {
        List<JsonNode> fileContents = [];
        List<ApiSchemaFailure> failures = [];

        try
        {
            IEnumerable<string> matchingFilePaths = Directory.EnumerateFiles(
                directoryPath,
                "ApiSchema*.json",
                SearchOption.AllDirectories
            );

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
        else
        {
            return LoadSchemaFromAssemblies();
        }
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
            return new ApiSchemaLoadResult(null, new List<ApiSchemaFailure> { failure });
        }

        string apiSchemaPath = appSettings.Value.ApiSchemaPath;

        if (!Directory.Exists(apiSchemaPath))
        {
            ApiSchemaFailure failure = new("FileSystem", $"The directory {apiSchemaPath} does not exist");
            _logger.LogError(failure.Message);
            return new ApiSchemaLoadResult(null, new List<ApiSchemaFailure> { failure });
        }

        var (apiSchemaNodes, readFailures) = ReadApiSchemaFiles(apiSchemaPath);
        if (readFailures.Count > 0)
        {
            return new ApiSchemaLoadResult(null, readFailures);
        }

        if (apiSchemaNodes == null || apiSchemaNodes.Count == 0)
        {
            ApiSchemaFailure failure = new(
                "FileSystem",
                $"No API schema files found in directory {apiSchemaPath}"
            );
            _logger.LogError(failure.Message);
            return new ApiSchemaLoadResult(null, new List<ApiSchemaFailure> { failure });
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
                return new ApiSchemaLoadResult(null, new List<ApiSchemaFailure> { failure });
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
            return new ApiSchemaLoadResult(null, new List<ApiSchemaFailure> { failure });
        }
    }

    /// <summary>
    /// Loads schemas from assemblies
    /// </summary>
    private ApiSchemaLoadResult LoadSchemaFromAssemblies()
    {
        List<ApiSchemaFailure> failures = [];
        JsonNode? coreApiSchemaNode = null;
        List<JsonNode> extensionApiSchemaNodes = [];

        try
        {
            var projectDirectory = Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory);
            string apiSchemaPath = Path.GetFullPath(projectDirectory);
            var assemblies = Directory.GetFiles(
                apiSchemaPath,
                "*.ApiSchema.dll",
                SearchOption.AllDirectories
            );

            if (assemblies.Length == 0)
            {
                ApiSchemaFailure failure = new(
                    "Configuration",
                    $"No API schema assemblies found in {apiSchemaPath}"
                );
                failures.Add(failure);
                _logger.LogError(failure.Message);
                return new ApiSchemaLoadResult(null, failures);
            }

            ApiSchemaAssemblyLoadContext apiSchemaAssemblyLoadContext = new();
            foreach (var assemblyPath in assemblies)
            {
                try
                {
                    Assembly assembly = apiSchemaAssemblyLoadContext.LoadFromAssemblyPath(assemblyPath);
                    string[] manifestResourceNames = assembly.GetManifestResourceNames();

                    string? coreSchemaResourceName = Array.Find(
                        manifestResourceNames,
                        str => str.EndsWith("ApiSchema.json")
                    );

                    string? extensionSchemaResourceName = Array.Find(
                        manifestResourceNames,
                        str => str.Contains(".ApiSchema-") && str.EndsWith("EXTENSION.json")
                    );

                    if (coreSchemaResourceName != null)
                    {
                        _logger.LogInformation(
                            "Loading {CoreSchemaResourceName} from assembly",
                            coreSchemaResourceName
                        );
                        try
                        {
                            coreApiSchemaNode = LoadFromAssembly(coreSchemaResourceName, assembly);
                        }
                        catch (Exception ex)
                        {
                            ApiSchemaFailure failure = new(
                                "ParseError",
                                $"Failed to load core schema from assembly {assemblyPath}",
                                null,
                                ex
                            );
                            failures.Add(failure);
                            _logger.LogError(ex, failure.Message);
                        }
                    }
                    else if (extensionSchemaResourceName != null)
                    {
                        _logger.LogInformation(
                            "Loading {ExtensionSchemaResourceName} from assembly",
                            extensionSchemaResourceName
                        );
                        try
                        {
                            JsonNode extensionNode = LoadFromAssembly(extensionSchemaResourceName, assembly);
                            extensionApiSchemaNodes.Add(extensionNode);
                        }
                        catch (Exception ex)
                        {
                            ApiSchemaFailure failure = new(
                                "ParseError",
                                $"Failed to load extension schema from assembly {assemblyPath}",
                                null,
                                ex
                            );
                            failures.Add(failure);
                            _logger.LogError(ex, failure.Message);
                        }
                    }
                }
                catch (Exception ex)
                {
                    ApiSchemaFailure failure = new(
                        "FileSystem",
                        $"Failed to load assembly {assemblyPath}",
                        null,
                        ex
                    );
                    failures.Add(failure);
                    _logger.LogError(ex, failure.Message);
                }
            }

            if (failures.Count > 0)
            {
                return new ApiSchemaLoadResult(null, failures);
            }

            if (coreApiSchemaNode == null)
            {
                ApiSchemaFailure failure = new("Configuration", "No core API schema found in assemblies");
                failures.Add(failure);
                _logger.LogError(failure.Message);
                return new ApiSchemaLoadResult(null, failures);
            }

            return new ApiSchemaLoadResult(
                new ApiSchemaDocumentNodes(coreApiSchemaNode, extensionApiSchemaNodes.ToArray()),
                []
            );
        }
        catch (Exception ex)
        {
            ApiSchemaFailure failure = new("FileSystem", "Failed to process API schema assemblies", null, ex);
            failures.Add(failure);
            _logger.LogError(ex, failure.Message);
            return new ApiSchemaLoadResult(null, failures);
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

#pragma warning disable S125 // Sections of code should not be commented out
        // for (int i = 0; i < schemaNodes.ExtensionApiSchemaRootNodes.Length; i++)
        // {
        //     List<SchemaValidationFailure> extensionValidationErrors = _apiSchemaValidator.Validate(
        //         schemaNodes.ExtensionApiSchemaRootNodes[i]
        //     );

        //     if (extensionValidationErrors.Count > 0)
        //     {
        //         _logger.LogError("Extension Api schema {Index} validation failed.", i);

        //         foreach (var error in extensionValidationErrors)
        //         {
        //             _logger.LogError(
        //                 "[Extension Schema {Index}] {FailurePath} - {FailureMessages}",
        //                 i,
        //                 error.FailurePath.Value,
        //                 string.Join(", ", error.FailureMessages)
        //             );

        //             failures.Add(
        //                 new ApiSchemaFailure(
        //                     "Validation",
        //                     $"[Extension Schema {i}] {string.Join(", ", error.FailureMessages)}",
        //                     error.FailurePath
        //                 )
        //             );
        //         }
        //     }
        // }
#pragma warning restore S125 // Sections of code should not be commented out

        return failures;
    }

    /// <summary>
    /// Validates the schema and updates validation state for initial load only
    /// </summary>
    private ApiSchemaLoadStatus ValidateSchemaForInitialLoad(ApiSchemaDocumentNodes schemaNodes)
    {
        _apiSchemaFailures = ValidateAllSchemas(schemaNodes);
        _isSchemaValid = _apiSchemaFailures.Count == 0;
        return new(_isSchemaValid, _apiSchemaFailures);
    }

    /// <summary>
    /// Attempts to update the schema after validation. Used for reload and upload scenarios.
    /// Will not update if validation fails, keeping the existing schema intact.
    /// </summary>
    private ApiSchemaLoadStatus TryUpdateSchema(ApiSchemaDocumentNodes newSchemaNodes)
    {
        List<ApiSchemaFailure> failures = ValidateAllSchemas(newSchemaNodes);

        if (failures.Count > 0)
        {
            return new(false, failures);
        }

        // Validation passed - update the schema
        _apiSchemaNodes = newSchemaNodes;
        _reloadId = Guid.NewGuid();

        // Clear any previous validation failures since we now have a valid schema
        _apiSchemaFailures = [];
        _isSchemaValid = true;

        _logger.LogInformation("Schema updated successfully. New reload ID: {ReloadId}", _reloadId);

        return new(true, []);
    }

    /// <summary>
    /// Returns core and extension ApiSchema JsonNodes
    /// </summary>
    public ApiSchemaDocumentNodes GetApiSchemaNodes()
    {
        lock (_reloadLock)
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

                    // For initial load, we still throw an exception to maintain backward compatibility
                    // The failures are available via ApiSchemaFailures property
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

                var validationResult = ValidateSchemaForInitialLoad(schemaNodes);
                var validationSuccess = validationResult.Success;

                if (!validationSuccess)
                {
                    // Validation failures are already stored in _apiSchemaFailures by ValidateSchemaForInitialLoad
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
    /// Gets the current reload identifier
    /// </summary>
    public Guid ReloadId
    {
        get
        {
            lock (_reloadLock)
            {
                return _reloadId;
            }
        }
    }

    /// <summary>
    /// Gets whether the currently loaded API schema is valid
    /// </summary>
    public bool IsSchemaValid
    {
        get
        {
            lock (_reloadLock)
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
            lock (_reloadLock)
            {
                return _apiSchemaFailures;
            }
        }
    }

    /// <summary>
    /// Reloads the API schema from the configured source
    /// </summary>
    public Task<ApiSchemaLoadStatus> ReloadApiSchemaAsync()
    {
        lock (_reloadLock)
        {
            _logger.LogInformation("Reloading API schema from configured source...");

            var loadResult = LoadSchemaFromSource();
            var newSchemaNodes = loadResult.Nodes;
            var loadFailures = loadResult.Failures;

            if (loadFailures.Count > 0)
            {
                return Task.FromResult(new ApiSchemaLoadStatus(false, loadFailures));
            }

            if (newSchemaNodes == null)
            {
                ApiSchemaFailure failure = new(
                    "Configuration",
                    "Schema loading returned null without failures"
                );
                return Task.FromResult(
                    new ApiSchemaLoadStatus(false, new List<ApiSchemaFailure> { failure })
                );
            }

            return Task.FromResult(TryUpdateSchema(newSchemaNodes));
        }
    }

    /// <summary>
    /// Loads API schemas from the provided JSON nodes
    /// </summary>
    public Task<ApiSchemaLoadStatus> LoadApiSchemaFromAsync(JsonNode coreSchema, JsonNode[] extensionSchemas)
    {
        lock (_reloadLock)
        {
            _logger.LogInformation("Uploading and reloading API schemas from memory...");

            try
            {
                // Validate core schema structure
                var isExtension = coreSchema.SelectNodeFromPath(
                    "$.projectSchema.isExtensionProject",
                    _logger
                );
                if (isExtension != null && isExtension.GetValue<bool>())
                {
                    ApiSchemaFailure failure = new(
                        "Configuration",
                        "Core schema is marked as extension project"
                    );
                    _logger.LogError(failure.Message);
                    return Task.FromResult(
                        new ApiSchemaLoadStatus(false, new List<ApiSchemaFailure> { failure })
                    );
                }

                // Create new schema nodes and attempt to update
                var newSchemaNodes = new ApiSchemaDocumentNodes(coreSchema, extensionSchemas);
                return Task.FromResult(TryUpdateSchema(newSchemaNodes));
            }
            catch (Exception ex)
            {
                ApiSchemaFailure failure = new(
                    "ParseError",
                    "Failed to process uploaded API schemas",
                    null,
                    ex
                );
                _logger.LogError(ex, failure.Message);
                return Task.FromResult(
                    new ApiSchemaLoadStatus(false, new List<ApiSchemaFailure> { failure })
                );
            }
        }
    }

    /// <summary>
    /// Returns ApiSchemaAssemblyLoadContext for loading Assembly Context
    /// </summary>
    private sealed class ApiSchemaAssemblyLoadContext : AssemblyLoadContext
    {
        public ApiSchemaAssemblyLoadContext()
            : base(isCollectible: true) { }
    }
}
