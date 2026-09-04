// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Confluent.Kafka;
using Confluent.Kafka.Admin;
using EdFi.DataManagementService.Core.DocumentCache.Cdc;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Cdc.Control.Tests.Unit;

/// <summary>
/// Binding topic and ACL teardown. Artifacts are located by kind and name rather than by array
/// position, and the shared cluster-scoped Connect offset store must never appear in any result.
/// </summary>
[TestFixture]
[Parallelizable]
[Category("CdcKafkaTeardown")]
public class Given_CdcKafkaTeardown
{
    private const string OffsetStoreTopic = "connect-offsets";
    private const string ConnectorPrincipal = "User:connector";

    private static readonly DateTimeOffset ObservedAt = new(2026, 8, 28, 9, 0, 0, TimeSpan.Zero);

    [Test]
    public async Task It_deletes_every_governed_postgresql_artifact()
    {
        CdcArtifactInventory inventory = Inventory(CdcProvider.Postgresql);
        IAdminClient adminClient = Broker(inventory, aclCount: 2);

        IReadOnlyList<CdcGovernedArtifact> artifacts = await RunAsync(adminClient, inventory);

        artifacts
            .Select(artifact => artifact.ArtifactKind)
            .Should()
            .BeEquivalentTo([
                CdcGovernedArtifactKind.PublicTopic,
                CdcGovernedArtifactKind.PublicTopicAcls,
                CdcGovernedArtifactKind.ProgressTopic,
                CdcGovernedArtifactKind.ProgressTopicAcls,
            ]);
        Artifact(artifacts, CdcGovernedArtifactKind.PublicTopic)
            .CleanupState.Should()
            .Be(CdcCleanupState.Deleted);
        Artifact(artifacts, CdcGovernedArtifactKind.PublicTopicAcls)
            .CleanupState.Should()
            .Be(CdcCleanupState.Deleted);
        Artifact(artifacts, CdcGovernedArtifactKind.ProgressTopic)
            .ArtifactName.Should()
            .Be(inventory.ProgressTopicName);
    }

    [Test]
    public async Task It_deletes_the_sql_server_schema_history_artifacts_as_well()
    {
        CdcArtifactInventory inventory = Inventory(CdcProvider.SqlServer);
        IAdminClient adminClient = Broker(inventory, aclCount: 4);

        IReadOnlyList<CdcGovernedArtifact> artifacts = await RunAsync(adminClient, inventory);

        artifacts
            .Select(artifact => artifact.ArtifactKind)
            .Should()
            .Contain([
                CdcGovernedArtifactKind.SchemaHistoryTopic,
                CdcGovernedArtifactKind.SchemaHistoryTopicAcls,
            ]);
        Artifact(artifacts, CdcGovernedArtifactKind.SchemaHistoryTopic)
            .ArtifactName.Should()
            .Be(inventory.SchemaHistoryTopicName);
        Artifact(artifacts, CdcGovernedArtifactKind.SchemaHistoryTopicAcls)
            .CleanupState.Should()
            .Be(CdcCleanupState.Deleted);
    }

    [Test]
    public async Task It_reports_no_schema_history_artifacts_for_postgresql()
    {
        CdcArtifactInventory inventory = Inventory(CdcProvider.Postgresql);

        IReadOnlyList<CdcGovernedArtifact> artifacts = await RunAsync(
            Broker(inventory, aclCount: 2),
            inventory
        );

        artifacts
            .Should()
            .NotContain(artifact =>
                artifact.ArtifactKind == CdcGovernedArtifactKind.SchemaHistoryTopic
                || artifact.ArtifactKind == CdcGovernedArtifactKind.SchemaHistoryTopicAcls
            );
    }

