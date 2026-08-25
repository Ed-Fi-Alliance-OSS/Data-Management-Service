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
[Category("ExitCode")]
public sealed class Given_DocumentCacheAdminStartupExitCodes
{
    [Test]
    public async Task It_returns_configuration_error_when_settings_file_cannot_be_loaded()
    {
        string missingSettingsPath = Path.Combine(
            Path.GetTempPath(),
            $"{Guid.NewGuid():N}-missing-document-cache-admin-settings.json"
        );

        ProcessResult result = await RunDocumentCacheAdminAsync(
            DocumentCacheAdminCommandSurface.StatusCommandName,
            DocumentCacheAdminCommandSurface.DataStoreIdOptionName,
            "1",
            DocumentCacheAdminCommandSurface.DatastoreOptionName,
            DocumentCacheAdminCommandSurface.PostgresqlDatastoreOptionValue,
            DocumentCacheAdminCommandSurface.SettingsOptionName,
            missingSettingsPath,
            DocumentCacheAdminCommandSurface.JsonOptionName
        );

        result.ExitCode.Should().Be(DocumentCacheAdminExitCodes.ConfigurationError);
        result.StandardOutput.Should().BeEmpty();
        result.StandardError.Should().Contain("DocumentCache configuration error");
    }

    [TestCaseSource(nameof(MalformedDocumentCacheOptionCases))]
    public async Task It_returns_configuration_error_for_malformed_document_cache_options_before_status_execution(
        MalformedDocumentCacheOption option,
        string expectedDiagnostic
    )
    {
        string settingsPath = CreateMalformedSettingsFile(option);

        try
        {
            ProcessResult result = await RunDocumentCacheAdminAsync(
                DocumentCacheAdminCommandSurface.StatusCommandName,
                DocumentCacheAdminCommandSurface.DataStoreIdOptionName,
                "1",
                DocumentCacheAdminCommandSurface.DatastoreOptionName,
                DocumentCacheAdminCommandSurface.PostgresqlDatastoreOptionValue,
                DocumentCacheAdminCommandSurface.SettingsOptionName,
                settingsPath,
                DocumentCacheAdminCommandSurface.JsonOptionName
            );

            AssertConfigurationError(result, expectedDiagnostic);
        }
        finally
        {
            TryDelete(settingsPath);
        }
    }

    [TestCaseSource(nameof(MalformedDocumentCacheOptionCases))]
    public async Task It_returns_configuration_error_for_malformed_document_cache_options_before_mutating_execution(
        MalformedDocumentCacheOption option,
        string expectedDiagnostic
    )
    {
        string settingsPath = CreateMalformedSettingsFile(option);

        try
        {
            ProcessResult result = await RunDocumentCacheAdminAsync(
                DocumentCacheAdminCommandSurface.RebuildOnlineCommandName,
                DocumentCacheAdminCommandSurface.DataStoreIdOptionName,
                "1",
                DocumentCacheAdminCommandSurface.ConfirmOptionName,
                "onlineCacheRebuild",
                DocumentCacheAdminCommandSurface.DatastoreOptionName,
                DocumentCacheAdminCommandSurface.PostgresqlDatastoreOptionValue,
                DocumentCacheAdminCommandSurface.SettingsOptionName,
                settingsPath,
                DocumentCacheAdminCommandSurface.JsonOptionName
            );

            AssertConfigurationError(result, expectedDiagnostic);
        }
        finally
        {
            TryDelete(settingsPath);
        }
    }

    [Test]
    public async Task It_keeps_stdout_empty_for_json_argument_errors()
    {
        ProcessResult result = await RunDocumentCacheAdminAsync(
            DocumentCacheAdminCommandSurface.StatusCommandName,
            DocumentCacheAdminCommandSurface.DataStoreIdOptionName,
            "0",
            DocumentCacheAdminCommandSurface.JsonOptionName
        );

        result.ExitCode.Should().Be(DocumentCacheAdminExitCodes.ArgumentError);
        result.StandardOutput.Should().BeEmpty();
        result.StandardError.Should().Contain(DocumentCacheAdminCommandSurface.DataStoreIdOptionName);
    }

    [Test]
    public async Task It_keeps_stdout_empty_for_json_request_input_loading_failures()
    {
        string missingRequestPath = Path.Combine(
            Path.GetTempPath(),
            $"{Guid.NewGuid():N}-missing-document-cache-admin-request.json"
        );

        ProcessResult result = await RunDocumentCacheAdminAsync(
            DocumentCacheAdminCommandSurface.StatusCommandName,
            DocumentCacheAdminCommandSurface.RequestJsonOptionName,
            missingRequestPath,
            DocumentCacheAdminCommandSurface.JsonOptionName
        );

        result.ExitCode.Should().Be(DocumentCacheAdminExitCodes.ArgumentError);
        result.StandardOutput.Should().BeEmpty();
        result
            .StandardError.Should()
            .Contain($"Unable to read {DocumentCacheAdminCommandSurface.RequestJsonOptionName} input");
        result.StandardError.Should().NotContain("Unexpected DocumentCache administration CLI failure");
    }

