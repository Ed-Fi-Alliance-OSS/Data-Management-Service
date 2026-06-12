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
    private const string BundledApiSchemaDirectoryName = "ApiSchema";

    private string? _resolvedWorkspaceRoot;
    private string? _canonicalWorkspaceRoot;

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

    private string GetOrResolveCanonicalWorkspaceRoot()
    {
        if (_canonicalWorkspaceRoot is not null)
        {
            return _canonicalWorkspaceRoot;
        }

        _canonicalWorkspaceRoot = ResolveCanonicalPath(GetOrResolveWorkspaceRoot());
        return _canonicalWorkspaceRoot;
    }

    public ApiSchemaAssetManifest GetManifest()
    {
        var workspaceRoot = GetOrResolveWorkspaceRoot();
        var manifestPath = Path.Combine(workspaceRoot, ManifestFileName);

        if (!File.Exists(manifestPath))
        {
            logger.LogError(
                "Bootstrap ApiSchema manifest not found at {ManifestPath}",
                SanitizeForLog(manifestPath)
            );
            throw new InvalidOperationException(
                $"Bootstrap manifest file '{ManifestFileName}' was not found under the configured "
                    + $"ApiSchema workspace. Expected path: {manifestPath}."
            );
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

        var fullPath = Path.GetFullPath(Path.Combine(GetOrResolveWorkspaceRoot(), relativePath));

        return ValidatePathInsideCanonicalWorkspace(fullPath, relativePath);
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

        return Directory
            .EnumerateFiles(validatedDir, "*.xsd", SearchOption.TopDirectoryOnly)
            .Select(f => ValidatePathInsideCanonicalWorkspace(f, f));
    }

    private string ValidatePathInsideCanonicalWorkspace(string fullPath, string sourcePath)
    {
        var canonicalWorkspaceRoot = GetOrResolveCanonicalWorkspaceRoot();
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
                $"ApiSchema asset path '{sourcePath}' resolves to '{canonicalPath}', which is outside the "
                    + $"configured workspace root '{canonicalWorkspaceRoot}'. Manifest paths must remain "
                    + "within the workspace after resolving symbolic links."
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
                    $"ApiSchema asset path '{originalPath}' contains symbolic link "
                        + $"'{fileSystemInfo.FullName}' whose target could not be resolved."
                );
            }

            return Path.GetFullPath(resolvedTarget.FullName);
        }
        catch (IOException ex)
        {
            throw new InvalidOperationException(
                $"ApiSchema asset path '{originalPath}' contains symbolic link "
                    + $"'{fileSystemInfo.FullName}' whose target could not be resolved: {ex.Message}",
                ex
            );
        }
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
