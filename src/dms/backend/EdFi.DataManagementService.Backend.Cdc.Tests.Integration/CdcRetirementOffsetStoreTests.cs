// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using Confluent.Kafka;
using EdFi.DataManagementService.Backend.Ddl;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Npgsql;
using NUnit.Framework;
using static EdFi.DataManagementService.Backend.Cdc.Tests.Integration.CdcProviderAdmissionFixture;
using CoreCdc = EdFi.DataManagementService.Core.DocumentCache.Cdc;

namespace EdFi.DataManagementService.Backend.Cdc.Tests.Integration;

[TestFixture(CdcProvider.Postgresql, Category = "PostgresqlIntegration")]
[TestFixture(CdcProvider.SqlServer, Category = "MssqlIntegration")]
[Category(CdcControllerCategories.ManagedLifecycle)]
[Category("DatabaseIntegration")]
[Category("CdcAuthorizationDisabledLocal")]
[NonParallelizable]
public sealed class Given_Cdc_Retirement_Offset_Store(CdcProvider provider)
{
    [TestCase(false)]
    [TestCase(true)]
    public async Task It_cleans_offline_initial_failure_through_the_production_wrapper_command(bool reserved)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(8));
        var token = timeout.Token;
        await using var fixture = await CdcProviderAdmissionFixture.StartAsync(
            provider,
            token,
            composeKafka: true,
            offlineKafka: true
        );
        var resources = fixture.Infrastructure.Resources;
        var request = fixture.Request;
        var docker = new DockerCli();
        (
            await docker.RunAsync(
                [
                    "ps",
                    "-a",
                    "--filter",
                    "label=com.docker.compose.project=" + resources.ControllerProject,
                    "-q",
                ],
                token
            )
        )
            .StandardOutput.Trim()
            .Should()
            .BeEmpty();
        var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (!File.Exists(Path.Combine(directory.FullName, "eng", "docker-compose", "cdc-lifecycle.psm1")))
        {
            directory = directory.Parent!;
        }
        string repository = directory.FullName;
        string settingsPath = Path.Combine(fixture.Infrastructure.StateRoot, "cleanup-settings.json");
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
            ["Cdc:Worker:Principal"] = "worker",
            ["Cdc:Worker:ConnectorPrincipal"] = "connector",
            ["Cdc:Worker:AdministratorPrincipal"] = "administrator",
            ["Cdc:SetupConnectionString"] = fixture.ConnectionString,
            ["Cdc:SetupPrincipal"] = provider == CdcProvider.Postgresql ? "postgres" : "sa",
            ["Cdc:DatabaseConnectorPrincipal"] = "dms_connector",
            ["Cdc:Schemas:0"] = Path.Combine(
                TestContext.CurrentContext.TestDirectory,
                "Fixtures",
                "minimal-api-schema.json"
            ),
            ["Cdc:Timing:CallMilliseconds"] = "60000",
            ["Cdc:Timing:WaitMilliseconds"] = "240000",
            ["Cdc:Timing:PollMilliseconds"] = "250",
        };
        foreach (var item in request.ProviderConnectionProperties.Properties)
        {
            settings["Cdc:ProviderConnectionProperties:" + item.Key] = item.Value;
        }
        await File.WriteAllTextAsync(settingsPath, JsonSerializer.Serialize(settings), token);
        string bridge = Path.Combine(fixture.Infrastructure.StateRoot, "cleanup-bridge.json");
        await File.WriteAllTextAsync(
            bridge,
            JsonSerializer.Serialize(
                new
                {
                    Receipt = fixture.CreationReceipt,
                    Live = true,
                    Cleanup = true,
                    Flavor = "local",
                    Project = resources.ControllerProject,
                    SettingsPath = settingsPath,
                    StateRoot = fixture.Infrastructure.StateRoot,
                    Binding = request.Binding,
                    Provider = provider == CdcProvider.Postgresql ? "postgresql" : "mssql",
                    IdentityProvider = "self-contained",
                    WorkerKey = request.WorkerPolicy.WorkerKey.Value,
                    OffsetTopic = request.WorkerPolicy.OffsetStorageTopic.Value,
                    ToolPath = Path.Combine(
                        repository,
                        "src/dms/clis/EdFi.DataManagementService.SchemaTools/bin",
                        Directory.GetParent(TestContext.CurrentContext.TestDirectory)!.Name,
                        "net10.0/api-schema-tools"
                    ),
                    ComposeFile = resources.ControllerComposeFile,
                    EnvironmentFile = resources.ControllerComposeEnvironment,
                    ProviderContainer = resources.ProviderContainerName,
                    ConnectEndpoint = request.ConnectEndpoint.AbsoluteUri,
                }
            ),
            token
        );
        var info = new System.Diagnostics.ProcessStartInfo("pwsh")
        {
            WorkingDirectory = repository,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        info.Environment["DMS_T57_BRIDGE"] = bridge;
        info.ArgumentList.Add("-NoProfile");
        info.ArgumentList.Add("-Command");
        info.ArgumentList.Add(
            "$r = Invoke-Pester -Path eng/docker-compose/tests/CdcLifecycleOrdering.Tests.ps1 -FullName '*production controller session bridge*' -Output Detailed -PassThru; if ($r.PassedCount -ne 1 -or $r.FailedCount -ne 0) { exit 1 }"
        );
        using var process = System.Diagnostics.Process.Start(info)!;
        var output = process.StandardOutput.ReadToEndAsync(token);
        var error = process.StandardError.ReadToEndAsync(token);
        try
        {
            bool handoffObserved = false;
            while (!process.HasExited)
            {
                if (!File.Exists(bridge + ".request"))
                {
                    await Task.Delay(10, token);
                    continue;
                }
                File.Delete(bridge + ".request");
                handoffObserved.Should().BeFalse();
                handoffObserved = true;
                if (reserved)
                {
                    Observed(
                        await fixture.Controllers.Activation.ActivateAsync(request, fixture.Runtime, token)
                    );
                    bool interrupted = false;
                    fixture.Hooks.OnBoundary = e =>
                    {
                        if (
                            !interrupted
                            && e.Boundary == CdcControllerBoundary.ProviderProof
                            && e.Edge == CdcControllerEdge.After
                        )
                        {
                            interrupted = true;
                            throw new IOException("lost provider setup response");
                        }
                    };
                    (await fixture.Controllers.ProviderSetup.SetupAsync(request, fixture.Runtime, token))
                        .State.Should()
                        .Be(CdcTransportEvidenceState.Unavailable);
                    interrupted.Should().BeTrue();
                    fixture.Hooks.OnBoundary = _ => { };
                }
                (
                    await docker.RunAsync(
                        [
                            "ps",
                            "-a",
                            "--filter",
                            "label=com.docker.compose.project=" + resources.ControllerProject,
                            "-q",
                        ],
                        token
                    )
                )
                    .StandardOutput.Trim()
                    .Should()
                    .BeEmpty();
                await File.WriteAllTextAsync(bridge + ".response", "prepared", token);
            }
            await process.WaitForExitAsync(token);
            handoffObserved.Should().BeTrue();
            process.ExitCode.Should().Be(0, await output + await error);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(true);
                await process.WaitForExitAsync(CancellationToken.None);
            }
        }
        (await fixture.Infrastructure.Bindings.ExactMatchBindingAsync(request.Binding, token))
            .Status.Should()
            .Be(CoreCdc.CdcControlPlaneOperationStatus.BindingMissing);
        await using var session = await fixture
            .Infrastructure.CreateJournalStore()
            .AcquireAsync(TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(10), token);
        var journal = await session.ReadAsync(request.TargetIdentity, token);
        journal.Operations.Last().Effect.Should().Be(CdcWorkflowEffect.Retire);
        journal.Operations.Last().Completions.Should().ContainSingle();
        journal
            .Operations.Should()
            .NotContain(o =>
                o.Effect == CdcWorkflowEffect.RegisterConnector
                || o.Effect == CdcWorkflowEffect.AuthorizeWriterPublication
                || o.Effect == CdcWorkflowEffect.ResumeConnector
            );
        var history = await session.ReadSourcePublicationHistoryAsync(
            request.TargetIdentity,
            request.Binding.PhysicalSourceFingerprint,
            token
        );
        history
            .Transitions.Last()
            .Status.Should()
            .Be(
                reserved
                    ? EdFi.DataManagementService
                        .Core
                        .DocumentCache
                        .DocumentCacheDownstreamPublicationStatus
                        .Historical
                    : EdFi.DataManagementService
                        .Core
                        .DocumentCache
                        .DocumentCacheDownstreamPublicationStatus
                        .InternalOnly
            );
    }

    [Test]
    public async Task It_retires_after_provider_creation_before_registration_without_changing_shared_storage()
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromMinutes(8));
        var token = timeout.Token;
        await using var fixture = await CdcProviderAdmissionFixture.StartAsync(provider, token);
        Observed(await fixture.Controllers.Activation.ActivateAsync(fixture.Request, fixture.Runtime, token));
        Observed(await fixture.Controllers.ProviderSetup.SetupAsync(fixture.Request, fixture.Runtime, token));
        var config = Client(fixture);
        var request = fixture.Request;
        string topic = request.WorkerPolicy.OffsetStorageTopic.Value;
        await CdcRetirementOffsetStoreProbe.WriteAsync(
            config,
            topic,
            "[\"unrelated-connector\",{}]",
            "{}",
            token
        );
        var before = await CdcRetirementOffsetStoreProbe.ReadAsync(config, topic, token);
        before.Should().ContainSingle();
        (await fixture.Infrastructure.Connect.ReadConfigurationAsync(request, token))
            .State.Should()
            .Be(CdcTransportEvidenceState.Absent);
        (await fixture.Infrastructure.Connect.ReadOffsetEvidenceAsync(request, token))
            .State.Should()
            .Be(CdcTransportEvidenceState.Unavailable);
        var result = await Retirement(fixture).RetireAsync(request, request.Binding.Generation, true, token);
        result.Succeeded.Should().BeTrue(JsonSerializer.Serialize(result.Diagnostics));
        (await CdcRetirementOffsetStoreProbe.ReadAsync(config, topic, token)).Should().BeEquivalentTo(before);
        (await fixture.Infrastructure.Bindings.ExactMatchBindingAsync(request.Binding, token))
            .Status.Should()
            .Be(CoreCdc.CdcControlPlaneOperationStatus.BindingMissing);
        (await fixture.Infrastructure.Connect.ReadConfigurationAsync(request, token))
            .State.Should()
            .Be(CdcTransportEvidenceState.Absent);
    }

    [Test]
    public async Task It_rejects_actual_pinned_worker_offsets_until_their_exact_keys_are_tombstoned()
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromMinutes(8));
        var token = timeout.Token;
        await using var fixture = await CdcProviderAdmissionFixture.StartAsync(provider, token);
        await fixture.RegisterAsync(token);
        var request = fixture.Request;
        Observed(await fixture.Infrastructure.Connect.StopAsync(request, token));
        (await fixture.Infrastructure.Connect.DeleteAsync(request, token))
            .State.Should()
            .Be(CdcTransportEvidenceState.Absent);
        var config = Client(fixture);
        string topic = request.WorkerPolicy.OffsetStorageTopic.Value;
        var actual = await CdcRetirementOffsetStoreProbe.ReadAsync(config, topic, token);
        var keys = actual
            .Where(record => record.Value is not null)
            .Select(record => record.Key)
            .Distinct()
            .ToArray();
        keys.Should().NotBeEmpty();
        foreach (string key in keys)
        {
            using var parsed = JsonDocument.Parse(key);
            (
                parsed.RootElement.ValueKind == JsonValueKind.Array
                && parsed.RootElement.GetArrayLength() == 2
                && parsed.RootElement[0].GetString() == request.Binding.ConnectorName
                && parsed.RootElement[1].ValueKind == JsonValueKind.Object
            )
                .Should()
                .BeTrue("the qualified worker must emit the supported namespace key format");
        }
        var retirement = Retirement(fixture);
        var rejected = await retirement.RetireAsync(request, request.Binding.Generation, true, token);
        rejected.Succeeded.Should().BeFalse();
        (await fixture.Infrastructure.Bindings.ExactMatchBindingAsync(request.Binding, token))
            .Status.Should()
            .Be(CoreCdc.CdcControlPlaneOperationStatus.Succeeded);
        (await CdcRetirementOffsetStoreProbe.ReadAsync(config, topic, token)).Should().BeEquivalentTo(actual);
        foreach (string key in keys)
        {
            await CdcRetirementOffsetStoreProbe.WriteAsync(config, topic, key, null!, token);
        }
        var tombstoned = await CdcRetirementOffsetStoreProbe.ReadAsync(config, topic, token);
        var result = await retirement.RetireAsync(request, request.Binding.Generation, true, token);
        result.Succeeded.Should().BeTrue(JsonSerializer.Serialize(result.Diagnostics));
        (await CdcRetirementOffsetStoreProbe.ReadAsync(config, topic, token))
            .Should()
            .BeEquivalentTo(tombstoned);
    }

    private CdcBindingRetirement Retirement(CdcProviderAdmissionFixture fixture) =>
        fixture.Controllers.Retirement(
            fixture.Kafka,
            new CdcProviderArtifactCleanupAdapter(
                provider == CdcProvider.Postgresql ? NpgsqlFactory.Instance : SqlClientFactory.Instance,
                fixture.ConnectionString
            )
        );

    private static ClientConfig Client(CdcProviderAdmissionFixture fixture) =>
        new()
        {
            BootstrapServers = fixture.Infrastructure.Resources.ControllerKafkaBootstrapServers,
            AllowAutoCreateTopics = false,
        };
}

