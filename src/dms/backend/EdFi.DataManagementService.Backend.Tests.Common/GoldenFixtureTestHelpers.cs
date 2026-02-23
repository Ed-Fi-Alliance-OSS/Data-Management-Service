// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics;

namespace EdFi.DataManagementService.Backend.Tests.Common;

public static class GoldenFixtureTestHelpers
{
    public static bool ShouldUpdateGoldens()
    {
        var update = Environment.GetEnvironmentVariable("UPDATE_GOLDENS");

        return string.Equals(update, "1", StringComparison.OrdinalIgnoreCase)
            || string.Equals(update, "true", StringComparison.OrdinalIgnoreCase);
    }

    public static string RunGitDiff(string expectedPath, string actualPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(actualPath);

        var startInfo = new ProcessStartInfo("git")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        startInfo.ArgumentList.Add("diff");
        startInfo.ArgumentList.Add("--no-index");
        startInfo.ArgumentList.Add("--ignore-space-at-eol");
        startInfo.ArgumentList.Add("--ignore-cr-at-eol");
        startInfo.ArgumentList.Add("--");
        startInfo.ArgumentList.Add(expectedPath);
        startInfo.ArgumentList.Add(actualPath);

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        // Read both streams concurrently to prevent deadlocks under heavy diff output.
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        Task.WaitAll(outputTask, errorTask);

        var output = outputTask.Result;
        var error = errorTask.Result;

        if (process.ExitCode == 0)
        {
            return string.Empty;
        }

        if (process.ExitCode == 1)
        {
            return output;
        }

        return string.IsNullOrWhiteSpace(error) ? output : $"{error}\n{output}".Trim();
    }

    public static string FindSolutionRoot(string startDirectory)
    {
        return FindDirectoryContainingFile(startDirectory, "EdFi.DataManagementService.sln");
    }

    public static string FindProjectRoot(string startDirectory, string projectFileName)
    {
        return FindDirectoryContainingFile(startDirectory, projectFileName);
    }

    private static string FindDirectoryContainingFile(string startDirectory, string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(startDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

        var directory = new DirectoryInfo(startDirectory);

        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, fileName);

            if (File.Exists(candidate))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException($"Unable to locate {fileName} in parent directories.");
    }
}
