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
/// SQL Server schema-history evidence, read from topic metadata and the offsets bounding the topic's
/// retained records. The continuity classifier requires this evidence for a SQL Server binding and
/// reports unknown continuity without it, so every state it can decide is produced here — and every
/// state it cannot is reported as unknown or unreadable rather than guessed at.
/// </summary>
[TestFixture]
[Parallelizable]
[Category("CdcKafkaSchemaHistory")]
public class Given_CdcKafkaSchemaHistory
{
    private static readonly DateTimeOffset ObservedAt = new(2026, 8, 28, 9, 0, 0, TimeSpan.Zero);

    [Test]
    public async Task It_reports_a_topic_holding_records_as_valid()
    {
        CdcArtifactInventory inventory = Inventory(CdcProvider.SqlServer);

        CdcSqlServerSchemaHistoryEvidence? evidence = await RunAsync(
            inventory,
            Broker(inventory.SchemaHistoryTopicName!, partitionCount: 1),
            OffsetReader(Watermarks((0, 0, 12)))
        );

        evidence!.State.Should().Be(CdcSqlServerSchemaHistoryState.Valid);
        evidence.Diagnostics.Should().BeEmpty("a continuous schema history reports no fault");
    }

    /// <summary>
    /// A topic whose log no longer starts at zero has lost the records in front of it. The governed
    /// policy for this topic is <c>cleanup.policy=delete</c> with infinite time and size retention, so
    /// nothing the deployment configures advances that offset; what remains cannot reconstruct the
    /// schema at a source offset older than the surviving prefix.
    /// </summary>
    [Test]
    public async Task It_reports_a_truncated_topic_with_a_committed_streaming_offset_as_required_record_lost()
    {
        CdcArtifactInventory inventory = Inventory(CdcProvider.SqlServer);

        CdcSqlServerSchemaHistoryEvidence? evidence = await RunAsync(
            inventory,
            Broker(inventory.SchemaHistoryTopicName!, partitionCount: 1),
            OffsetReader(Watermarks((0, 8, 12))),
            connectorCommittedStreamingOffset: true
        );

        evidence!.State.Should().Be(CdcSqlServerSchemaHistoryState.RequiredRecordLost);
        Diagnostic(evidence, inventory.SchemaHistoryTopicName!)
            .Category.Should()
            .Be(CdcDiagnosticCategory.SourceHistoryLost);
        Diagnostic(evidence, inventory.SchemaHistoryTopicName!).Observed.Should().Be("truncated");
    }

    /// <summary>
    /// The same truncated topic without a committed streaming offset: there is no retained position
    /// whose replay the missing prefix would have broken, so the state is undecidable rather than lost
    /// - and undecidable keeps readiness false.
    /// </summary>
    [Test]
    public async Task It_reports_a_truncated_topic_with_no_committed_streaming_offset_as_unknown()
    {
        CdcArtifactInventory inventory = Inventory(CdcProvider.SqlServer);

        CdcSqlServerSchemaHistoryEvidence? evidence = await RunAsync(
            inventory,
            Broker(inventory.SchemaHistoryTopicName!, partitionCount: 1),
            OffsetReader(Watermarks((0, 8, 12))),
            connectorCommittedStreamingOffset: false
        );

        evidence!.State.Should().Be(CdcSqlServerSchemaHistoryState.Unknown);
        Diagnostic(evidence, inventory.SchemaHistoryTopicName!).Observed.Should().Be("truncated");
    }

    /// <summary>
    /// Truncation is decided across every partition the broker reports, so a retained history on one
    /// partition does not cover a removed prefix on another.
    /// </summary>
    [Test]
    public async Task It_reports_a_topic_truncated_on_one_of_several_partitions_as_required_record_lost()
    {
        CdcArtifactInventory inventory = Inventory(CdcProvider.SqlServer);

        CdcSqlServerSchemaHistoryEvidence? evidence = await RunAsync(
            inventory,
            Broker(inventory.SchemaHistoryTopicName!, partitionCount: 2),
            OffsetReader(Watermarks((0, 0, 12), (1, 5, 9))),
            connectorCommittedStreamingOffset: true
        );

        evidence!.State.Should().Be(CdcSqlServerSchemaHistoryState.RequiredRecordLost);
    }