    [Test]
    public async Task It_returns_argument_error_with_usage_when_human_command_line_is_invalid()
    {
        ProcessResult result = await RunDocumentCacheAdminAsync(
            DocumentCacheAdminCommandSurface.StatusCommandName,
            DocumentCacheAdminCommandSurface.DataStoreIdOptionName,
            "0"
        );

        result.ExitCode.Should().Be(DocumentCacheAdminExitCodes.ArgumentError);
        result.StandardOutput.Should().Contain("Usage:");
        result.StandardError.Should().Contain(DocumentCacheAdminCommandSurface.DataStoreIdOptionName);
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

    private static IEnumerable<TestCaseData> MalformedDocumentCacheOptionCases()
    {
        yield return new TestCaseData(
            MalformedDocumentCacheOption.Status,
            "Status:EndpointTimeout must be positive"
        ).SetName("Status timeout");
        yield return new TestCaseData(
            MalformedDocumentCacheOption.Administration,
            "Administration:WorkflowTimeout must be positive"
        ).SetName("Administration timeout");
        yield return new TestCaseData(
            MalformedDocumentCacheOption.Projector,
            "Projector:PageSize must be positive"
        ).SetName("Projector page size");
        yield return new TestCaseData(
            MalformedDocumentCacheOption.Target,
            "Targets0 DataStoreId must be positive"
        ).SetName("Configured target");
    }

    private static string CreateMalformedSettingsFile(MalformedDocumentCacheOption option)
    {
        JsonObject settings = CreateValidSettings();
        JsonObject documentCacheSettings = settings["DataManagement"]!.AsObject()[
            "DocumentCache"
        ]!.AsObject();

        switch (option)
        {
            case MalformedDocumentCacheOption.Status:
                documentCacheSettings["Status"]!.AsObject()["EndpointTimeout"] = "00:00:00";
                break;
            case MalformedDocumentCacheOption.Administration:
                documentCacheSettings["Administration"]!.AsObject()["WorkflowTimeout"] = "00:00:00";
                break;
            case MalformedDocumentCacheOption.Projector:
                documentCacheSettings["Projector"]!.AsObject()["PageSize"] = 0;
                break;
            case MalformedDocumentCacheOption.Target:
                documentCacheSettings["Targets"] = new JsonArray(
                    new JsonObject { ["TenantKey"] = "", ["DataStoreId"] = 0 }
                );
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(option), option, "Unknown option case.");
        }

        string settingsPath = Path.Combine(
            Path.GetTempPath(),
            $"{Guid.NewGuid():N}-document-cache-admin-settings.json"
        );
        File.WriteAllText(settingsPath, settings.ToJsonString());
        return settingsPath;
    }

    private static JsonObject CreateValidSettings() =>
        new()
        {
            ["AppSettings"] = new JsonObject
            {
                ["Datastore"] = DocumentCacheAdminCommandSurface.PostgresqlDatastoreOptionValue,
                ["DefaultPartitionCount"] = 10,
                ["UseApiSchemaPath"] = false,
            },
            ["ConfigurationServiceSettings"] = new JsonObject
            {
                ["BaseUrl"] = "https://cms.example.org",
                ["ClientId"] = "document-cache-admin-startup-test",
                ["Scope"] = "edfi_admin_api/full_access",
                ["EncryptionKey"] = "TestEncryptionKey123456789012345678901234567890",
            },
            ["DataManagement"] = new JsonObject
            {
                ["DocumentCache"] = new JsonObject
                {
                    ["ReadAcceleration"] = new JsonObject
                    {
                        ["Enabled"] = false,
                        ["DirectFillTimeout"] = "00:00:00.250",
                    },
                    ["Projector"] = new JsonObject
                    {
                        ["PollInterval"] = "00:00:05",
                        ["PageSize"] = 100,
                        ["MaxConcurrentTargets"] = 1,
                        ["FailureBackoff"] = "00:00:05",
                        ["BaselineHighWaterMark"] = 100,
                    },
                    ["Administration"] = new JsonObject { ["WorkflowTimeout"] = "00:05:00" },
                    ["Status"] = new JsonObject
                    {
                        ["StatusObservationTimeout"] = "00:00:01",
                        ["EndpointTimeout"] = "00:00:05",
                    },
                },
            },
        };

    private static void AssertConfigurationError(ProcessResult result, string expectedDiagnostic)
    {
        result.ExitCode.Should().Be(DocumentCacheAdminExitCodes.ConfigurationError);
        result.StandardOutput.Should().BeEmpty();
        result.StandardError.Should().Contain("DocumentCache configuration error");
        result.StandardError.Should().Contain(expectedDiagnostic);
        result.StandardError.Should().NotContain("Unexpected DocumentCache administration CLI failure");
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Best-effort temp-file cleanup.
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort temp-file cleanup.
        }
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

    public enum MalformedDocumentCacheOption
    {
        Status,
        Administration,
        Projector,
        Target,
    }
}