    [Test]
    public async Task It_reports_an_absent_topic_as_not_found()
    {
        CdcArtifactInventory inventory = Inventory(CdcProvider.Postgresql);
        IAdminClient adminClient = Broker(inventory, aclCount: 2, presentTopics: []);

        IReadOnlyList<CdcGovernedArtifact> artifacts = await RunAsync(adminClient, inventory);

        Artifact(artifacts, CdcGovernedArtifactKind.PublicTopic)
            .CleanupState.Should()
            .Be(CdcCleanupState.NotFound);
        Artifact(artifacts, CdcGovernedArtifactKind.ProgressTopic)
            .CleanupState.Should()
            .Be(CdcCleanupState.NotFound);
        A.CallTo(() => adminClient.DeleteTopicsAsync(A<IEnumerable<string>>._, A<DeleteTopicsOptions>._))
            .MustNotHaveHappened();
    }

    [Test]
    public async Task It_reports_absent_grants_as_not_found()
    {
        CdcArtifactInventory inventory = Inventory(CdcProvider.Postgresql);
        IAdminClient adminClient = Broker(inventory, aclCount: 0);

        IReadOnlyList<CdcGovernedArtifact> artifacts = await RunAsync(adminClient, inventory);

        Artifact(artifacts, CdcGovernedArtifactKind.PublicTopicAcls)
            .CleanupState.Should()
            .Be(CdcCleanupState.NotFound);
        Artifact(artifacts, CdcGovernedArtifactKind.PublicTopic)
            .CleanupState.Should()
            .Be(CdcCleanupState.Deleted);
    }

    [Test]
    public async Task It_reports_grants_as_not_found_when_the_deployment_has_no_authorizer()
    {
        CdcArtifactInventory inventory = Inventory(CdcProvider.Postgresql);
        IAdminClient adminClient = Broker(inventory, aclCount: 2);

        IReadOnlyList<CdcGovernedArtifact> artifacts = await RunAsync(
            adminClient,
            inventory,
            aclsEnabled: false
        );

        Artifact(artifacts, CdcGovernedArtifactKind.PublicTopicAcls)
            .CleanupState.Should()
            .Be(CdcCleanupState.NotFound);
        A.CallTo(() =>
                adminClient.DeleteAclsAsync(A<IEnumerable<AclBindingFilter>>._, A<DeleteAclsOptions>._)
            )
            .MustNotHaveHappened();
    }

    [Test]
    public async Task It_tolerates_a_topic_deleted_concurrently()
    {
        CdcArtifactInventory inventory = Inventory(CdcProvider.Postgresql);
        IAdminClient adminClient = Broker(inventory, aclCount: 2);
        A.CallTo(() => adminClient.DeleteTopicsAsync(A<IEnumerable<string>>._, A<DeleteTopicsOptions>._))
            .Throws(
                new DeleteTopicsException([
                    new DeleteTopicReport
                    {
                        Topic = inventory.TopicName,
                        Error = new Error(ErrorCode.UnknownTopicOrPart),
                    },
                ])
            );

        IReadOnlyList<CdcGovernedArtifact> artifacts = await RunAsync(adminClient, inventory);

        Artifact(artifacts, CdcGovernedArtifactKind.PublicTopic)
            .CleanupState.Should()
            .Be(CdcCleanupState.NotFound);
    }

    [Test]
    public async Task It_removes_only_literal_grants_on_the_governed_topic()
    {
        CdcArtifactInventory inventory = Inventory(CdcProvider.Postgresql);
        IAdminClient adminClient = Broker(inventory, aclCount: 2);

        await RunAsync(adminClient, inventory);

        DeleteFilters(adminClient)
            .Should()
            .OnlyContain(filter =>
                filter.PatternFilter.Type == ResourceType.Topic
                && filter.PatternFilter.ResourcePatternType == ResourcePatternType.Literal
            );
        DeleteFilters(adminClient)
            .Select(filter => filter.PatternFilter.Name)
            .Should()
            .BeEquivalentTo(inventory.TopicName, inventory.ProgressTopicName);
    }

    [Test]
    public async Task It_never_touches_the_shared_connect_offset_store()
    {
        CdcArtifactInventory inventory = Inventory(CdcProvider.SqlServer);
        IAdminClient adminClient = Broker(inventory, aclCount: 4);

        IReadOnlyList<CdcGovernedArtifact> artifacts = await RunAsync(adminClient, inventory);

        artifacts.Should().NotContain(artifact => artifact.ArtifactName == OffsetStoreTopic);
        artifacts
            .Should()
            .NotContain(artifact => artifact.ArtifactKind == CdcGovernedArtifactKind.ConnectSourceOffsets);
        DeletedTopics(adminClient).Should().NotContain(OffsetStoreTopic);
        DeleteFilters(adminClient)
            .Should()
            .NotContain(filter => filter.PatternFilter.Name == OffsetStoreTopic);
    }