    [Test]
    public async Task It_reports_an_absent_topic_as_missing()
    {
        CdcArtifactInventory inventory = Inventory(CdcProvider.SqlServer);

        CdcSqlServerSchemaHistoryEvidence? evidence = await RunAsync(
            inventory,
            EmptyBroker(),
            OffsetReader(Watermarks())
        );

        evidence!.State.Should().Be(CdcSqlServerSchemaHistoryState.Missing);
        Diagnostic(evidence, inventory.SchemaHistoryTopicName!).Observed.Should().Be("absent");
    }

    [Test]
    public async Task It_reports_an_empty_topic_with_a_committed_streaming_offset_as_empty_with_retained_offset()
    {
        CdcArtifactInventory inventory = Inventory(CdcProvider.SqlServer);

        CdcSqlServerSchemaHistoryEvidence? evidence = await RunAsync(
            inventory,
            Broker(inventory.SchemaHistoryTopicName!, partitionCount: 1),
            OffsetReader(Watermarks((0, 4, 4))),
            connectorCommittedStreamingOffset: true
        );

        evidence!.State.Should().Be(CdcSqlServerSchemaHistoryState.EmptyWithRetainedOffset);
        Diagnostic(evidence, inventory.SchemaHistoryTopicName!)
            .Category.Should()
            .Be(CdcDiagnosticCategory.SourceHistoryLost);
    }

    /// <summary>
    /// Without a committed streaming offset there is nothing whose replay the empty history would have
    /// broken, so the state is undecidable rather than lost — and undecidable keeps readiness false.
    /// </summary>
    [Test]
    public async Task It_reports_an_empty_topic_with_no_committed_streaming_offset_as_unknown()
    {
        CdcArtifactInventory inventory = Inventory(CdcProvider.SqlServer);

        CdcSqlServerSchemaHistoryEvidence? evidence = await RunAsync(
            inventory,
            Broker(inventory.SchemaHistoryTopicName!, partitionCount: 1),
            OffsetReader(Watermarks((0, 0, 0))),
            connectorCommittedStreamingOffset: false
        );

        evidence!.State.Should().Be(CdcSqlServerSchemaHistoryState.Unknown);
    }

    [Test]
    public async Task It_reports_an_unreachable_broker_as_unreadable()
    {
        CdcArtifactInventory inventory = Inventory(CdcProvider.SqlServer);
        IAdminClient adminClient = A.Fake<IAdminClient>();
        A.CallTo(() => adminClient.GetMetadata(A<TimeSpan>._))
            .Throws(new KafkaException(ErrorCode.Local_AllBrokersDown));

        CdcSqlServerSchemaHistoryEvidence? evidence = await RunAsync(
            inventory,
            adminClient,
            OffsetReader(Watermarks())
        );

        evidence!.State.Should().Be(CdcSqlServerSchemaHistoryState.Unreadable);
        Diagnostic(evidence, inventory.SchemaHistoryTopicName!)
            .Observed.Should()
            .Be(ErrorCode.Local_AllBrokersDown.ToString());
    }

    [Test]
    public async Task It_reports_an_unreadable_offset_response_as_unreadable()
    {
        CdcArtifactInventory inventory = Inventory(CdcProvider.SqlServer);

        CdcSqlServerSchemaHistoryEvidence? evidence = await RunAsync(
            inventory,
            Broker(inventory.SchemaHistoryTopicName!, partitionCount: 1),
            OffsetReader(Watermarks((0, 0, 12)), new Error(ErrorCode.LeaderNotAvailable))
        );

        evidence!.State.Should().Be(CdcSqlServerSchemaHistoryState.Unreadable);
    }

    [Test]
    public async Task It_reports_a_failed_offset_request_as_unreadable()
    {
        CdcArtifactInventory inventory = Inventory(CdcProvider.SqlServer);
        ICdcKafkaTopicOffsetReader offsetReader = A.Fake<ICdcKafkaTopicOffsetReader>();
        A.CallTo(() =>
                offsetReader.ListOffsetsAsync(A<IReadOnlyList<TopicPartitionOffsetSpec>>._, A<TimeSpan>._)
            )
            .Throws(new KafkaException(ErrorCode.RequestTimedOut));

        CdcSqlServerSchemaHistoryEvidence? evidence = await RunAsync(
            inventory,
            Broker(inventory.SchemaHistoryTopicName!, partitionCount: 1),
            offsetReader
        );

        evidence!.State.Should().Be(CdcSqlServerSchemaHistoryState.Unreadable);
    }

