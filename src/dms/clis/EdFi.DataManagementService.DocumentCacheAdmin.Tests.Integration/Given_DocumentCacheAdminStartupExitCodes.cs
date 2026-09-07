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

    [TestCaseSource(nameof(StatusMalformedEffectiveDocumentCacheOptionCases))]
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

    [TestCaseSource(nameof(MutatingMalformedEffectiveDocumentCacheOptionCases))]
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

    [TestCaseSource(nameof(MalformedConfigurationServiceBaseUrlCases))]
    public async Task It_returns_configuration_error_for_malformed_configuration_service_base_url_before_status_execution(
        MalformedConfigurationServiceBaseUrl option,
        string expectedDiagnostic
    )
    {
        string settingsPath = CreateMalformedConfigurationServiceBaseUrlSettingsFile(option);

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

    [TestCaseSource(nameof(MalformedConfigurationServiceBaseUrlCases))]
    public async Task It_returns_configuration_error_for_malformed_configuration_service_base_url_before_mutating_execution(
        MalformedConfigurationServiceBaseUrl option,
        string expectedDiagnostic
    )
    {
        string settingsPath = CreateMalformedConfigurationServiceBaseUrlSettingsFile(option);

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

    /// <summary>
    /// The packaged CLI, invoked for the planned fence against a deployment whose effective-schema
    /// inputs will not initialize, whose Configuration Service address is unusable, whose Kafka
    /// admin-client security properties cannot build a client, and whose CDC configuration is missing
    /// everything a provisioning or observing verb reads.
    /// </summary>
    /// <remarks>
    /// The control case below proves those same settings really do refuse a sibling verb. What this
    /// asserts is that the fence is not refused with them: it reaches the control plane, reads the
    /// durable binding state store, and reports a cdc result rather than a configuration error. Which
    /// result it reports is not the point - the store this run names is empty, so there is no binding
    /// and therefore no connector to fence - the point is that the verb got as far as being able to
    /// say so.
    ///
    /// The Configuration Service and the Kafka admin client are the two whose CONSTRUCTION used to end
    /// the run: relaxing what the fence is validated against left both of them in its graph, and a
    /// dependency that throws while being built refuses a fence just as surely as a setting that fails
    /// validation.
    /// </remarks>
    [Test]
    public async Task It_reaches_the_planned_fence_though_the_schema_and_cdc_configuration_are_unusable()
    {
        string settingsPath = CreateFenceOnlySettingsFile();
        string bindingStateRoot = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}-cdc-state");
        Directory.CreateDirectory(bindingStateRoot);

        try
        {
            ProcessResult result = await RunPlannedFenceAsync(settingsPath, bindingStateRoot);

            result
                .StandardError.Should()
                .NotContain(
                    "DocumentCache configuration error",
                    "the fence reads neither the effective schema nor the configuration the other verbs are validated against"
                );
            result.ExitCode.Should().NotBe(DocumentCacheAdminExitCodes.ConfigurationError);
            result.ExitCode.Should().NotBe(DocumentCacheAdminExitCodes.UnexpectedFailure);
            JsonNode
                .Parse(result.StandardOutput)
                .Should()
                .NotBeNull("the fence reports the shared cdc result contract on stdout");
        }
        finally
        {
            TryDelete(settingsPath);
            TryDeleteDirectory(bindingStateRoot);
        }
    }

    /// <summary>
    /// The control for the case above: the same settings refuse a verb that does read the effective
    /// schema, so the fence's success there is the boundary change rather than settings that were
    /// usable all along.
    /// </summary>
    [Test]
    public async Task It_returns_configuration_error_for_a_cdc_status_on_the_same_unusable_settings()
    {
        string settingsPath = CreateFenceOnlySettingsFile();
        string bindingStateRoot = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}-cdc-state");
        Directory.CreateDirectory(bindingStateRoot);

        try
        {
            ProcessResult result = await RunDocumentCacheAdminAsync(
                DocumentCacheAdminCommandSurface.CdcCommandName,
                DocumentCacheAdminCommandSurface.CdcStatusVerbName,
                DocumentCacheAdminCommandSurface.DataStoreIdOptionName,
                "1",
                DocumentCacheAdminCommandSurface.DeploymentKeyOptionName,
                "local",
                DocumentCacheAdminCommandSurface.InstanceKeyOptionName,
                "ds1",
                DocumentCacheAdminCommandSurface.GenerationOptionName,
                "1",
                DocumentCacheAdminCommandSurface.CdcBindingStatePathOptionName,
                bindingStateRoot,
                DocumentCacheAdminCommandSurface.DatastoreOptionName,
                DocumentCacheAdminCommandSurface.PostgresqlDatastoreOptionValue,
                DocumentCacheAdminCommandSurface.SettingsOptionName,
                settingsPath,
                DocumentCacheAdminCommandSurface.JsonOptionName
            );

            result.ExitCode.Should().Be(DocumentCacheAdminExitCodes.ConfigurationError);
        }
        finally
        {
            TryDelete(settingsPath);
            TryDeleteDirectory(bindingStateRoot);
        }
    }

    private static Task<ProcessResult> RunPlannedFenceAsync(string settingsPath, string bindingStateRoot) =>
        RunDocumentCacheAdminAsync(
            DocumentCacheAdminCommandSurface.CdcCommandName,
            DocumentCacheAdminCommandSurface.CdcStopVerbName,
            DocumentCacheAdminCommandSurface.DataStoreIdOptionName,
            "1",
            DocumentCacheAdminCommandSurface.DeploymentKeyOptionName,
            "local",
            DocumentCacheAdminCommandSurface.InstanceKeyOptionName,
            "ds1",
            DocumentCacheAdminCommandSurface.GenerationOptionName,
            "1",
            DocumentCacheAdminCommandSurface.CdcBindingStatePathOptionName,
            bindingStateRoot,
            DocumentCacheAdminCommandSurface.DatastoreOptionName,
            DocumentCacheAdminCommandSurface.PostgresqlDatastoreOptionValue,
            DocumentCacheAdminCommandSurface.SettingsOptionName,
            settingsPath,
            DocumentCacheAdminCommandSurface.JsonOptionName
        );

    /// <summary>
    /// A deployment in the state a planned fence exists to survive: an API schema path that will not
    /// load, a Configuration Service address that is not a usable one, an admin-client security
    /// property no Kafka client can be built with, and a CDC section carrying only the Connect address
    /// the fence reaches the worker through and the identity that names the binding it looks up. Every
    /// setting a provisioning or observing verb reads — the principals, the Kafka bootstrap list, the
    /// record size, the offset topic — is absent.
    /// </summary>
    /// <remarks>
    /// The last two are unrelated to the fence and are the point: settings a verb never reads are one
    /// thing, and dependencies whose CONSTRUCTION fails are another. Both used to end the process
    /// before the binding store was ever opened — the configuration-service address is validated when
    /// its data-store provider is registered, and the admin client validates its own security
    /// properties when it is built.
    /// </remarks>
    private static string CreateFenceOnlySettingsFile()
    {
        JsonObject settings = CreateValidSettings();
        JsonObject appSettings = settings["AppSettings"]!.AsObject();
        JsonObject documentCacheSettings = settings["DataManagement"]!.AsObject()[
            "DocumentCache"
        ]!.AsObject();

        appSettings["UseApiSchemaPath"] = true;
        appSettings["ApiSchemaPath"] = Path.Combine(
            Path.GetTempPath(),
            $"{Guid.NewGuid():N}-no-such-api-schema"
        );
        settings["ConfigurationServiceSettings"]!.AsObject()["BaseUrl"] = string.Empty;
        documentCacheSettings["Cdc"] = new JsonObject
        {
            ["ConnectBaseUri"] = "http://127.0.0.1:1",
            ["TopicPrefix"] = "edfi.dms",
            ["KafkaAdminClientSecurityProperties"] = new JsonObject
            {
                ["security.protocol"] = "ssl",
                ["ssl.ca.location"] = Path.Combine(
                    Path.GetTempPath(),
                    $"{Guid.NewGuid():N}-no-such-certificate-authority.pem"
                ),
            },
        };

        string settingsPath = Path.Combine(
            Path.GetTempPath(),
            $"{Guid.NewGuid():N}-document-cache-admin-fence-settings.json"
        );
        File.WriteAllText(settingsPath, settings.ToJsonString());
        return settingsPath;
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort temp-directory cleanup.
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort temp-directory cleanup.
        }
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
        process.StartInfo.ArgumentList.Add("--configuration");
        process.StartInfo.ArgumentList.Add(CurrentBuildConfiguration());
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

    private static IEnumerable<TestCaseData> StatusMalformedEffectiveDocumentCacheOptionCases()
    {
        yield return new TestCaseData(
            MalformedDocumentCacheOption.Administration,
            "Administration:WorkflowTimeout must be positive"
        ).SetName("Administration timeout");
        yield return new TestCaseData(
            MalformedDocumentCacheOption.Projector,
            "Projector:PageSize must be positive"
        ).SetName("Projector page size");
    }

    private static IEnumerable<TestCaseData> MutatingMalformedEffectiveDocumentCacheOptionCases()
    {
        yield return new TestCaseData(
            MalformedDocumentCacheOption.Status,
            "Status:EndpointTimeout must be positive"
        ).SetName("Status timeout");
        yield return new TestCaseData(
            MalformedDocumentCacheOption.Projector,
            "Projector:PageSize must be positive"
        ).SetName("Projector page size");
    }

    private static IEnumerable<TestCaseData> MalformedConfigurationServiceBaseUrlCases()
    {
        yield return new TestCaseData(
            MalformedConfigurationServiceBaseUrl.Missing,
            "ConfigurationServiceSettings:BaseUrl must be an absolute HTTP or HTTPS URI."
        ).SetName("Missing");
        yield return new TestCaseData(
            MalformedConfigurationServiceBaseUrl.Empty,
            "ConfigurationServiceSettings:BaseUrl must be an absolute HTTP or HTTPS URI."
        ).SetName("Empty");
        yield return new TestCaseData(
            MalformedConfigurationServiceBaseUrl.Relative,
            "ConfigurationServiceSettings:BaseUrl must be an absolute HTTP or HTTPS URI."
        ).SetName("Relative");
        yield return new TestCaseData(
            MalformedConfigurationServiceBaseUrl.Malformed,
            "ConfigurationServiceSettings:BaseUrl must be an absolute HTTP or HTTPS URI."
        ).SetName("Malformed");
        yield return new TestCaseData(
            MalformedConfigurationServiceBaseUrl.NonHttpScheme,
            "ConfigurationServiceSettings:BaseUrl must be an absolute HTTP or HTTPS URI."
        ).SetName("Non HTTP scheme");
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

    private static string CreateMalformedConfigurationServiceBaseUrlSettingsFile(
        MalformedConfigurationServiceBaseUrl option
    )
    {
        JsonObject settings = CreateValidSettings();
        JsonObject configurationServiceSettings = settings["ConfigurationServiceSettings"]!.AsObject();

        switch (option)
        {
            case MalformedConfigurationServiceBaseUrl.Missing:
                configurationServiceSettings.Remove("BaseUrl");
                break;
            case MalformedConfigurationServiceBaseUrl.Empty:
                configurationServiceSettings["BaseUrl"] = "";
                break;
            case MalformedConfigurationServiceBaseUrl.Relative:
                configurationServiceSettings["BaseUrl"] = "/configuration-service";
                break;
            case MalformedConfigurationServiceBaseUrl.Malformed:
                configurationServiceSettings["BaseUrl"] = "http://[::1";
                break;
            case MalformedConfigurationServiceBaseUrl.NonHttpScheme:
                configurationServiceSettings["BaseUrl"] = "ftp://cms.example.org";
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(option), option, "Unknown base URL case.");
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

    private static string CurrentBuildConfiguration()
    {
        DirectoryInfo? currentDirectory = new(AppContext.BaseDirectory);

        while (currentDirectory is not null)
        {
            if (currentDirectory.Name is "Debug" or "Release")
            {
                return currentDirectory.Name;
            }

            currentDirectory = currentDirectory.Parent;
        }

        return "Debug";
    }

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

    public enum MalformedConfigurationServiceBaseUrl
    {
        Missing,
        Empty,
        Relative,
        Malformed,
        NonHttpScheme,
    }
}