    [Test]
    public async Task It_refuses_a_binding_topic_that_resolves_to_the_shared_offset_store()
    {
        CdcArtifactInventory inventory = Inventory(CdcProvider.Postgresql) with
        {
            TopicName = OffsetStoreTopic,
        };
        IAdminClient adminClient = Broker(inventory, aclCount: 2);

        Func<Task> teardown = () => RunAsync(adminClient, inventory);

        await teardown.Should().ThrowAsync<InvalidOperationException>().WithMessage("*offset store*");
        A.CallTo(() => adminClient.DeleteTopicsAsync(A<IEnumerable<string>>._, A<DeleteTopicsOptions>._))
            .MustNotHaveHappened();
    }

    [Test]
    public async Task It_removes_grants_before_the_topic_they_protect()
    {
        CdcArtifactInventory inventory = Inventory(CdcProvider.Postgresql);
        IAdminClient adminClient = Broker(inventory, aclCount: 2);

        await RunAsync(adminClient, inventory);

        List<string> ordered =
        [
            .. Fake.GetCalls(adminClient)
                .Select(call => call.Method.Name)
                .Where(name =>
                    name is nameof(IAdminClient.DeleteAclsAsync) or nameof(IAdminClient.DeleteTopicsAsync)
                ),
        ];

        ordered
            .Should()
            .Equal(
                nameof(IAdminClient.DeleteAclsAsync),
                nameof(IAdminClient.DeleteTopicsAsync),
                nameof(IAdminClient.DeleteAclsAsync),
                nameof(IAdminClient.DeleteTopicsAsync)
            );
    }

    [Test]
    public async Task It_propagates_a_broker_failure_rather_than_reporting_removal()
    {
        CdcArtifactInventory inventory = Inventory(CdcProvider.Postgresql);
        IAdminClient adminClient = Broker(inventory, aclCount: 2);
        A.CallTo(() => adminClient.DeleteTopicsAsync(A<IEnumerable<string>>._, A<DeleteTopicsOptions>._))
            .Throws(new KafkaException(ErrorCode.ClusterAuthorizationFailed));

        Func<Task> teardown = () => RunAsync(adminClient, inventory);

        await teardown.Should().ThrowAsync<KafkaException>();
    }

    [Test]
    public async Task It_keeps_every_evidence_summary_free_of_the_topic_identity()
    {
        CdcArtifactInventory inventory = Inventory(CdcProvider.SqlServer);

        IReadOnlyList<CdcGovernedArtifact> artifacts = await RunAsync(
            Broker(inventory, aclCount: 4),
            inventory
        );

        artifacts
            .Should()
            .OnlyContain(artifact =>
                artifact.EvidenceSummary.Length > 0
                && !artifact.EvidenceSummary.Contains(ConnectorPrincipal, StringComparison.OrdinalIgnoreCase)
            );
    }

    private static Task<IReadOnlyList<CdcGovernedArtifact>> RunAsync(
        IAdminClient adminClient,
        CdcArtifactInventory inventory,
        bool aclsEnabled = true
    ) =>
        new CdcKafkaAdminAdapter(
            adminClient,
            Options.Create(ControlOptions(aclsEnabled)),
            new FixedTimeProvider(ObservedAt),
            NullLogger<CdcKafkaAdminAdapter>.Instance
        ).DeleteBindingArtifactsAsync(inventory, CancellationToken.None);

    private static CdcGovernedArtifact Artifact(
        IReadOnlyList<CdcGovernedArtifact> artifacts,
        CdcGovernedArtifactKind artifactKind
    ) => artifacts.Single(artifact => artifact.ArtifactKind == artifactKind);

