// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text;
using System.Text.Json;
using EdFi.DataManagementService.Backend;
using EdFi.DataManagementService.Backend.Cdc;
using EdFi.DataManagementService.Backend.Cdc.Tests.Integration;
using EdFi.DataManagementService.Backend.Cdc.Tests.Unit;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.DocumentCache;
using EdFi.DataManagementService.Core.DocumentCache.Cdc;
using EdFi.DataManagementService.Tests.E2E.Cdc;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace EdFi.DataManagementService.Tests.Unit;

[TestFixture]
public class Given_CdcRebuildObservations
{
    private readonly DocumentCacheTargetKey _target = DocumentCacheTargetKey.Create("Tenant", 7);
    private readonly DocumentCacheAdministrativeCommandPhase[] _phases =
    [
        DocumentCacheAdministrativeCommandPhase.EnterResetting,
        DocumentCacheAdministrativeCommandPhase.ClearCache,
        DocumentCacheAdministrativeCommandPhase.EnterRebuilding,
        DocumentCacheAdministrativeCommandPhase.CaptureBoundary,
        DocumentCacheAdministrativeCommandPhase.SeedBaseline,
        DocumentCacheAdministrativeCommandPhase.DrainWork,
        DocumentCacheAdministrativeCommandPhase.EnterTracking,
        DocumentCacheAdministrativeCommandPhase.Complete,
    ];
    private DocumentCacheAdministrativeCommandExecutionId _execution = null!;
    private DocumentCacheAdministrativeCommandResult _result = null!;
    private CdcRebuildObservations _collector = null!;
    private ServiceProvider _provider = null!;
    private IDocumentCacheProjectionObservationSink _sink = null!;
    private DocumentCacheProjectionObservationStore _store = null!;
    private CdcRebuildObservation _observation = null!;

    [SetUp]
    public async Task Setup()
    {
        _execution = DocumentCacheAdministrativeCommandExecutionId.New();
        _result = new(
            DocumentCacheAdministrativeCommand.OnlineCacheRebuild,
            DocumentCacheAdministrativeTargetKey.FromTargetKey(_target),
            DocumentCacheAdministrativeCommandStatus.Completed,
            DocumentCacheAdministrativeCommandClassification.Succeeded,
            true,
            1
        );
        _collector = new(_target);
        ServiceCollection services = new();
        services.AddSingleton(new DocumentCacheProjectionObservationStore(TimeProvider.System, 2));
        _collector.ConfigureServices(services);
        _provider = services.BuildServiceProvider();
        _sink = _provider.GetRequiredService<IDocumentCacheProjectionObservationSink>();
        _store = _provider.GetRequiredService<DocumentCacheProjectionObservationStore>();
        _observation = await _collector.RunAsync(
            token =>
            {
                token.CanBeCanceled.Should().BeTrue();
                foreach (var phase in _phases)
                {
                    _sink.ObserveAdministrativeCommand(Snapshot(phase, false));
                    for (int repeat = 0; repeat < 100; repeat++)
                    {
                        _sink.ObserveAdministrativeCommand(Snapshot(phase, true));
                    }
                }
                return Task.FromResult(_result);
            },
            CancellationToken.None
        );
    }

    [TearDown]
    public async Task Cleanup() => await _provider.DisposeAsync();

    [Test]
    public void It_collects_completed_committed_phases_once_including_complete_without_an_enter_phase()
    {
        CdcRebuildAssertions.AssertCompleted(_observation);
        _observation.Phases.Select(p => p.Phase).Should().Equal(_phases);
        _observation.Result.Should().BeSameAs(_result);
    }

    [Test]
    public void It_delegates_administrative_observation_and_end_to_the_production_store()
    {
        _store
            .CurrentSnapshot.GetActiveCommand(_execution)!
            .LastCompletedPhase.Should()
            .Be(DocumentCacheAdministrativeCommandPhase.Complete);
        _sink.EndAdministrativeCommand(_execution, _result);
        _store.CurrentSnapshot.GetActiveCommand(_execution).Should().BeNull();
    }

    [Test]
    public async Task It_ignores_entered_phases_and_other_commands_targets_and_inactive_observations()
    {
        _sink.ObserveAdministrativeCommand(
            Snapshot(DocumentCacheAdministrativeCommandPhase.ClearCache, true)
        );
        var observed = await _collector.RunAsync(
            _ =>
            {
                foreach (var phase in _phases)
                {
                    _sink.ObserveAdministrativeCommand(Snapshot(phase, false));
                    _sink.ObserveAdministrativeCommand(Snapshot(phase, true, otherTarget: true));
                    _sink.ObserveAdministrativeCommand(Snapshot(phase, true, otherCommand: true));
                }
                return Task.FromResult(_result);
            },
            CancellationToken.None
        );
        observed.Phases.Should().BeEmpty();
    }

