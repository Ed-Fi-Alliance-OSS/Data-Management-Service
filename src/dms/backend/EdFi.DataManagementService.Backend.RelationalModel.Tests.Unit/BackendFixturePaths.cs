// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Backend.RelationalModel.Tests.Unit;

internal static class BackendFixturePaths
{
    public static string GetAuthoritativeFixtureRoot(string startDirectory)
    {
        var solutionRoot = FindSolutionRoot(startDirectory);
        return Path.Combine(solutionRoot, "backend", "Fixtures", "authoritative");
    }

    private static string FindSolutionRoot(string startDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(startDirectory);

        var directory = new DirectoryInfo(startDirectory);

        while (directory is not null)
        {
            var solutionFilePath = Path.Combine(directory.FullName, "EdFi.DataManagementService.sln");

            if (File.Exists(solutionFilePath))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            "Unable to locate EdFi.DataManagementService.sln in parent directories."
        );
    }
}
