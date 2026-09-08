// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.Utilities;
using Microsoft.Extensions.Options;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Content;

/// <summary>
/// Provides access to ApiSchema asset manifests and validated path resolution.
/// </summary>
public interface IApiSchemaAssetManifestProvider
{
    /// <summary>
    /// Loads and validates the bootstrap-api-schema-manifest.json from the configured ApiSchemaPath.
    /// Throws <see cref="InvalidOperationException"/> when the manifest data is missing, malformed,
    /// has an unsupported version, or contains zero projects.
    /// </summary>
    ApiSchemaAssetManifest GetManifest();

    /// <summary>
    /// Resolves a manifest-relative path to a fully qualified path that is guaranteed to remain
    /// under the workspace root. Throws <see cref="InvalidOperationException"/> for rooted paths,
    /// paths containing ".." traversal components, or resolution results outside the workspace root.
    /// </summary>
    /// <param name="relativePath">The manifest-relative path to resolve.</param>
    /// <returns>The fully qualified absolute path.</returns>
    string ResolveValidatedPath(string relativePath);

    /// <summary>
    /// Enumerates the XSD files for a given manifest project after validating that the project's
    /// xsdDirectory is within the workspace root and exists. Returns an empty enumerable if
    /// xsdDirectory is absent.
    /// </summary>
    /// <param name="project">The manifest project whose XSD files are to be enumerated.</param>
    /// <returns>Validated absolute paths to each XSD file in the project's xsdDirectory.</returns>
    IEnumerable<string> EnumerateValidatedXsdFiles(ApiSchemaProject project);
}

/// <summary>
/// Reads and validates ApiSchema manifest data from the configured ApiSchemaPath,
/// and provides validated path resolution so content loading cannot escape the workspace.
/// </summary>
public class ApiSchemaAssetManifestProvider(
    IOptions<AppSettings> appSettings,
    ILogger<ApiSchemaAssetManifestProvider> logger
) : IApiSchemaAssetManifestProvider
{
    private const string BundledApiSchemaDirectoryName = "ApiSchema";

    private string? _resolvedWorkspaceRoot;
    private ApiSchemaWorkspacePathResolver? _workspacePathResolver;

    private string GetOrResolveWorkspaceRoot()
    {
        if (_resolvedWorkspaceRoot is not null)
        {
            return _resolvedWorkspaceRoot;
        }

        var path = appSettings.Value.UseApiSchemaPath
            ? appSettings.Value.ApiSchemaPath
            : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, BundledApiSchemaDirectoryName);

        if (string.IsNullOrEmpty(path))
        {
            throw new InvalidOperationException(
                "ApiSchemaPath is not configured in AppSettings. "
                    + "Set AppSettings:ApiSchemaPath to the bootstrap workspace directory."
            );
        }

        _resolvedWorkspaceRoot = Path.GetFullPath(path);
        return _resolvedWorkspaceRoot;
    }

    private ApiSchemaWorkspacePathResolver GetOrCreatePathResolver()
    {
        if (_workspacePathResolver is not null)
        {
            return _workspacePathResolver;
        }

        _workspacePathResolver = new ApiSchemaWorkspacePathResolver(GetOrResolveWorkspaceRoot());
        return _workspacePathResolver;
    }

    public ApiSchemaAssetManifest GetManifest()
    {
        var workspaceRoot = GetOrResolveWorkspaceRoot();
        try
        {
            return ApiSchemaAssetManifestReader.ReadFromWorkspace(workspaceRoot);
        }
        catch (ApiSchemaAssetManifestException ex)
        {
            logger.LogError(
                ex,
                "Invalid ApiSchema manifest in workspace {WorkspaceRoot}: {Message}",
                LoggingSanitizer.SanitizeForLogging(workspaceRoot),
                LoggingSanitizer.SanitizeForLogging(ex.Message)
            );

            throw new InvalidOperationException(
                $"Invalid ApiSchema manifest in workspace '{workspaceRoot}': {ex.Message}",
                ex
            );
        }
    }

    public string ResolveValidatedPath(string relativePath)
    {
        return GetOrCreatePathResolver().ResolveManifestRelativePath(relativePath);
    }

    public IEnumerable<string> EnumerateValidatedXsdFiles(ApiSchemaProject project)
    {
        if (project.XsdDirectory is null)
        {
            return [];
        }

        var validatedDir = ResolveValidatedPath(project.XsdDirectory);
        if (!Directory.Exists(validatedDir))
        {
            throw new InvalidOperationException(
                $"Manifest project '{project.ProjectName}' declares xsdDirectory "
                    + $"'{project.XsdDirectory}', but the resolved directory '{validatedDir}' does not exist."
            );
        }

        var allXsdFiles = Directory
            .EnumerateFiles(validatedDir, "*.xsd", SearchOption.AllDirectories)
            .ToList();
        var nestedXsdFile = allXsdFiles.Find(f => IsNestedUnderDirectory(f, validatedDir));
        if (nestedXsdFile is not null)
        {
            throw new InvalidOperationException(
                $"Manifest project '{project.ProjectName}' declares xsdDirectory '{project.XsdDirectory}', "
                    + $"but nested XSD file '{nestedXsdFile}' was found. XSD files must be flattened "
                    + "directly under the declared xsdDirectory."
            );
        }

        return allXsdFiles
            .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .Select(f => GetOrCreatePathResolver().ResolveWorkspacePath(f, f));
    }

    private static bool IsNestedUnderDirectory(string filePath, string directoryPath)
    {
        var relativePath = Path.GetRelativePath(directoryPath, filePath);
        return relativePath.Contains(Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || relativePath.Contains(Path.AltDirectorySeparatorChar, StringComparison.Ordinal);
    }
}