/// <summary>Private fixture-only storage access. Raw keys never enter qualification attachments.</summary>
internal static class CdcRetirementOffsetStoreProbe
{
    internal static async Task WriteAsync(
        ClientConfig client,
        string topic,
        string key,
        string value,
        CancellationToken token
    )
    {
        using var producer = new ProducerBuilder<string, string>(
            new ProducerConfig(client) { Acks = Acks.All }
        )
            .SetLogHandler((_, _) => { })
            .SetErrorHandler((_, _) => { })
            .Build();
        await producer.ProduceAsync(topic, new() { Key = key, Value = value }, token);
    }

    internal sealed record Record(int Partition, long Offset, string Key, string Value);

    internal static Task<Record[]> ReadAsync(ClientConfig client, string topic, CancellationToken token) =>
        Task.Run(
            () =>
            {
                using var admin = new AdminClientBuilder(new AdminClientConfig(client))
                    .SetLogHandler((_, _) => { })
                    .SetErrorHandler((_, _) => { })
                    .Build();
                using var consumer = new ConsumerBuilder<string, string>(
                    new ConsumerConfig(client)
                    {
                        GroupId = "retirement-fixture",
                        EnableAutoCommit = false,
                        EnableAutoOffsetStore = false,
                        EnablePartitionEof = true,
                        AutoOffsetReset = AutoOffsetReset.Error,
                    }
                ).SetLogHandler((_, _) => { }).SetErrorHandler((_, _) => { }).Build();
                var partitions = admin
                    .GetMetadata(topic, TimeSpan.FromSeconds(10))
                    .Topics.Single()
                    .Partitions.Select(p => new TopicPartition(topic, p.PartitionId))
                    .ToArray();
                var bounds = partitions.ToDictionary(
                    p => p,
                    p => consumer.QueryWatermarkOffsets(p, TimeSpan.FromSeconds(10))
                );
                consumer.Assign(bounds.Select(p => new TopicPartitionOffset(p.Key, p.Value.Low)));
                HashSet<TopicPartition> pending = [.. partitions];
                List<Record> records = [];
                while (pending.Count > 0)
                {
                    var record = consumer.Consume(token);
                    if (record.Offset >= bounds[record.TopicPartition].High)
                    {
                        pending.Remove(record.TopicPartition);
                        consumer.Pause([record.TopicPartition]);
                    }
                    else if (!record.IsPartitionEOF)
                    {
                        records.Add(
                            new(
                                record.Partition.Value,
                                record.Offset.Value,
                                record.Message.Key,
                                record.Message.Value
                            )
                        );
                    }
                }
                return records.ToArray();
            },
            token
        );
}
