// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
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
    private const string BootstrapManifestFileName = "bootstrap-api-schema-manifest.json";
    private const int SupportedBootstrapManifestVersion = 1;

    private sealed record BootstrapApiSchemaManifest(
        int Version,
        IReadOnlyList<BootstrapApiSchemaProject>? Projects
    );

    private sealed record BootstrapApiSchemaProject(
        string? ProjectName,
        string? ProjectEndpointName,
        string? SchemaPath
    );

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
                var (node, failure) = ReadApiSchemaFile(filePath);
                if (failure is not null)
                {
                    failures.Add(failure);
                }
                else if (node is not null)
                {
                    fileContents.Add(node);
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

        var manifestPath = Path.Combine(apiSchemaPath, BootstrapManifestFileName);
        if (File.Exists(manifestPath))
        {
            return LoadSchemaFromManifest(Path.GetFullPath(apiSchemaPath), manifestPath);
        }

        var (apiSchemaNodes, readFailures) = ReadApiSchemaFiles(apiSchemaPath);
        if (readFailures.Count > 0)
        {
            return new(null, readFailures);
        }

        return CreateApiSchemaLoadResult(
            apiSchemaNodes,
            $"No API schema files found in directory {apiSchemaPath}"
        );
    }

    private ApiSchemaLoadResult LoadSchemaFromManifest(string workspaceRoot, string manifestPath)
    {
        var (manifest, manifestFailure) = ReadBootstrapManifest(manifestPath);
        if (manifestFailure is not null)
        {
            return new(null, [manifestFailure]);
        }

        if (manifest is null)
        {
            ApiSchemaFailure failure = new(
                "Configuration",
                $"Bootstrap manifest file '{BootstrapManifestFileName}' deserialized to null"
            );
            _logger.LogError(failure.Message);
            return new(null, [failure]);
        }

        List<ApiSchemaFailure> failures = ValidateBootstrapManifest(manifest);
        if (failures.Count > 0)
        {
            return new(null, failures);
        }

        IReadOnlyList<BootstrapApiSchemaProject> projects = manifest.Projects ?? [];
        List<JsonNode> apiSchemaNodes = [];
        foreach ((BootstrapApiSchemaProject project, int index) in projects.Select((p, i) => (p, i)))
        {
            var projectDescription = DescribeManifestProject(project, index);
            var schemaPath = project.SchemaPath;

            if (string.IsNullOrWhiteSpace(schemaPath))
            {
                failures.Add(
                    new ApiSchemaFailure(
                        "Configuration",
                        $"Bootstrap manifest project {projectDescription} must declare a non-empty schemaPath."
                    )
                );
                continue;
            }

            string resolvedSchemaPath;
            try
            {
                resolvedSchemaPath = ResolveManifestRelativePath(workspaceRoot, schemaPath);
            }
            catch (InvalidOperationException ex)
            {
                failures.Add(
                    new ApiSchemaFailure(
                        "Configuration",
                        $"Bootstrap manifest project {projectDescription} has invalid schemaPath '{schemaPath}': {ex.Message}",
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
                        $"Bootstrap manifest project {projectDescription} declares schemaPath '{schemaPath}', "
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
                apiSchemaNodes.Add(node);
            }
        }

        if (failures.Count > 0)
        {
            return new(null, failures);
        }

        return CreateApiSchemaLoadResult(
            apiSchemaNodes,
            $"Bootstrap manifest file '{BootstrapManifestFileName}' did not select any API schema files."
        );
    }

    private (BootstrapApiSchemaManifest? Manifest, ApiSchemaFailure? Failure) ReadBootstrapManifest(
        string manifestPath
    )
    {
        try
        {
            string json = File.ReadAllText(manifestPath);
            var manifest = JsonSerializer.Deserialize<BootstrapApiSchemaManifest>(
                json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
            );
            return (manifest, null);
        }
        catch (JsonException ex)
        {
            ApiSchemaFailure failure = new(
                "ParseError",
                $"Bootstrap manifest file '{BootstrapManifestFileName}' contains malformed JSON",
                null,
                ex
            );
            _logger.LogError(ex, failure.Message);
            return (null, failure);
        }
        catch (IOException ex)
        {
            ApiSchemaFailure failure = new(
                "FileSystem",
                $"Error reading bootstrap manifest file '{manifestPath}'",
                null,
                ex
            );
            _logger.LogError(ex, failure.Message);
            return (null, failure);
        }
        catch (UnauthorizedAccessException ex)
        {
            ApiSchemaFailure failure = new(
                "AccessDenied",
                $"Access denied to bootstrap manifest file '{manifestPath}'",
                null,
                ex
            );
            _logger.LogError(ex, failure.Message);
            return (null, failure);
        }
    }

    private List<ApiSchemaFailure> ValidateBootstrapManifest(BootstrapApiSchemaManifest manifest)
    {
        List<ApiSchemaFailure> failures = [];

        if (manifest.Version != SupportedBootstrapManifestVersion)
        {
            failures.Add(
                new ApiSchemaFailure(
                    "Configuration",
                    $"Bootstrap manifest version {manifest.Version} is not supported. "
                        + $"Only version {SupportedBootstrapManifestVersion} is accepted."
                )
            );
        }

        if (manifest.Projects is null || manifest.Projects.Count == 0)
        {
            failures.Add(
                new ApiSchemaFailure(
                    "Configuration",
                    $"Bootstrap manifest '{BootstrapManifestFileName}' contains zero projects."
                )
            );
        }

        foreach (ApiSchemaFailure failure in failures)
        {
            _logger.LogError(failure.Message);
        }

        return failures;
    }

    private static string ResolveManifestRelativePath(string workspaceRoot, string relativePath)
    {
        if (Path.IsPathRooted(relativePath))
        {
            throw new InvalidOperationException(
                "Only workspace-relative paths are permitted; rooted paths are rejected."
            );
        }

        if (ContainsParentTraversal(relativePath))
        {
            throw new InvalidOperationException(
                "Paths containing a parent-directory traversal ('..') component are rejected."
            );
        }

        var fullPath = Path.GetFullPath(Path.Combine(workspaceRoot, relativePath));
        var canonicalWorkspaceRoot = ResolveCanonicalPath(workspaceRoot);
        var canonicalPath = ResolveCanonicalPath(fullPath);
        var relativeToWorkspace = Path.GetRelativePath(canonicalWorkspaceRoot, canonicalPath);

        if (
            Path.IsPathRooted(relativeToWorkspace)
            || relativeToWorkspace == ".."
            || relativeToWorkspace.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            || relativeToWorkspace.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal)
        )
        {
            throw new InvalidOperationException(
                $"Path resolves to '{canonicalPath}', which is outside the configured ApiSchema workspace."
            );
        }

        return canonicalPath;
    }

    private static string ResolveCanonicalPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrEmpty(root))
        {
            return fullPath;
        }

        var canonicalPath = root;
        var pathWithoutRoot = fullPath[root.Length..];
        var pathParts = pathWithoutRoot.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries
        );

        foreach (var pathPart in pathParts)
        {
            var candidatePath = Path.Combine(canonicalPath, pathPart);
            var fileSystemInfo = GetFileSystemInfo(candidatePath);
            if (fileSystemInfo?.LinkTarget is not null)
            {
                canonicalPath = ResolveSymbolicLink(fileSystemInfo, path);
                continue;
            }

            canonicalPath = candidatePath;
        }

        return Path.GetFullPath(canonicalPath);
    }

    private static FileSystemInfo? GetFileSystemInfo(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            return attributes.HasFlag(FileAttributes.Directory)
                ? new DirectoryInfo(path)
                : new FileInfo(path);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
    }

    private static string ResolveSymbolicLink(FileSystemInfo fileSystemInfo, string originalPath)
    {
        try
        {
            var resolvedTarget = fileSystemInfo.ResolveLinkTarget(returnFinalTarget: true);
            if (resolvedTarget is null)
            {
                throw new InvalidOperationException(
                    $"Path '{originalPath}' contains symbolic link '{fileSystemInfo.FullName}' "
                        + "whose target could not be resolved."
                );
            }

            return Path.GetFullPath(resolvedTarget.FullName);
        }
        catch (IOException ex)
        {
            throw new InvalidOperationException(
                $"Path '{originalPath}' contains symbolic link '{fileSystemInfo.FullName}' "
                    + $"whose target could not be resolved: {ex.Message}",
                ex
            );
        }
    }

    private static bool ContainsParentTraversal(string path)
    {
        var parts = path.Split(['/', '\\'], StringSplitOptions.None);
        return Array.Exists(parts, p => p == "..");
    }

    private static string DescribeManifestProject(BootstrapApiSchemaProject project, int index)
    {
        if (!string.IsNullOrWhiteSpace(project.ProjectName))
        {
            return $"'{project.ProjectName}'";
        }

        if (!string.IsNullOrWhiteSpace(project.ProjectEndpointName))
        {
            return $"'{project.ProjectEndpointName}'";
        }

        return $"at index {index}";
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
            JsonNode? coreApiSchemaNode = apiSchemaNodes.FirstOrDefault(node =>
                !node.SelectRequiredNodeFromPathAs<bool>("$.projectSchema.isExtensionProject", _logger)
            );

            if (coreApiSchemaNode is null)
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
