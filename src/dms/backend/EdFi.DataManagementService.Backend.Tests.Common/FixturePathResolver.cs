// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Backend.Tests.Common;

public static class FixturePathResolver
{
    private const string RepositoryRelativePrefix = "repo:";
    private static readonly string[] _repositoryLayoutMarkers =
    [
        Path.Combine("src", "dms", "EdFi.DataManagementService.sln"),
    ];

    public static string FindRepositoryRoot(string startDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(startDirectory);

        var directory = new DirectoryInfo(Path.GetFullPath(startDirectory));

        while (directory is not null)
        {
            var candidate = directory.FullName;

            if (ContainsAllMarkers(candidate, _repositoryLayoutMarkers))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException($"Unable to locate repository root from '{startDirectory}'.");
    }

    public static string ResolveRepositoryRelativePath(string startDirectory, string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);

        if (Path.IsPathRooted(relativePath))
        {
            throw new InvalidOperationException(
                $"Expected a repository-relative path but got rooted path '{relativePath}'."
            );
        }

        return ResolvePathWithinRoot(
            FindRepositoryRoot(startDirectory),
            relativePath,
            $"Repository-relative path escapes repository root: '{relativePath}'."
        );
    }

    public static string ResolveFixtureInputPath(
        string fixtureDirectory,
        string relativePath,
        string? repositoryRoot = null
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fixtureDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);

        if (Path.IsPathRooted(relativePath))
        {
            throw new InvalidOperationException(
                $"apiSchemaFiles must be relative paths, but got rooted path: '{relativePath}'"
            );
        }

        if (relativePath.StartsWith(RepositoryRelativePrefix, StringComparison.OrdinalIgnoreCase))
        {
            var repositoryRelativePath = relativePath[RepositoryRelativePrefix.Length..];

            ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRelativePath);

            var resolvedPath = ResolvePathWithinRoot(
                repositoryRoot ?? FindRepositoryRoot(fixtureDirectory),
                repositoryRelativePath,
                $"apiSchemaFiles repo path escapes the repository root: '{relativePath}'"
            );

            if (!File.Exists(resolvedPath))
            {
                throw new FileNotFoundException(
                    $"ApiSchema file declared in fixture.json not found: {resolvedPath}",
                    resolvedPath
                );
            }

            return resolvedPath;
        }

        var resolvedFixtureInputsDirectory = Path.GetFullPath(Path.Combine(fixtureDirectory, "inputs"));
        var resolvedFixturePath = ResolvePathWithinRoot(
            resolvedFixtureInputsDirectory,
            relativePath,
            $"apiSchemaFiles path escapes the inputs/ directory: '{relativePath}'"
        );

        if (!File.Exists(resolvedFixturePath))
        {
            throw new FileNotFoundException(
                $"ApiSchema file declared in fixture.json not found: {resolvedFixturePath}",
                resolvedFixturePath
            );
        }

        return resolvedFixturePath;
    }

    private static string ResolvePathWithinRoot(
        string rootDirectory,
        string relativePath,
        string escapeMessage
    )
    {
        var resolvedRootDirectory = Path.GetFullPath(rootDirectory);
        var resolvedPath = Path.GetFullPath(Path.Combine(resolvedRootDirectory, relativePath));
        var relativeResolvedPath = Path.GetRelativePath(resolvedRootDirectory, resolvedPath);

        if (
            relativeResolvedPath.StartsWith("..", StringComparison.Ordinal)
            || Path.IsPathRooted(relativeResolvedPath)
        )
        {
            throw new InvalidOperationException(escapeMessage);
        }

        return resolvedPath;
    }

    private static bool ContainsAllMarkers(string candidate, IReadOnlyList<string> markers)
    {
        return markers.All(marker => File.Exists(Path.Combine(candidate, marker)));
    }
}