    [TestCase(DocumentCacheAdministrativeCommandPhase.EnterResetting)]
    [TestCase(DocumentCacheAdministrativeCommandPhase.ClearCache)]
    [TestCase(DocumentCacheAdministrativeCommandPhase.EnterRebuilding)]
    [TestCase(DocumentCacheAdministrativeCommandPhase.SeedBaseline)]
    [TestCase(DocumentCacheAdministrativeCommandPhase.DrainWork)]
    [TestCase(DocumentCacheAdministrativeCommandPhase.EnterTracking)]
    public void It_rejects_success_without_each_required_completed_phase(
        DocumentCacheAdministrativeCommandPhase missing
    )
    {
        Action assert = () =>
            CdcRebuildAssertions.AssertCompleted(
                _observation with
                {
                    Phases = _observation.Phases.Where(p => p.Phase != missing).ToArray(),
                }
            );
        assert.Should().Throw<AssertionException>();
    }

    [Test]
    public void It_rejects_reordered_completed_phases()
    {
        Action assert = () =>
            CdcRebuildAssertions.AssertCompleted(
                _observation with
                {
                    Phases = _observation.Phases.Reverse().ToArray(),
                }
            );
        assert.Should().Throw<AssertionException>();
    }

    [TestCase(false)]
    [TestCase(true)]
    public void It_rejects_wrong_committed_lifecycle_or_latch(bool latch)
    {
        var phases = _observation.Phases.ToArray();
        phases[1] = phases[1] with
        {
            Lifecycle = latch ? DocumentCacheLifecycleState.Resetting : DocumentCacheLifecycleState.Tracking,
            CacheAheadRecoveryRequired = latch,
        };
        Action assert = () => CdcRebuildAssertions.AssertCompleted(_observation with { Phases = phases });
        assert.Should().Throw<AssertionException>();
    }

    [Test]
    public async Task It_propagates_cancellation_and_resets_collection_after_failure()
    {
        using CancellationTokenSource cancellation = new();
        Func<Task> command = () =>
            _collector.RunAsync(
                async token =>
                {
                    await cancellation.CancelAsync();
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                    return _result;
                },
                cancellation.Token
            );
        await command.Should().ThrowAsync<OperationCanceledException>();
        (await _collector.RunAsync(_ => Task.FromResult(_result), CancellationToken.None))
            .Phases.Should()
            .BeEmpty();
    }

    private DocumentCacheAdministrativeCommandObservationSnapshot Snapshot(
        DocumentCacheAdministrativeCommandPhase phase,
        bool complete,
        bool otherTarget = false,
        bool otherCommand = false
    )
    {
        var lifecycle = phase switch
        {
            DocumentCacheAdministrativeCommandPhase.EnterResetting
            or DocumentCacheAdministrativeCommandPhase.ClearCache => DocumentCacheLifecycleState.Resetting,
            DocumentCacheAdministrativeCommandPhase.EnterTracking
            or DocumentCacheAdministrativeCommandPhase.Complete => DocumentCacheLifecycleState.Tracking,
            _ => DocumentCacheLifecycleState.Rebuilding,
        };
        return new(
            _execution,
            otherCommand
                ? DocumentCacheAdministrativeCommand.ExplicitIntegrityScrub
                : DocumentCacheAdministrativeCommand.OnlineCacheRebuild,
            otherTarget ? DocumentCacheTargetKey.Create("Other", 7) : _target,
            new(1),
            2,
            TimeSpan.FromMinutes(1),
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            phase == DocumentCacheAdministrativeCommandPhase.Complete
                ? DocumentCacheAdministrativeCommandPhase.EnterTracking
                : phase,
            complete ? phase : null,
            true,
            lifecycle: lifecycle,
            cacheAheadRecoveryRequired: false
        );
    }
}

[TestFixture(CdcProvider.Postgresql)]
[TestFixture(CdcProvider.SqlServer)]
public class Given_CdcRebuildConsumer(CdcProvider provider)
{
    private CdcBinding _binding = null!;
    private Dictionary<Guid, IReadOnlyDictionary<long, JsonElement>> _expected = null!;
    private MessageContractConsumer _consumer = null!;
    private MessageContractKafkaRecord[] _records = null!;

    [SetUp]
    public void Setup()
    {
        _binding = new(
            1,
            "deployment",
            "",
            "1",
            "instance",
            1,
            provider,
            "source",
            "connector",
            "public",
            3,
            "kafka-murmur2-v1",
            1
        );
        _expected = [];
        List<MessageContractKafkaRecord> records = [];
        for (int index = 0; index < 3; index++)
        {
            Guid uuid = Guid.Parse($"abcdef00-0000-0000-0000-00000000000{index + 1}");
            CdcSourceDocument source = new(
                index + 1,
                uuid,
                32,
                901 + index,
                new DateTimeOffset(2026, 9, 28, 12, 30, 45, TimeSpan.Zero),
                "schema",
                "EdFi",
                "Student",
                "5.2.0"
            );
            JsonElement envelope = CdcEnvelopeExpectations.Create(
                CdcApiResource.Student,
                CdcApiClient.NewStudent("Rebuild"),
                source
            );
            _expected.Add(uuid, new Dictionary<long, JsonElement> { [source.ContentVersion] = envelope });
            int partition = MessageContractPartition.ForUuid(
                uuid.ToString("D"),
                3,
                _binding.PartitionerAlgorithm
            );
            records.Add(
                new(
                    _binding.TopicName,
                    partition,
                    index,
                    new(false, Encoding.UTF8.GetBytes(uuid.ToString("D"))),
                    new(false, Encoding.UTF8.GetBytes(envelope.GetRawText())),
                    [],
                    0
                )
            );
        }
        _records = records.OrderBy(r => r.Partition).ThenBy(r => r.Offset).ToArray();
        _consumer = new(DateTimeOffset.UtcNow);
        _consumer.Assign(
            Enumerable.Range(0, 3).Select(p => new MessageContractPartitionBounds(p, 0, 0)).ToArray()
        );
        Apply(_records, 0, 4);
    }

