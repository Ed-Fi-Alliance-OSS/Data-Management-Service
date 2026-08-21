// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics;

namespace EdFi.DataManagementService.Performance.Harness.Measurement;

/// <summary>
/// Reads the subject tree's commit identity from git: HEAD and the dirty paths. The working
/// directory anchors repository discovery, so a run inside the baseline worktree reports the
/// baseline commit while the runner commit is supplied through configuration.
/// </summary>
public static class GitIdentity
{
    public static string HeadCommit(string workingDirectory) =>
        RunGit("rev-parse HEAD", workingDirectory).Trim().ToLowerInvariant();

    public static IReadOnlyList<string> DirtyPaths(string workingDirectory) =>
        [
            .. RunGit("status --porcelain", workingDirectory)
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.TrimEnd('\r'))
                .Where(line => line.Length > 3)
                .Select(line => line[3..].Trim()),
        ];

    private static string RunGit(string arguments, string workingDirectory)
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = "git",
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        using Process process =
            Process.Start(startInfo) ?? throw new PerfObservationException("git could not be started.");
        // Both pipes must drain concurrently: reading them to the end one after the other
        // can deadlock when the process fills the unread pipe's buffer.
        Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
        Task<string> errorTask = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        string output = outputTask.GetAwaiter().GetResult();
        string error = errorTask.GetAwaiter().GetResult();

        if (process.ExitCode != 0)
        {
            throw new PerfObservationException($"git {arguments} failed ({process.ExitCode}): {error}");
        }

        return output;
    }
}
