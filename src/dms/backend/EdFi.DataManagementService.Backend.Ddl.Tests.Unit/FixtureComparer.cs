// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics;
using System.Text;

namespace EdFi.DataManagementService.Backend.Ddl.Tests.Unit;

/// <summary>
/// Result of comparing a fixture's expected/ and actual/ directories.
/// </summary>
/// <param name="Passed">True if all files match.</param>
/// <param name="Message">Detailed diff output when files don't match, or empty if passed.</param>
public record FixtureCompareResult(bool Passed, string Message);

/// <summary>
/// Compares expected/ vs actual/ directories for a fixture using git diff --no-index.
/// Supports UPDATE_GOLDENS mode to copy actual/ -> expected/.
/// </summary>
public static class FixtureComparer
{
    /// <summary>
    /// Compares all files in expected/ against corresponding files in actual/.
    /// If UPDATE_GOLDENS is set, copies actual/ to expected/ first and reports success.
    /// </summary>
    /// <param name="fixtureDirectory">Absolute path to the fixture directory.</param>
    /// <returns>A result indicating pass/fail with diff details on failure.</returns>
    public static FixtureCompareResult Compare(string fixtureDirectory, bool? updateGoldens = null)
    {
        var expectedDir = Path.Combine(fixtureDirectory, "expected");
        var actualDir = Path.Combine(fixtureDirectory, "actual");

        if (!Directory.Exists(actualDir))
        {
            return new FixtureCompareResult(
                false,
                $"actual/ directory does not exist: {actualDir}. Run FixtureRunner first."
            );
        }

        if (updateGoldens ?? ShouldUpdateGoldens())
        {
            UpdateGoldens(expectedDir, actualDir);
            return new FixtureCompareResult(true, "Golden files updated from actual/.");
        }

        if (!Directory.Exists(expectedDir))
        {
            return new FixtureCompareResult(
                false,
                $"expected/ directory does not exist: {expectedDir}. Set UPDATE_GOLDENS=1 to generate."
            );
        }

        var expectedFiles = Directory
            .GetFiles(expectedDir, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(expectedDir, f))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (expectedFiles.Count == 0)
        {
            return new FixtureCompareResult(
                false,
                $"expected/ directory is empty: {expectedDir}. Set UPDATE_GOLDENS=1 to generate."
            );
        }

        var failures = new StringBuilder();

        foreach (var relativePath in expectedFiles)
        {
            var expectedPath = Path.Combine(expectedDir, relativePath);
            var actualPath = Path.Combine(actualDir, relativePath);

            if (!File.Exists(actualPath))
            {
                failures.AppendLine($"Missing in actual/: {relativePath}");
                continue;
            }

            var diff = RunGitDiff(expectedPath, actualPath);

            if (!string.IsNullOrWhiteSpace(diff))
            {
                failures.AppendLine($"--- {relativePath} ---");
                failures.AppendLine(diff);
                failures.AppendLine();
            }
        }

        // Check for unexpected extra files in actual/ that aren't in expected/
        // Use case-sensitive (Ordinal) comparison so files differing only by case are detected
        var actualFiles = Directory
            .GetFiles(actualDir, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(actualDir, f))
            .ToHashSet(StringComparer.Ordinal);

        var expectedSet = new HashSet<string>(expectedFiles, StringComparer.Ordinal);
        var extraFiles = actualFiles.Except(expectedSet, StringComparer.Ordinal).Order().ToList();

        if (extraFiles.Count > 0)
        {
            failures.AppendLine("Extra files in actual/ not present in expected/:");
            foreach (var extra in extraFiles)
            {
                failures.AppendLine($"  {extra}");
            }
        }

        var message = failures.ToString().TrimEnd();

        return string.IsNullOrEmpty(message)
            ? new FixtureCompareResult(true, string.Empty)
            : new FixtureCompareResult(false, message);
    }

    private static bool ShouldUpdateGoldens()
    {
        var update = Environment.GetEnvironmentVariable("UPDATE_GOLDENS");

        return string.Equals(update, "1", StringComparison.OrdinalIgnoreCase)
            || string.Equals(update, "true", StringComparison.OrdinalIgnoreCase);
    }

    private static void UpdateGoldens(string expectedDir, string actualDir)
    {
        // Clean and recreate expected/ from actual/
        if (Directory.Exists(expectedDir))
        {
            Directory.Delete(expectedDir, recursive: true);
        }

        CopyDirectory(actualDir, expectedDir);
    }

    private static void CopyDirectory(string sourceDir, string targetDir)
    {
        Directory.CreateDirectory(targetDir);

        foreach (var file in Directory.GetFiles(sourceDir))
        {
            File.Copy(file, Path.Combine(targetDir, Path.GetFileName(file)));
        }

        foreach (var dir in Directory.GetDirectories(sourceDir))
        {
            CopyDirectory(dir, Path.Combine(targetDir, Path.GetFileName(dir)));
        }
    }

    private static string RunGitDiff(string expectedPath, string actualPath)
    {
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

        using var process = new Process();
        process.StartInfo = startInfo;
        process.Start();
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();

        if (!process.WaitForExit(30_000))
        {
            process.Kill();
            process.WaitForExit();
            throw new TimeoutException("git diff timed out after 30 seconds");
        }

        var output = outputTask.GetAwaiter().GetResult();
        var error = errorTask.GetAwaiter().GetResult();

        // Exit code 0 = no diff, 1 = diff found, other = error
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
}