    private static IReadOnlyList<AclBindingFilter> DeleteFilters(IAdminClient adminClient) =>
        [
            .. Fake.GetCalls(adminClient)
                .Where(call =>
                    string.Equals(
                        call.Method.Name,
                        nameof(IAdminClient.DeleteAclsAsync),
                        StringComparison.Ordinal
                    )
                )
                .SelectMany(call => (IEnumerable<AclBindingFilter>)call.Arguments[0]!),
        ];

    private static IReadOnlyList<string> DeletedTopics(IAdminClient adminClient) =>
        [
            .. Fake.GetCalls(adminClient)
                .Where(call =>
                    string.Equals(
                        call.Method.Name,
                        nameof(IAdminClient.DeleteTopicsAsync),
                        StringComparison.Ordinal
                    )
                )
                .SelectMany(call => (IEnumerable<string>)call.Arguments[0]!),
        ];

    private static IAdminClient Broker(
        CdcArtifactInventory inventory,
        int aclCount,
        IReadOnlyList<string>? presentTopics = null
    )
    {
        IReadOnlyList<string> topics =
            presentTopics
            ??
            [
                inventory.TopicName,
                inventory.ProgressTopicName,
                .. inventory.SchemaHistoryTopicName is { } schemaHistoryTopicName
                    ? new[] { schemaHistoryTopicName }
                    : [],
            ];

        IAdminClient adminClient = A.Fake<IAdminClient>();
        A.CallTo(() => adminClient.GetMetadata(A<TimeSpan>._))
            .Returns(
                new Metadata(
                    [new BrokerMetadata(0, "broker", 9092)],
                    [
                        .. topics.Select(topicName => new TopicMetadata(
                            topicName,
                            [new PartitionMetadata(0, 0, [0], [0], new Error(ErrorCode.NoError))],
                            new Error(ErrorCode.NoError)
                        )),
                    ],
                    0,
                    "broker"
                )
            );
        A.CallTo(() =>
                adminClient.DeleteAclsAsync(A<IEnumerable<AclBindingFilter>>._, A<DeleteAclsOptions>._)
            )
            .ReturnsLazily(call =>
                Task.FromResult<List<DeleteAclsResult>>([
                    new()
                    {
                        AclBindings =
                        [
                            .. Enumerable
                                .Range(0, aclCount)
                                .Select(_ =>
                                    DeletedGrant(
                                        ((IEnumerable<AclBindingFilter>)call.Arguments[0]!)
                                            .Single()
                                            .PatternFilter.Name
                                    )
                                ),
                        ],
                    },
                ])
            );

        return adminClient;
    }

    private static AclBinding DeletedGrant(string topicName) =>
        new()
        {
            Pattern = new ResourcePattern
            {
                Type = ResourceType.Topic,
                Name = topicName,
                ResourcePatternType = ResourcePatternType.Literal,
            },
            Entry = new AccessControlEntry
            {
                Principal = ConnectorPrincipal,
                Host = CdcKafkaAdminAdapter.AnyHost,
                Operation = AclOperation.Write,
                PermissionType = AclPermissionType.Allow,
            },
        };

    private static CdcArtifactInventory Inventory(CdcProvider provider) =>
        CdcArtifactNameGenerator.Render(new("dms-local", "edfi.dms", "data-store-1", 1, provider)).Inventory!;

    private static CdcControlOptions ControlOptions(bool aclsEnabled) =>
        new()
        {
            DeploymentKey = "dms-local",
            InstanceKey = "data-store-1",
            TopicPrefix = "edfi.dms",
            Generation = 1,
            PartitionCount = 3,
            KafkaBootstrapServers = "localhost:9092",
            ConnectBaseUri = "http://localhost:8083",
            ConnectWorkerKey = "worker-1",
            ConnectOffsetStorageTopic = OffsetStoreTopic,
            DurabilityProfile = CdcControlOptions.LocalDurabilityProfile,
            MaxRecordBytes = 4_194_304,
            AclsEnabled = aclsEnabled,
            ConnectorKafkaPrincipal = ConnectorPrincipal,
            ConnectWorkerPrincipal = "User:connect-worker",
            DmsBaseUrl = "http://localhost:8080",
            DmsBearerToken = "token",
        };

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
