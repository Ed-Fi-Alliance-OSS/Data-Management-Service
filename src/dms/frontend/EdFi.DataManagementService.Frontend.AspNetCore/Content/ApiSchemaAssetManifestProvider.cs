// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using EdFi.DataManagementService.Core.Configuration;
using Microsoft.Extensions.Options;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Content;

/// <summary>
/// Provides access to ApiSchema asset manifests and validated path resolution.
/// </summary>
public interface IApiSchemaAssetManifestProvider
{
    /// <summary>
    /// Loads and validates the bootstrap-api-schema-manifest.json from the configured ApiSchemaPath,
    /// or synthesizes the same manifest shape from package-manifest.json files in package content.
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
    /// xsdDirectory is within the workspace root. Returns an empty enumerable if xsdDirectory is absent.
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
    private const int SupportedManifestVersion = 1;
    private const string ManifestFileName = "bootstrap-api-schema-manifest.json";
    private const string PackageManifestFileName = "package-manifest.json";
    private const string BundledApiSchemaDirectoryName = "ApiSchema";

    private string? _resolvedWorkspaceRoot;

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

    public ApiSchemaAssetManifest GetManifest()
    {
        var workspaceRoot = GetOrResolveWorkspaceRoot();
        var manifestPath = Path.Combine(workspaceRoot, ManifestFileName);

        if (!File.Exists(manifestPath))
        {
            return GetManifestFromPackageManifests(workspaceRoot, manifestPath);
        }

        string json;
        try
        {
            json = File.ReadAllText(manifestPath);
        }
        catch (IOException ex)
        {
            throw new InvalidOperationException(
                $"Failed to read bootstrap manifest file '{ManifestFileName}': {ex.Message}",
                ex
            );
        }

        ApiSchemaAssetManifest manifest;
        try
        {
            manifest =
                JsonSerializer.Deserialize<ApiSchemaAssetManifest>(
                    json,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
                )
                ?? throw new InvalidOperationException(
                    $"Bootstrap manifest file '{ManifestFileName}' deserializes to null. "
                        + "The file may be empty or contain only a JSON null literal."
                );
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"Bootstrap manifest file '{ManifestFileName}' contains malformed JSON: {ex.Message}",
                ex
            );
        }

        if (manifest.Version != SupportedManifestVersion)
        {
            throw new InvalidOperationException(
                $"Bootstrap manifest version {manifest.Version} is not supported. "
                    + $"Only version {SupportedManifestVersion} is accepted."
            );
        }

        if (manifest.Projects is null || manifest.Projects.Count == 0)
        {
            throw new InvalidOperationException(
                $"Bootstrap manifest '{ManifestFileName}' contains zero projects. "
                    + "A valid staged workspace must declare at least one project."
            );
        }