    /// <summary>
    /// The topic is governed as single-partition, but the read decides emptiness across every partition
    /// the broker reports, so an extra partition holding records cannot be mistaken for an empty topic.
    /// </summary>
    [Test]
    public async Task It_reports_a_topic_whose_records_are_on_another_partition_as_valid()
    {
        CdcArtifactInventory inventory = Inventory(CdcProvider.SqlServer);

        CdcSqlServerSchemaHistoryEvidence? evidence = await RunAsync(
            inventory,
            Broker(inventory.SchemaHistoryTopicName!, partitionCount: 2),
            OffsetReader(Watermarks((0, 0, 0), (1, 0, 3)))
        );

        evidence!.State.Should().Be(CdcSqlServerSchemaHistoryState.Valid);
    }

    [TestCase(CdcSqlServerSchemaHistoryEnablementPhase.BeforeInitialAdmission)]
    [TestCase(CdcSqlServerSchemaHistoryEnablementPhase.AfterInitialAdmission)]
    public async Task It_reports_the_enablement_phase_the_caller_supplied(
        CdcSqlServerSchemaHistoryEnablementPhase enablementPhase
    )
    {
        CdcArtifactInventory inventory = Inventory(CdcProvider.SqlServer);

        CdcSqlServerSchemaHistoryEvidence? valid = await RunAsync(
            inventory,
            Broker(inventory.SchemaHistoryTopicName!, partitionCount: 1),
            OffsetReader(Watermarks((0, 0, 12))),
            enablementPhase
        );
        CdcSqlServerSchemaHistoryEvidence? missing = await RunAsync(
            inventory,
            EmptyBroker(),
            OffsetReader(Watermarks()),
            enablementPhase
        );

        valid!.EnablementPhase.Should().Be(enablementPhase);
        missing!.EnablementPhase.Should().Be(enablementPhase);
    }

    /// <summary>
    /// PostgreSQL has no schema-history topic, and the classifier returns before consulting the field
    /// for that provider, so nothing is read and no evidence is invented for it.
    /// </summary>
    [Test]
    public async Task It_never_reads_a_topic_for_a_postgresql_binding()
    {
        CdcArtifactInventory inventory = Inventory(CdcProvider.Postgresql);
        IAdminClient adminClient = A.Fake<IAdminClient>();
        ICdcKafkaTopicOffsetReader offsetReader = A.Fake<ICdcKafkaTopicOffsetReader>();

        CdcSqlServerSchemaHistoryEvidence? evidence = await RunAsync(inventory, adminClient, offsetReader);

        evidence.Should().BeNull();
        A.CallTo(() => adminClient.GetMetadata(A<TimeSpan>._)).MustNotHaveHappened();
        A.CallTo(() =>
                offsetReader.ListOffsetsAsync(A<IReadOnlyList<TopicPartitionOffsetSpec>>._, A<TimeSpan>._)
            )
            .MustNotHaveHappened();
    }

    private static async Task<CdcSqlServerSchemaHistoryEvidence?> RunAsync(
        CdcArtifactInventory inventory,
        IAdminClient adminClient,
        ICdcKafkaTopicOffsetReader offsetReader,
        CdcSqlServerSchemaHistoryEnablementPhase enablementPhase =
            CdcSqlServerSchemaHistoryEnablementPhase.BeforeInitialAdmission,
        bool connectorCommittedStreamingOffset = true
    )
    {
        CdcKafkaAdminAdapter adapter = new(
            adminClient,
            Options.Create(ControlOptions(inventory)),
            new FixedTimeProvider(ObservedAt),
            NullLogger<CdcKafkaAdminAdapter>.Instance,
            offsetReader
        );

        CdcSqlServerSchemaHistoryEvidence? evidence = await adapter.ReadSqlServerSchemaHistoryAsync(
            inventory,
            enablementPhase,
            connectorCommittedStreamingOffset,
            CancellationToken.None
        );

        return evidence;
    }

