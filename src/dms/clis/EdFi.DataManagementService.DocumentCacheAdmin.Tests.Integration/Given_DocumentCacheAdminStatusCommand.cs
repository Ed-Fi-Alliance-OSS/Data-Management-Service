// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.DocumentCacheAdmin;
using FluentAssertions;

namespace EdFi.DataManagementService.DocumentCacheAdmin.Tests.Integration;

[TestFixture]
[Category("Status")]
public sealed class Given_DocumentCacheAdminStatusCommand
{
    [Test]
    public async Task It_returns_one_shared_status_json_document_from_the_tool_process()
    {
        ProcessResult result = await RunDocumentCacheAdminAsync(
            DocumentCacheAdminCommandSurface.StatusCommandName,
            DocumentCacheAdminCommandSurface.DataStoreIdOptionName,
            "1",
            DocumentCacheAdminCommandSurface.JsonOptionName
        );

        result.ExitCode.Should().Be(DocumentCacheAdminExitCodes.Success);
        result.StandardError.Should().BeEmpty();
        result.StandardOutput.TrimEnd().Should().NotContain("\n");

        JsonObject root = JsonNode.Parse(result.StandardOutput)!.AsObject();
        root["contractVersion"]!.GetValue<int>().Should().Be(1);
        JsonArray targets = root["targets"]!.AsArray();
        targets.Should().ContainSingle();
        targets[0]!["targetKey"]!["tenantKey"]!.GetValue<string>().Should().Be("");
        targets[0]!["targetKey"]!["dataStoreId"]!.GetValue<long>().Should().Be(1);
        targets[0]!["operationalHealth"]!["reason"]!.GetValue<string>().Should().Be("runtimeNotObserved");
    }

    private static async Task<ProcessResult> RunDocumentCacheAdminAsync(params string[] arguments)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = RepositoryRoot(),
        };
        process.StartInfo.ArgumentList.Add("run");
        process.StartInfo.ArgumentList.Add("--project");
        process.StartInfo.ArgumentList.Add(ToolProjectPath());
        process.StartInfo.ArgumentList.Add("--no-build");
        process.StartInfo.ArgumentList.Add("--");
        foreach (string argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        process.StartInfo.Environment["AppSettings__Datastore"] = "";

        process.Start();
        Task<string> standardOutput = process.StandardOutput.ReadToEndAsync();
        Task<string> standardError = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));

        return new ProcessResult(process.ExitCode, await standardOutput, await standardError);
    }

    private static string ToolProjectPath() =>
        Path.Combine(
            RepositoryRoot(),
            "src",
            "dms",
            "clis",
            "EdFi.DataManagementService.DocumentCacheAdmin",
            "EdFi.DataManagementService.DocumentCacheAdmin.csproj"
        );

    private static string RepositoryRoot()
    {
        DirectoryInfo? currentDirectory = new(AppContext.BaseDirectory);

        while (currentDirectory is not null)
        {
            string solutionPath = Path.Combine(
                currentDirectory.FullName,
                "src",
                "dms",
                "EdFi.DataManagementService.sln"
            );

            if (File.Exists(solutionPath))
            {
                return currentDirectory.FullName;
            }

            currentDirectory = currentDirectory.Parent;
        }

        throw new DirectoryNotFoundException("Unable to locate the repository root.");
    }

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);
}
