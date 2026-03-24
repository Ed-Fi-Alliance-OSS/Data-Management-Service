// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Postgresql.Tests.Integration;

internal static class RepositoryPathHelper
{
    public static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);

        while (directory is not null)
        {
            var candidate = directory.FullName;

            if (
                File.Exists(Path.Combine(candidate, "tasks.json"))
                && File.Exists(Path.Combine(candidate, "progress.txt"))
            )
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            $"Unable to locate repository root from '{TestContext.CurrentContext.TestDirectory}'."
        );
    }

    public static string ResolveRepositoryRelativePath(string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);

        if (Path.IsPathRooted(relativePath))
        {
            throw new InvalidOperationException(
                $"Expected a repository-relative path but got rooted path '{relativePath}'."
            );
        }

        return Path.GetFullPath(Path.Combine(FindRepositoryRoot(), relativePath));
    }
}