    private static CdcDiagnostic Diagnostic(CdcSqlServerSchemaHistoryEvidence evidence, string topicName) =>
        evidence
            .Diagnostics.Should()
            .ContainSingle(diagnostic => diagnostic.ArtifactName == topicName)
            .Subject;

    private static Dictionary<int, (long Earliest, long Latest)> Watermarks(
        params (int Partition, long Earliest, long Latest)[] partitions
    ) =>
        partitions.ToDictionary(
            partition => partition.Partition,
            partition => (partition.Earliest, partition.Latest)
        );

    /// <summary>
    /// Answers each requested bound from the supplied watermarks, discriminating on the requested
    /// offset spec rather than on call order.
    /// </summary>
    private static ICdcKafkaTopicOffsetReader OffsetReader(
        Dictionary<int, (long Earliest, long Latest)> watermarks,
        Error? error = null
    )
    {
        ICdcKafkaTopicOffsetReader offsetReader = A.Fake<ICdcKafkaTopicOffsetReader>();
        A.CallTo(() =>
                offsetReader.ListOffsetsAsync(A<IReadOnlyList<TopicPartitionOffsetSpec>>._, A<TimeSpan>._)
            )
            .ReturnsLazily(
                (IReadOnlyList<TopicPartitionOffsetSpec> offsetSpecs, TimeSpan _) =>
                    Task.FromResult<IReadOnlyList<ListOffsetsResultInfo>>([
                        .. offsetSpecs.Select(offsetSpec => new ListOffsetsResultInfo
                        {
                            TopicPartitionOffsetError = new(
                                offsetSpec.TopicPartition,
                                new Offset(
                                    offsetSpec.OffsetSpec is OffsetSpec.EarliestSpec
                                        ? watermarks[offsetSpec.TopicPartition.Partition.Value].Earliest
                                        : watermarks[offsetSpec.TopicPartition.Partition.Value].Latest
                                ),
                                error ?? new Error(ErrorCode.NoError),
                                null
                            ),
                        }),
                    ])
            );

        return offsetReader;
    }

    private static IAdminClient EmptyBroker()
    {
        IAdminClient adminClient = A.Fake<IAdminClient>();
        A.CallTo(() => adminClient.GetMetadata(A<TimeSpan>._)).Returns(new Metadata([], [], 0, "broker"));

        return adminClient;
    }

    private static IAdminClient Broker(string topicName, int partitionCount)
    {
        int[] replicas = [0];
        TopicMetadata topicMetadata = new(
            topicName,
            [
                .. Enumerable
                    .Range(0, partitionCount)
                    .Select(index => new PartitionMetadata(
                        index,
                        0,
                        replicas,
                        replicas,
                        new Error(ErrorCode.NoError)
                    )),
            ],
            new Error(ErrorCode.NoError)
        );

        IAdminClient adminClient = A.Fake<IAdminClient>();
        A.CallTo(() => adminClient.GetMetadata(A<TimeSpan>._))
            .Returns(new Metadata([], [topicMetadata], 0, "broker"));

        return adminClient;
    }

    private static CdcArtifactInventory Inventory(CdcProvider provider) =>
        CdcArtifactNameGenerator.Render(new("dms-local", "edfi.dms", "data-store-1", 1, provider)).Inventory!;

    private static CdcControlOptions ControlOptions(CdcArtifactInventory inventory) =>
        new()
        {
            DeploymentKey = inventory.DeploymentKey,
            InstanceKey = inventory.InstanceKey,
            TopicPrefix = inventory.TopicPrefix,
            Generation = inventory.Generation,
            PartitionCount = 1,
            KafkaBootstrapServers = "localhost:9092",
            ConnectBaseUri = "http://localhost:8083",
            ConnectWorkerKey = "worker-1",
            ConnectOffsetStorageTopic = "connect-offsets",
            DurabilityProfile = CdcControlOptions.LocalDurabilityProfile,
            MaxRecordBytes = 4_194_304,
            ConnectorKafkaPrincipal = "User:connector",
            ConnectWorkerPrincipal = "User:connect-worker",
            DmsBaseUrl = "http://localhost:8080",
            DmsBearerToken = "token",
        };

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