        return manifest;
    }

    private ApiSchemaAssetManifest GetManifestFromPackageManifests(
        string workspaceRoot,
        string expectedBootstrapManifestPath
    )
    {
        if (!Directory.Exists(workspaceRoot))
        {
            logger.LogError(
                "ApiSchema workspace root not found: {WorkspaceRoot}",
                SanitizeForLog(workspaceRoot)
            );
            throw new InvalidOperationException(
                $"Bootstrap manifest file '{ManifestFileName}' was not found under the configured "
                    + $"ApiSchemaPath. Expected path: {expectedBootstrapManifestPath}. "
                    + $"The ApiSchema workspace root also does not exist: {workspaceRoot}."
            );
        }

        var packageManifestPaths = Directory
            .EnumerateFiles(workspaceRoot, PackageManifestFileName, SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (packageManifestPaths.Length == 0)
        {
            logger.LogError(
                "No bootstrap or package ApiSchema manifest files found under workspace root {WorkspaceRoot}",
                SanitizeForLog(workspaceRoot)
            );
            throw new InvalidOperationException(
                $"Bootstrap manifest file '{ManifestFileName}' was not found under the configured "
                    + $"ApiSchemaPath. Expected path: {expectedBootstrapManifestPath}. "
                    + $"No '{PackageManifestFileName}' files were found under {workspaceRoot}."
            );
        }

        List<ApiSchemaProject> projects = [];
        foreach (var packageManifestPath in packageManifestPaths)
        {
            var packageManifest = ReadPackageManifest(packageManifestPath);
            if (packageManifest.Version != SupportedManifestVersion)
            {
                throw new InvalidOperationException(
                    $"ApiSchema package manifest version {packageManifest.Version} is not supported. "
                        + $"Only version {SupportedManifestVersion} is accepted. "
                        + $"Manifest path: {packageManifestPath}"
                );
            }

            var packageRoot = Path.GetDirectoryName(packageManifestPath);
            if (string.IsNullOrEmpty(packageRoot))
            {
                throw new InvalidOperationException(
                    $"Unable to determine package root for ApiSchema package manifest {packageManifestPath}."
                );
            }

            projects.Add(
                new ApiSchemaProject(
                    packageManifest.ProjectName,
                    packageManifest.ProjectEndpointName,
                    packageManifest.IsExtensionProject,
                    BuildWorkspaceRelativePath(workspaceRoot, packageRoot, packageManifest.SchemaPath),
                    packageManifest.DiscoverySpecPath is null
                        ? null
                        : BuildWorkspaceRelativePath(
                            workspaceRoot,
                            packageRoot,
                            packageManifest.DiscoverySpecPath
                        ),
                    packageManifest.XsdDirectory is null
                        ? null
                        : BuildWorkspaceRelativePath(workspaceRoot, packageRoot, packageManifest.XsdDirectory)
                )
            );
        }

        return new ApiSchemaAssetManifest(SupportedManifestVersion, projects);
    }

    private static string BuildWorkspaceRelativePath(
        string workspaceRoot,
        string packageRoot,
        string packageRelativePath
    )
    {
        var workspaceRelativePackageRoot = Path.GetRelativePath(workspaceRoot, packageRoot);
        return Path.Combine(workspaceRelativePackageRoot, packageRelativePath)
            .Replace(Path.DirectorySeparatorChar, '/')
            .Replace(Path.AltDirectorySeparatorChar, '/');
    }

    private static ApiSchemaPackageManifest ReadPackageManifest(string packageManifestPath)
    {
        string json;
        try
        {
            json = File.ReadAllText(packageManifestPath);
        }
        catch (IOException ex)
        {
            throw new InvalidOperationException(
                $"Failed to read ApiSchema package manifest '{packageManifestPath}': {ex.Message}",
                ex
            );
        }

        try
        {
            return JsonSerializer.Deserialize<ApiSchemaPackageManifest>(
                    json,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
                )
                ?? throw new InvalidOperationException(
                    $"ApiSchema package manifest '{packageManifestPath}' deserializes to null. "
                        + "The file may be empty or contain only a JSON null literal."
                );
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"ApiSchema package manifest '{packageManifestPath}' contains malformed JSON: {ex.Message}",
                ex
            );
        }
    }

    public string ResolveValidatedPath(string relativePath)
    {
        if (Path.IsPathRooted(relativePath))
        {
            throw new InvalidOperationException(
                $"Manifest path '{relativePath}' is an absolute (rooted) path. "
                    + "Only workspace-relative paths are permitted."
            );
        }

        if (relativePath.Contains("..", StringComparison.Ordinal) && ContainsParentTraversal(relativePath))
        {
            throw new InvalidOperationException(
                $"Manifest path '{relativePath}' contains a parent-directory traversal ('..') component. "
                    + "Paths that escape the workspace root are not permitted."
            );
        }

        var workspaceRoot = GetOrResolveWorkspaceRoot();
        var fullPath = Path.GetFullPath(Path.Combine(workspaceRoot, relativePath));
        var relativeToWorkspace = Path.GetRelativePath(workspaceRoot, fullPath);

        if (
            Path.IsPathRooted(relativeToWorkspace)
            || relativeToWorkspace == ".."
            || relativeToWorkspace.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            || relativeToWorkspace.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal)
        )
        {
            throw new InvalidOperationException(
                $"Resolved path '{fullPath}' is outside the configured workspace root '{workspaceRoot}'. "
                    + "Manifest paths must remain within the workspace."
            );
        }

        return fullPath;
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
            return [];
        }

        var workspaceRoot = GetOrResolveWorkspaceRoot();
        return Directory
            .EnumerateFiles(validatedDir, "*.xsd", SearchOption.TopDirectoryOnly)
            .Select(f => ResolveValidatedPath(Path.GetRelativePath(workspaceRoot, f)));
    }

    /// <summary>
    /// Returns true if the path contains a ".." segment that is an actual parent-directory component
    /// rather than a literal substring inside a file/directory name.
    /// </summary>
    private static bool ContainsParentTraversal(string path)
    {
        var parts = path.Split(['/', '\\'], StringSplitOptions.None);
        return Array.Exists(parts, p => p == "..");
    }

    /// <summary>
    /// Sanitizes a string for safe logging by allowing only safe characters.
    /// Uses a whitelist approach to prevent log injection and log forging attacks.
    /// Allows: letters, digits, spaces, and safe punctuation (_-.:/)
    /// </summary>
    private static string SanitizeForLog(string? input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return string.Empty;
        }

        return new string(
            input
                .Where(c =>
                    char.IsLetterOrDigit(c)
                    || c == ' '
                    || c == '_'
                    || c == '-'
                    || c == '.'
                    || c == ':'
                    || c == '/'
                )
                .ToArray()
        );
    }
}

internal sealed record ApiSchemaPackageManifest(
    int Version,
    string PackageId,
    string ProjectName,
    string ProjectEndpointName,
    bool IsExtensionProject,
    string SchemaPath,
    string? DiscoverySpecPath,
    string? XsdDirectory
);
