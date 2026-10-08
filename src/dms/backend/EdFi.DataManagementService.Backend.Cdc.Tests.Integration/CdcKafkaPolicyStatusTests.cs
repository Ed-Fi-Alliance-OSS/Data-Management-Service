// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Confluent.Kafka;
using Confluent.Kafka.Admin;
using EdFi.DataManagementService.Backend.Ddl;
using EdFi.DataManagementService.Core.ApiSchema;
using EdFi.DataManagementService.DocumentCacheAdmin.Tests.Integration;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Cdc.Tests.Integration;

[TestFixture]
[Category(CdcControllerCategories.KafkaPolicy)]
[Category("DatabaseIntegration")]
[Category("KafkaIntegration")]
[Category("CdcAuthorizationDisabledLocal")]
[NonParallelizable]
public sealed class Given_Cdc_runbook_local_topic_policy
{
    [Test]
    public async Task It_executes_marked_topic_policy_status_with_shared_offset_drift()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var token = timeout.Token;
        string workspace = DocumentCacheAdminCliFixture.Shared.ApiSchemaDirectory;
        var manifest = ApiSchemaAssetManifestReader.ReadFromFile(
            workspace,
            Path.Combine(workspace, ApiSchemaAssetManifestReader.ManifestFileName)
        );
        await using var fixture = await CdcProviderAdmissionFixture.StartAsync(
            CdcProvider.Postgresql,
            token,
            schemaFiles: manifest.Projects.Select(p => Path.Combine(workspace, p.SchemaPath)).ToArray()
        );
        await fixture.RegisterAsync(token);
        await using var runtime = await DocumentCacheAdminCliProcessHarness.CreateAsync(
            DocumentCacheAdminCliTarget.ForManagedSource(false, fixture.ConnectionString)
        );
        var configuration = new ConfigurationBuilder().AddJsonFile(runtime.SettingsPath).Build();
        using var lifetime = (IDisposable)configuration;
        var settings = configuration
            .AsEnumerable()
            .Where(p => p.Value is not null)
            .ToDictionary(p => p.Key, p => p.Value!);
        settings["ConfigurationServiceSettings:ClientSecret"] = runtime.SecretFromEnvironment;
        string path = Path.Combine(fixture.Infrastructure.StateRoot, "kafka-status-settings.json");
        await CdcRunbookLiveCommands.WriteSettingsAsync(
            fixture,
            CdcProvider.Postgresql,
            path,
            token,
            settings
        );
        // Fixture stops after durable connector registration, before writer-publication intent.
        // Resume the original initial workflow through the exact operator retry command.
        var enabled = await CdcRunbookLiveCommands.InvokeAsync(
            "cdc-enable-retry",
            path,
            fixture.Infrastructure.StateRoot,
            token
        );
        enabled.GetProperty("exitCode").GetInt32().Should().Be(0);
        enabled
            .GetProperty("data")
            .GetProperty("workflowId")
            .GetGuid()
            .Should()
            .Be(fixture.CreationReceipt.WorkflowId);
        enabled
            .GetProperty("data")
            .GetProperty("authorizedAt")
            .GetDateTimeOffset()
            .Should()
            .BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1));
        var repeated = await CdcRunbookLiveCommands.InvokeAsync(
            "cdc-enable-retry",
            path,
            fixture.Infrastructure.StateRoot,
            token
        );
        repeated.GetProperty("exitCode").GetInt32().Should().Be(1);
        var before = await CdcRunbookLiveCommands.InvokeAsync(
            "cdc-topic-policy-inspect",
            path,
            fixture.Infrastructure.StateRoot,
            token
        );
        var target = before.GetProperty("data").GetProperty("targets")[0];
        target
            .GetProperty("status")
            .GetProperty("kafkaPolicy")
            .GetProperty("state")
            .GetString()
            .Should()
            .Be("Satisfied");
        target
            .GetProperty("status")
            .GetProperty("connectOffsetStore")
            .GetProperty("state")
            .GetString()
            .Should()
            .Be("Satisfied");
        target.GetProperty("hasSharedOffsetStoreIssue").GetBoolean().Should().BeFalse();
        before
            .GetProperty("deploymentProfile")
            .GetProperty("aclIsolationProven")
            .GetBoolean()
            .Should()
            .BeFalse();
        // A standalone status process cannot observe an in-process projector's health.
        before.GetProperty("exitCode").GetInt32().Should().Be(1);
        using var admin = new AdminClientBuilder(
            new AdminClientConfig
            {
                BootstrapServers = fixture.Infrastructure.Resources.ControllerKafkaBootstrapServers,
            }
        )
            .SetLogHandler((_, _) => { })
            .SetErrorHandler((_, _) => { })
            .Build();
        var resource = new ConfigResource
        {
            Type = ResourceType.Topic,
            Name = fixture.Request.WorkerPolicy.OffsetStorageTopic.Value,
        };
        try
        {
            await SetPolicyAsync("delete");
            var after = await CdcRunbookLiveCommands.InvokeAsync(
                "cdc-topic-policy-inspect",
                path,
                fixture.Infrastructure.StateRoot,
                token
            );
            var drift = after.GetProperty("data").GetProperty("targets")[0];
            drift
                .GetProperty("status")
                .GetProperty("connectOffsetStore")
                .GetProperty("state")
                .GetString()
                .Should()
                .Be("NotSatisfied");
            drift.GetProperty("hasSharedOffsetStoreIssue").GetBoolean().Should().BeTrue();
            after
                .GetProperty("deploymentProfile")
                .GetProperty("aclIsolationProven")
                .GetBoolean()
                .Should()
                .BeFalse();
            after.GetProperty("exitCode").GetInt32().Should().Be(1);
            var observed = CdcProviderAdmissionFixture.Observed(
                await fixture.Kafka.InspectTopicAsync(fixture.Request, resource.Name, token)
            );
            observed
                .Configuration["cleanup.policy"]
                .Value.Should()
                .Be("delete", "status must not repair drift");
        }
        finally
        {
            await SetPolicyAsync("compact");
        }

        Task SetPolicyAsync(string value) =>
            admin.IncrementalAlterConfigsAsync(
                new Dictionary<ConfigResource, List<ConfigEntry>>
                {
                    [resource] =
                    [
                        new()
                        {
                            Name = "cleanup.policy",
                            Value = value,
                            IncrementalOperation = AlterConfigOpType.Set,
                        },
                    ],
                }
            );
    }
}
