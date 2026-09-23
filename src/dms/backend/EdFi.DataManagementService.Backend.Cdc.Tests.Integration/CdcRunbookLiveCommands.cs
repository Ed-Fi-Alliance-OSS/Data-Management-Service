// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Diagnostics;
using System.Text.Json;
using EdFi.DataManagementService.Backend.Ddl;
using EdFi.DataManagementService.SchemaTools.Tests.Unit;
using FluentAssertions;

namespace EdFi.DataManagementService.Backend.Cdc.Tests.Integration;

/// <summary>Packaged commands over the existing fixture-owned services and original provenance.</summary>
internal static class CdcRunbookLiveCommands
{
    internal static async Task WriteSettingsAsync(
        CdcProviderAdmissionFixture fixture,
        CdcProvider provider,
        string settingsPath,
        CancellationToken token,
        IReadOnlyDictionary<string, string> runtimeSettings
    )
    {
        var request = fixture.Request;
        var resources = fixture.Infrastructure.Resources;
        var settings = new Dictionary<string, string>
        {
            ["AppSettings:Datastore"] = provider == CdcProvider.Postgresql ? "postgresql" : "mssql",
            ["Cdc:Provider"] = provider == CdcProvider.Postgresql ? "postgresql" : "sqlserver",
            ["DataManagement:DocumentCache:Targets:0:DataStoreId"] = request.Binding.DataStoreId,
            ["Cdc:DeploymentKey"] = request.Binding.DeploymentKey,
            ["Cdc:InstanceKey"] = request.Binding.InstanceKey,
            ["Cdc:DataStoreId"] = request.Binding.DataStoreId,
            ["Cdc:Generation"] = request.Binding.Generation.ToString(),
            ["Cdc:TopicPrefix"] = "edfi.documents",
            ["Cdc:PartitionCount"] = request.Binding.PartitionCount.ToString(),
            ["Cdc:MaxRecordBytes"] = "1000000",
            ["Cdc:KafkaBootstrapServers"] = request.ConnectorPolicy.KafkaBootstrapServers,
            ["Cdc:KafkaAdminBootstrapServers"] = resources.ControllerKafkaBootstrapServers,
            ["Cdc:Compose:Project"] = resources.ControllerProject,
            ["Cdc:Compose:File"] = resources.ControllerComposeFile,
            ["Cdc:Compose:EnvironmentFile"] = resources.ControllerComposeEnvironment,
            ["Cdc:Compose:BrokerSizeOverrideFile"] = resources.ControllerSizeOverride,
            ["Cdc:ConnectEndpoint"] = request.ConnectEndpoint.AbsoluteUri,
            ["Cdc:WorkerMetricsEndpoint"] = request.WorkerMetricsEndpoint.AbsoluteUri,
            ["Cdc:LagThresholdMilliseconds"] = "1000",
            ["Cdc:DurabilityProfile"] = "LocalSingleBroker",
            ["Cdc:AuthorizationProfile"] = "AuthorizationDisabledLocal",
            ["Cdc:Worker:Key"] = request.WorkerPolicy.WorkerKey.Value,
            ["Cdc:Worker:OffsetStorageTopic"] = request.WorkerPolicy.OffsetStorageTopic.Value,
            ["Cdc:Worker:HeapBytes"] = "1073741824",
            ["Cdc:Worker:Principal"] = request.WorkerPolicy.WorkerPrincipal.Value,
            ["Cdc:Worker:ConnectorPrincipal"] = request.WorkerPolicy.ConnectorPrincipal.Value,
            ["Cdc:Worker:AdministratorPrincipal"] = request
                .WorkerPolicy
                .DeploymentAdministratorPrincipal
                .Value,
            ["Cdc:SetupConnectionString"] = fixture.ConnectionString,
            ["Cdc:SetupPrincipal"] = provider == CdcProvider.Postgresql ? "postgres" : "sa",
            ["Cdc:DatabaseConnectorPrincipal"] = "dms_connector",
            ["Cdc:Timing:CallMilliseconds"] = "60000",
            ["Cdc:Timing:WaitMilliseconds"] = "240000",
            ["Cdc:Timing:PollMilliseconds"] = "250",
        };
        foreach (var item in runtimeSettings)
        {
            settings[item.Key] = item.Value;
        }
        for (int i = 0; i < fixture.SchemaFiles.Count; i++)
        {
            settings[$"Cdc:Schemas:{i}"] = fixture.SchemaFiles[i];
        }
        for (int i = 0; i < request.WorkerPolicy.Consumers.Count; i++)
        {
            settings[$"Cdc:Consumers:{i}:Principal"] = request.WorkerPolicy.Consumers[i].Principal.Value;
            settings[$"Cdc:Consumers:{i}:Group"] = request.WorkerPolicy.Consumers[i].Group.Value;
        }
        foreach (var item in request.ProviderConnectionProperties.Properties)
        {
            settings["Cdc:ProviderConnectionProperties:" + item.Key] = item.Value;
        }
        settings["Cdc:Timing:MaximumObservationAgeMilliseconds"] = "60000";
        settings["Cdc:SqlServerPollMilliseconds"] = "1000";
        await File.WriteAllTextAsync(settingsPath, JsonSerializer.Serialize(settings), token);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(settingsPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    internal static async Task<JsonElement> InvokeAsync(
        string id,
        string settingsPath,
        string statePath,
        CancellationToken token
    )
    {
        string repository = CdcRunbookExamples.RepositoryRoot;
        string configuration = Directory.GetParent(TestContext.CurrentContext.TestDirectory)!.Name;
        var start = new ProcessStartInfo(
            Path.Combine(
                repository,
                "src/dms/clis/EdFi.DataManagementService.SchemaTools/bin",
                configuration,
                "net10.0/api-schema-tools"
            )
        )
        {
            WorkingDirectory = repository,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (
            string key in start
                .Environment.Keys.Where(k => k.StartsWith("DMS_CDC__", StringComparison.OrdinalIgnoreCase))
                .ToArray()
        )
        {
            start.Environment.Remove(key);
        }
        foreach (
            string argument in CdcRunbookArguments.Parse(
                CdcRunbookExamples.Read(id),
                new Dictionary<string, string>
                {
                    ["<retained-settings-path>"] = settingsPath,
                    ["<original-state-root>"] = statePath,
                }
            )
        )
        {
            start.ArgumentList.Add(argument);
        }
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(token);
        var error = process.StandardError.ReadToEndAsync(token);
        try
        {
            await process.WaitForExitAsync(token);
            string stdout = await output;
            string stderr = await error;
            stdout.Should().NotContain("t33-private-sentinel");
            stderr.Should().NotContain("t33-private-sentinel");
            using var json = JsonDocument.Parse(stdout);
            var result = json.RootElement.Clone();
            string artifact = Path.Combine(
                TestContext.CurrentContext.WorkDirectory,
                "native-recovery-command-" + Guid.NewGuid().ToString("N") + ".json"
            );
            var lines = stderr
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.TrimEnd('\r'))
                .ToArray();
            var passes = lines
                .Where(line => line.StartsWith('{'))
                .Select(line => JsonSerializer.Deserialize<JsonElement>(line))
                .ToArray();
            await File.WriteAllTextAsync(
                artifact,
                JsonSerializer.Serialize(
                    new
                    {
                        Test = TestContext.CurrentContext.Test.Name,
                        SnippetId = id,
                        ExitCode = process.ExitCode,
                        Result = result,
                        Passes = passes,
                    }
                ),
                token
            );
            TestContext.AddTestAttachment(
                artifact,
                "Packaged marked command with bounded watch passes; no interval certification"
            );
            process.ExitCode.Should().Be(result.GetProperty("exitCode").GetInt32());
            result.GetProperty("operation").GetString().Should().Be(start.ArgumentList[1]);
            lines
                .Where(line => !line.StartsWith('{'))
                .Should()
                .Equal(
                    result
                        .GetProperty("diagnostics")
                        .EnumerateArray()
                        .Select(d =>
                            $"{d.GetProperty("component").GetString()}: {d.GetProperty("message").GetString()}"
                        )
                );
            if (start.ArgumentList[1] == "watch")
            {
                passes.Should().HaveCount(3);
                foreach (var pass in passes)
                {
                    pass.GetProperty("targets").GetArrayLength().Should().Be(1);
                }
                JsonElement.DeepEquals(result.GetProperty("data"), passes[^1]).Should().BeTrue();
            }
            else
            {
                passes.Should().BeEmpty();
            }
            return result;
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }
        }
    }
}
