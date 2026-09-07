// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using EdFi.DataManagementService.Core.DocumentCache.Cdc;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Cdc.Control.Tests.Integration;

[TestFixture("registered")]
[TestFixture("neverRegistered")]
[TestFixture("providerTimeout")]
[NonParallelizable]
[Category("CdcRetirementOperator")]
public sealed class Given_CdcControlRetirementOperator(string scenario)
{
    [Test]
    public async Task It_preserves_retry_authority_until_governed_cleanup_is_proved()
    {
        using CancellationTokenSource cancellation = new(TimeSpan.FromMinutes(10));
        CancellationToken token = cancellation.Token;
        await using CdcControlBrokerFixture fixture = await CdcControlBrokerFixture.StartAsync(token);
        string stateRoot = Path.Combine(Path.GetTempPath(), $"cdc-retirement-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stateRoot);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                stateRoot,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            );
        }
        try
        {
            await fixture.GrantConnectWorkerOffsetStoreAclsAsync(token);
            await fixture.SetTopicConfigAsync(fixture.OffsetStoreTopicName, "min.insync.replicas", "1");
            (
                await fixture.KafkaAdmin.EnsureBindingKafkaPolicyAsync(
                    fixture.ObservationContext,
                    fixture.Inventory,
                    CdcControlBrokerFixture.BindingPartitionCount,
                    token
                )
            )
                .PolicyState.Should()
                .Be(CdcKafkaPolicyState.Satisfied);
            CdcConnectorTemplateResult rendered = await fixture.RenderConnectorAsync(token);
            if (scenario != "neverRegistered")
            {
                (await fixture.Connect.PutConnectorConfigAsync(fixture.ConnectorName, rendered.Config, token))
                    .Succeeded.Should()
                    .BeTrue();
                await fixture.WaitForConnectorStateAsync("RUNNING", token);
                (await fixture.WaitForCommittedOffsetsAsync(token)).Entries.Should().NotBeEmpty();
            }
            await using ServiceProvider services = fixture.CreateRetirementServices(
                stateRoot,
                timeout: false
            );
            await using AsyncServiceScope scope = services.CreateAsyncScope();
            ICdcBindingLifecycleService bindings =
                scope.ServiceProvider.GetRequiredService<ICdcBindingLifecycleService>();
            CdcBinding binding = fixture.BuildBinding();
            // A durable provisioned generation can exist before connector registration. No adoption
            // proof is fabricated for the never-registered case.
            (await bindings.CreateBindingIfAbsentAsync(binding, token))
                .Status.Should()
                .Be(CdcControlPlaneOperationStatus.Succeeded);
            Dictionary<string, string> recordsBefore = ReadRecords(stateRoot);
            int providerBefore = await fixture.ReadRetirementProviderArtifactCountAsync(token);
            providerBefore.Should().Be(2);
            var sharedAclsBefore = await fixture.DescribeTopicAclsAsync(fixture.OffsetStoreTopicName);
            foreach (string topic in fixture.SharedWorkerTopics)
            {
                (await fixture.TopicExistsAsync(topic)).Should().BeTrue();
            }
            ICdcSetupController controller = scope.ServiceProvider.GetRequiredService<ICdcSetupController>();
            List<object> attempts = [];

            if (scenario == "providerTimeout")
            {
                await using ServiceProvider timedServices = fixture.CreateRetirementServices(
                    stateRoot,
                    timeout: true
                );
                await using AsyncServiceScope timedScope = timedServices.CreateAsyncScope();
                CdcContractReadResult<CdcCleanupProof> timedOut = await timedScope
                    .ServiceProvider.GetRequiredService<ICdcSetupController>()
                    .RetireAsync(fixture.RetirementRequest(false), token);
                timedOut.Contract.Should().BeNull();
                timedOut
                    .Diagnostics.Should()
                    .Contain(d =>
                        d.Code == CdcRetirementDiagnosticCodes.IncompleteRetryable
                        && d.Retryable
                        && d.Observed == "timedOut"
                    );
                (await fixture.Connect.GetConnectorStatusAsync(fixture.ConnectorName, token))
                    .Outcome.Should()
                    .Be(CdcConnectOutcome.NotFound);
                (await fixture.WaitForTopicAbsentAsync(binding.TopicName, token)).Should().BeTrue();
                (await fixture.WaitForTopicAbsentAsync(fixture.Inventory.ProgressTopicName, token))
                    .Should()
                    .BeTrue();
                (await fixture.ReadRetirementProviderArtifactCountAsync(token)).Should().Be(providerBefore);
                ReadRecords(stateRoot).Should().BeEquivalentTo(recordsBefore);
                attempts.Add(
                    new
                    {
                        phase = "providerTimeout",
                        timedOut.Diagnostics,
                        proof = timedOut.Contract,
                        records = ReadRecords(stateRoot),
                        connector = "notFound",
                        publicTopic = "absent",
                        progressTopic = "absent",
                        providerArtifacts = providerBefore,
                    }
                );
            }
            if (scenario != "registered")
            {
                CdcContractReadResult<CdcCleanupProof> refused = await controller.RetireAsync(
                    fixture.RetirementRequest(false),
                    token
                );
                refused.Contract.Should().BeNull();
                refused
                    .Diagnostics.Should()
                    .Contain(d => d.Code == CdcRetirementDiagnosticCodes.RefusedNoMutation && !d.Retryable);
                ReadRecords(stateRoot).Should().BeEquivalentTo(recordsBefore);
                (await fixture.ReadRetirementProviderArtifactCountAsync(token)).Should().Be(providerBefore);
                attempts.Add(
                    new
                    {
                        phase = "absentConnectorWithoutAssertion",
                        refused.Diagnostics,
                        proof = refused.Contract,
                    }
                );
            }
            CdcContractReadResult<CdcCleanupProof> completed = await controller.RetireAsync(
                fixture.RetirementRequest(scenario != "registered"),
                token
            );
            completed
                .Succeeded.Should()
                .BeTrue(string.Join("; ", completed.Diagnostics.Select(d => d.Message)));
            CdcCleanupProof proof = completed.Contract!;
            CdcCleanupProofValidator
                .Validate(proof, binding, DateTimeOffset.UtcNow)
                .Succeeded.Should()
                .BeTrue();
            proof.OperationId.Should().Be("retirement-retry");
            proof.BindingIdentity.Should().Be(binding.ToCompleteBindingIdentity());
            proof
                .GovernedArtifacts.Should()
                .OnlyContain(a =>
                    a.CleanupState == CdcCleanupState.Deleted || a.CleanupState == CdcCleanupState.NotFound
                );
            if (scenario != "registered")
            {
                proof
                    .GovernedArtifacts.Single(a =>
                        a.ArtifactKind == CdcGovernedArtifactKind.ConnectSourceOffsets
                    )
                    .EvidenceSummary.Should()
                    .Contain("the operator asserted");
            }
            (await fixture.Connect.GetConnectorStatusAsync(fixture.ConnectorName, token))
                .Outcome.Should()
                .Be(CdcConnectOutcome.NotFound);
            (await fixture.WaitForTopicAbsentAsync(binding.TopicName, token)).Should().BeTrue();
            (await fixture.WaitForTopicAbsentAsync(fixture.Inventory.ProgressTopicName, token))
                .Should()
                .BeTrue();
            (await fixture.DescribeTopicAclsAsync(binding.TopicName)).Should().BeEmpty();
            (await fixture.DescribeTopicAclsAsync(fixture.Inventory.ProgressTopicName)).Should().BeEmpty();
            (await fixture.ReadRetirementProviderArtifactCountAsync(token)).Should().Be(0);
            (await fixture.TopicExistsAsync(fixture.OffsetStoreTopicName)).Should().BeTrue();
            (await fixture.DescribeTopicAclsAsync(fixture.OffsetStoreTopicName))
                .Should()
                .BeEquivalentTo(sharedAclsBefore);
            (await bindings.ListBindingsAsync(binding.DeploymentKey, token)).States.Should().BeEmpty();
            CdcRetirementListResult retired = await bindings.ListRetirementsAsync(
                binding.DeploymentKey,
                token
            );
            CdcRetirement retirement = retired.Retirements.Should().ContainSingle().Subject;
            retirement.ToBindingIdentity().Should().Be(binding.ToBindingIdentity());
            retirement.PhysicalSourceFingerprint.Should().Be(binding.PhysicalSourceFingerprint);
            foreach (string topic in fixture.SharedWorkerTopics)
            {
                (await fixture.TopicExistsAsync(topic)).Should().BeTrue();
                proof.GovernedArtifacts.Should().NotContain(a => a.ArtifactName == topic);
            }
            ReadRecords(stateRoot).Should().NotBeEmpty();
            string artifact = Path.Combine(
                TestContext.CurrentContext.WorkDirectory,
                $"retirement-broker-{scenario}.json"
            );
            await File.WriteAllTextAsync(
                artifact,
                JsonSerializer.Serialize(
                    new
                    {
                        scenario,
                        boundary = "real PostgreSQL, Kafka, Connect, filesystem; provider deletion timeout injected only for providerTimeout",
                        recordsBefore,
                        providerBefore,
                        attempts,
                        cleanupProof = JsonSerializer.Deserialize<JsonElement>(
                            CdcJsonContract.Serialize(proof)
                        ),
                        recordsAfter = ReadRecords(stateRoot),
                        retired.Retirements,
                        after = new
                        {
                            connector = "notFound",
                            publicTopic = "absent",
                            progressTopic = "absent",
                            providerArtifacts = 0,
                            sharedWorkerTopics = fixture.SharedWorkerTopics,
                            sharedWorkerTopicsState = "present",
                            sharedOffsetAcls = "unchanged",
                        },
                    }
                ),
                token
            );
            TestContext.AddTestAttachment(artifact);
        }
        finally
        {
            Directory.Delete(stateRoot, recursive: true);
        }
    }

    private static Dictionary<string, string> ReadRecords(string root) =>
        Directory
            .GetFiles(root, "*.json", SearchOption.AllDirectories)
            .ToDictionary(path => Path.GetRelativePath(root, path), File.ReadAllText);
}
