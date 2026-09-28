// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using EdFi.DataManagementService.Backend.Cdc.Tests.Integration;
using EdFi.DataManagementService.Backend.Cdc.Tests.Unit;
using EdFi.DataManagementService.Core.DocumentCache.Cdc;
using FluentAssertions;

namespace EdFi.DataManagementService.Tests.E2E.Cdc;

internal sealed partial class CdcApiScenarios
{
    public Task DeleteBeforeProjectionAsync(CancellationToken token) =>
        _context.InPhaseAsync(
            async (phase, ct) =>
            {
                const string scenarioId = "CDC-E2E-04";
                var binding = _context.Request.Binding;
                string progressTopic = CdcArtifactNameGenerator
                    .RecoverFromBinding(binding)
                    .Inventory!.ProgressTopicName;
                var positions = await CaptureAsync(ct);
                var consumer = new MessageContractConsumer(DateTimeOffset.UtcNow);
                consumer.Assign(
                    positions
                        .Where(b => b.Topic == binding.TopicName)
                        .Select(b => new MessageContractPartitionBounds(
                            b.Partition,
                            b.EndOffset,
                            b.EndOffset
                        ))
                        .ToArray()
                );
                await phase.Gate.WaitUntilIdleAsync(ct);
                phase.Gate.Pause();
                try
                {
                    await phase.Runtime.StartProcessingAsync(ct);
                    var body = CdcApiClient.NewStudent("DeleteBeforeProjection");
                    Guid uuid = await _context.Api.PostAsync(CdcApiResource.Student, body, ct);
                    var held = await phase.Gate.WaitUntilPausedAsync(ct);
                    var created = (await _context.Documents.ReadSourceAsync(uuid, ct))
                        .Should()
                        .ContainSingle()
                        .Subject;
                    await AssertHeldAsync(created, held, ct);
                    (await _context.Documents.ReadCacheAsync(created.DocumentId, ct)).Should().BeEmpty();
                    CdcCrudAssertions.AssertApiBody(
                        await _context.Api.GetAsync(CdcApiResource.Student, uuid, ct),
                        body,
                        uuid
                    );
                    await CheckpointAsync(scenarioId, "held-create", created, 0, created.ContentVersion);

                    await _context.Api.DeleteAsync(CdcApiResource.Student, uuid, ct);
                    await AssertDeletedAsync();
                    await CheckpointAsync(scenarioId, "deleted-while-held", created, 0);

                    // Consumption and its assertions must finish while the original work item is held.
                    // Keeping an empty expected-upsert set rejects even an upsert followed by a delete.
                    await CdcDeleteBeforeProjectionAssertions.ConsumeThenResumeAsync(
                        phase.Gate,
                        async cancellation =>
                        {
                            var deleted = await ScanAsync("tombstone-consumed-while-held", cancellation);
                            CdcDeleteBeforeProjectionAssertions.ApplyScan(
                                binding,
                                uuid,
                                deleted,
                                consumer,
                                requireTombstone: true
                            );
                            var tombstone = deleted.Records[0];
                            await WriteDiagnosticAsync(
                                $"{scenarioId}:tombstone-consumed-while-held: partition={tombstone.Partition} offset={tombstone.Offset}"
                            );
                        },
                        ct
                    );
                    await WriteDiagnosticAsync($"{scenarioId}:projector-released-and-drained");

                    // Drain the held production call before fencing source progress and Kafka ends.
                    // This detects transient resurrection as well as a cache row left behind.
                    var resumed = await ScanAsync("after-projector-drain", ct);
                    CdcDeleteBeforeProjectionAssertions.ApplyScan(
                        binding,
                        uuid,
                        resumed,
                        consumer,
                        requireTombstone: false
                    );
                    await AssertDeletedAsync();
                    await CheckpointAsync(scenarioId, "absent-after-fenced-drain", created, 0);

                    async Task AssertDeletedAsync()
                    {
                        (await _context.Documents.ReadSourceAsync(uuid, ct)).Should().BeEmpty();
                        (await _context.Documents.ReadCacheAsync(created.DocumentId, ct)).Should().BeEmpty();
                        (await _context.Documents.ReadWorkAsync(created.DocumentId, ct)).Should().BeEmpty();
                    }
                }
                finally
                {
                    phase.Gate.Release();
                }
                return true;

                async Task<IReadOnlyList<MessageContractKafkaBoundary>> CaptureAsync(
                    CancellationToken cancellation
                )
                {
                    var publicBounds = await _context.Kafka.CaptureKafkaBoundariesAsync(
                        binding.TopicName,
                        cancellation
                    );
                    var progressBounds = await _context.Kafka.CaptureKafkaBoundariesAsync(
                        progressTopic,
                        cancellation
                    );
                    return [.. publicBounds, .. progressBounds];
                }

                async Task<MessageContractKafkaScan> ScanAsync(
                    string checkpoint,
                    CancellationToken cancellation
                )
                {
                    string label = scenarioId + ":" + checkpoint;
                    await WriteDiagnosticAsync($"{label}:provider-fence-started");
                    if (binding.Provider == CdcProvider.Postgresql)
                    {
                        await _context.Fences.FencePostgresqlSourceAsync(label, cancellation);
                    }
                    else
                    {
                        await _context.Fences.FenceSqlServerSourceAsync(label, cancellation);
                    }
                    await WriteDiagnosticAsync($"{label}:provider-fence-completed");
                    var ends = await CaptureAsync(cancellation);
                    var bounds = ends.Select(end =>
                            end with
                            {
                                StartOffset = positions
                                    .Single(p => p.Topic == end.Topic && p.Partition == end.Partition)
                                    .EndOffset,
                            }
                        )
                        .ToArray();
                    var scan = await _context.Kafka.ConsumeThroughAsync(bounds, cancellation);
                    foreach (var record in scan.Records.Where(r => r.Topic == progressTopic))
                    {
                        MessageContractProgressAssertions.AssertHeartbeat(binding, record);
                    }
                    await _context.AssertTopicInventoryAsync(cancellation);
                    positions = scan.CompletedBoundaries;
                    foreach (var bound in positions)
                    {
                        await WriteDiagnosticAsync(
                            $"{label}: {(bound.Topic == binding.TopicName ? "public" : "progress")} partition={bound.Partition} start={bound.StartOffset} end={bound.EndOffset}"
                        );
                    }
                    return new(
                        scan.Records.Where(r => r.Topic == binding.TopicName).ToArray(),
                        scan.CompletedBoundaries.Where(b => b.Topic == binding.TopicName).ToArray()
                    );
                }
            },
            token
        );
}

internal static class CdcDeleteBeforeProjectionAssertions
{
    public static void ApplyScan(
        CdcBinding binding,
        Guid uuid,
        MessageContractKafkaScan scan,
        MessageContractConsumer consumer,
        bool requireTombstone
    )
    {
        CdcCrudAssertions.ApplyPublicScan(
            binding,
            uuid,
            new Dictionary<long, JsonElement>(),
            scan,
            consumer,
            allowTombstones: true
        );
        if (requireTombstone)
        {
            scan.Records.Should().NotBeEmpty("canonical deletion must publish before projection resumes");
        }
        consumer.Documents.Should().NotContainKey(uuid.ToString("D"));
    }

    // A narrow ordering seam: failed or incomplete deletion evidence cannot release projection.
    public static async Task ConsumeThenResumeAsync(
        CdcProjectionGate gate,
        Func<CancellationToken, Task> consumeDeletion,
        CancellationToken token
    )
    {
        await consumeDeletion(token);
        token.ThrowIfCancellationRequested();
        await gate.ResumeHeldProcessingAsync(token);
    }
}
