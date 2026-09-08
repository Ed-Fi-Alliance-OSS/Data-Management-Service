// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Core.ApiSchema;

/// <summary>
/// Resolves ApiSchema workspace paths after rejecting traversal and canonicalizing symbolic links.
/// </summary>
public sealed class ApiSchemaWorkspacePathResolver(string workspaceRoot)
{
    private string? _canonicalWorkspaceRoot;

    public string WorkspaceRoot { get; } = Path.GetFullPath(workspaceRoot);

    public string CanonicalWorkspaceRoot => _canonicalWorkspaceRoot ??= ResolveCanonicalPath(WorkspaceRoot);

    public string ResolveManifestRelativePath(string relativePath)
    {
        if (Path.IsPathRooted(relativePath))
        {
            throw new InvalidOperationException(
                $"Manifest path '{relativePath}' is an absolute (rooted) path. "
                    + "Only workspace-relative paths are permitted."
            );
        }

        if (ContainsParentTraversal(relativePath))
        {
            throw new InvalidOperationException(
                $"Manifest path '{relativePath}' contains a parent-directory traversal ('..') component. "
                    + "Paths that escape the workspace root are not permitted."
            );
        }

        var fullPath = Path.GetFullPath(Path.Combine(WorkspaceRoot, relativePath));

        return ResolveWorkspacePath(fullPath, relativePath);
    }

    public string ResolveWorkspacePath(string fullPath, string sourcePath)
    {
        var canonicalPath = ResolveCanonicalPath(fullPath);
        var relativeToWorkspace = Path.GetRelativePath(CanonicalWorkspaceRoot, canonicalPath);

        if (
            Path.IsPathRooted(relativeToWorkspace)
            || relativeToWorkspace == ".."
            || relativeToWorkspace.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            || relativeToWorkspace.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal)
        )
        {
            throw new InvalidOperationException(
                $"ApiSchema asset path '{sourcePath}' resolves to '{canonicalPath}', which is outside the "
                    + $"configured workspace root '{CanonicalWorkspaceRoot}'. Manifest paths must remain "
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

    private static bool ContainsParentTraversal(string path)
    {
        var parts = path.Split(['/', '\\'], StringSplitOptions.None);
        return Array.Exists(parts, p => p == "..");
    }
}