    [Test]
    public void It_retains_all_live_keys_across_equal_version_rebuild_replay_and_compacted_gaps()
    {
        Apply(_records.Select(r => r with { Offset = r.Offset + 4 }).ToArray(), 4, 9);
        CdcRebuildAssertions.AssertConsumer(_consumer, _expected);
        _consumer.DurableNextOffsets.Values.Should().OnlyContain(offset => offset == 9);
    }

    [Test]
    public void It_allows_a_scan_without_replays_and_preserves_state()
    {
        Apply([], 4, 9);
        CdcRebuildAssertions.AssertConsumer(_consumer, _expected);
    }

    [Test]
    public void It_rejects_a_transient_tombstone_even_when_followed_by_repopulation()
    {
        var first = _records[0];
        Action apply = () =>
            Apply([first with { Offset = 4, Value = new(true, []) }, first with { Offset = 5 }], 4, 9);
        apply.Should().Throw<AssertionException>();
    }

    [Test]
    public void It_rejects_an_unexpected_key_instead_of_filtering_it_out()
    {
        Action apply = () =>
            Apply(
                [
                    _records[0] with
                    {
                        Offset = 4,
                        Key = new(false, Encoding.UTF8.GetBytes(Guid.NewGuid().ToString("D"))),
                    },
                ],
                4,
                9
            );
        apply.Should().Throw<AssertionException>();
    }

    [Test]
    public void It_rejects_a_known_key_on_the_wrong_partition()
    {
        Action apply = () =>
            Apply([_records[0] with { Offset = 4, Partition = (_records[0].Partition + 1) % 3 }], 4, 9);
        apply.Should().Throw<AssertionException>();
    }

    [Test]
    public void It_rejects_unequal_bytes_at_an_equal_version_even_with_equivalent_json()
    {
        var first = _records[0];
        Action apply = () =>
            Apply(
                [
                    first with
                    {
                        Offset = 4,
                        Value = new(
                            false,
                            Encoding.UTF8.GetBytes(" " + Encoding.UTF8.GetString(first.Value.Bytes))
                        ),
                    },
                ],
                4,
                9
            );
        apply.Should().Throw<AssertionException>();
    }

    private void Apply(MessageContractKafkaRecord[] records, long start, long end) =>
        CdcCrudAssertions.ApplyPublicScan(
            _binding,
            _expected,
            new(
                records,
                Enumerable
                    .Range(0, 3)
                    .Select(p => new MessageContractKafkaBoundary(_binding.TopicName, p, start, end))
                    .ToArray()
            ),
            _consumer,
            false
        );
}

[TestFixture(CdcProvider.Postgresql)]
[TestFixture(CdcProvider.SqlServer)]
public class Given_CdcRebuildOffsets(CdcProvider provider)
{
    [TestCase(false)]
    [TestCase(true)]
    public void It_accepts_advanced_offsets_with_the_same_source_partition(bool idle)
    {
        CdcRebuildAssertions.AssertOffsets(provider, Offset(10, idle), Offset(11, idle));
    }

    [Test]
    public void It_rejects_regressed_offsets()
    {
        Action assert = () => CdcRebuildAssertions.AssertOffsets(provider, Offset(10), Offset(9));
        assert.Should().Throw<AssertionException>();
    }

    [Test]
    public void It_rejects_replaced_source_partitions()
    {
        Action assert = () =>
            CdcRebuildAssertions.AssertOffsets(provider, Offset(10), Offset(11, hash: "other"));
        assert.Should().Throw<AssertionException>();
    }

    [TestCase(CdcConnectOffsetState.Missing)]
    [TestCase(CdcConnectOffsetState.Snapshot)]
    public void It_rejects_missing_or_reset_offsets(CdcConnectOffsetState state)
    {
        Action assert = () =>
            CdcRebuildAssertions.AssertOffsets(provider, Offset(10), Offset(11, state: state));
        assert.Should().Throw<AssertionException>();
    }

    private static CdcConnectOffsetEvidence Offset(
        long position,
        bool idle = false,
        string hash = "same",
        CdcConnectOffsetState state = CdcConnectOffsetState.Streaming
    ) =>
        new(
            state,
            hash,
            new(CdcConnectorOffsetMatchResult.Exact, false, false, position),
            new(
                CdcConnectorOffsetMatchResult.Exact,
                false,
                false,
                $"00000001:00000001:{position:x4}",
                idle ? "NULL" : "00000001:00000001:0001",
                idle ? 0 : 2
            )
        );
}
