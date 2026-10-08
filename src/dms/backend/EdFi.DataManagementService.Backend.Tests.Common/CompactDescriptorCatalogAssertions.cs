// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics;

namespace EdFi.DataManagementService.Backend.Tests.Common;

/// <summary>
/// Runs the same manifest reader and renders the same SQL assets used by template source/restore
/// verification. Callers execute the result on their ordinary provider connection.
/// </summary>
public static class CompactDescriptorCatalogAssertions
{
    public const string FixtureRelativePath =
        "src/dms/backend/EdFi.DataManagementService.Backend.Ddl.Tests.Unit/Fixtures/focused/compact-descriptor";

    public static async Task<string> RenderAsync(string repositoryRoot, string dialect, string manifestPath)
    {
        ProcessStartInfo start = new("pwsh")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (
            var argument in new[]
            {
                "-NoProfile",
                "-File",
                Path.Combine(repositoryRoot, "eng/DatabaseTemplates/Assert-CompactDescriptor.ps1"),
                "-ExpectedModelManifestPath",
                manifestPath,
                "-DatabaseEngine",
                dialect == "pgsql" ? "postgresql" : "mssql",
                "-RenderSql",
            }
        )
        {
            start.ArgumentList.Add(argument);
        }
        using var process =
            Process.Start(start) ?? throw new InvalidOperationException("Unable to start pwsh.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using CancellationTokenSource timeout = new(TimeSpan.FromMinutes(1));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch
        {
            process.Kill(entireProcessTree: true);
            throw;
        }
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"Compact descriptor manifest rejected: {await error}");
        }
        return await output;
    }
}
