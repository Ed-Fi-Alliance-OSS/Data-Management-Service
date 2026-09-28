// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.Cdc.Tests.Integration;
using EdFi.DataManagementService.Backend.Cdc.Tests.Unit;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.DocumentCache.Cdc;
using FluentAssertions;

namespace EdFi.DataManagementService.Tests.E2E.Cdc;

internal sealed partial class CdcApiScenarios
{
    // Both API paths use the same held-work, publication, consumption and deletion sequence.
    private Task CrudAsync(
        CdcApiResource resource,
        JsonObject body,
        string updatedField,
        string scenarioId,
        CancellationToken token
    ) =>
        _context.InPhaseAsync(
            async (phase, ct) =>
            {
                var binding = _context.Request.Binding;
                var scanner = new ScenarioScan(this, scenarioId);
                var consumer = new MessageContractConsumer(DateTimeOffset.UtcNow);
                consumer.Assign(await scanner.InitializeAsync(ct));
                Dictionary<long, JsonElement> expected = [];

                await _context.Provider.AssertCaptureInventoryAsync(ct);
                await _context.Fences.AssertConnectorIncludeListAsync(ct);
                await phase.Gate.WaitUntilIdleAsync(ct);
                phase.Gate.Pause();
                try
                {
                    await CdcAttachmentReadiness.StartAndWaitAsync(
                        phase.Runtime,
                        phase.StatusAsync,
                        _context.Request.Timing.WaitTimeout,
                        _context.Request.Timing.PollInterval,
                        ct
                    );
                    Guid uuid = await _context.Api.PostAsync(resource, body, ct);
                    var held = await phase.Gate.WaitUntilPausedAsync(ct);
                    var created = (await _context.Documents.ReadSourceAsync(uuid, ct))
                        .Should()
                        .ContainSingle()
                        .Subject;
                    created.DocumentUuid.Should().Be(uuid);
                    created.EffectiveSchemaHash.Should().Be(_context.EffectiveSchemaHash);
                    var identity = await phase.Runtime.ObserveEstablishedDatabaseAsync(ct);
                    CdcCrudAssertions.AssertRuntimeTarget(binding, identity.TargetKey);
                    identity.PhysicalSourceFingerprint.Should().Be(binding.PhysicalSourceFingerprint);
                    await AssertHeldAsync(created, held, ct);
                    (await _context.Documents.ReadCacheAsync(created.DocumentId, ct)).Should().BeEmpty();
                    CdcCrudAssertions.AssertApiBody(
                        await _context.Api.GetAsync(resource, uuid, ct),
                        body,
                        uuid
                    );
                    // Fence while work is held: canonical inserts/work alone must not publish public state.
                    await ScanAsync("held-create", uuid, false, ct);
                    consumer.Documents.Should().BeEmpty();
                    await CheckpointAsync(
                        scenarioId,
                        "admitted-identity-held-create",
                        created,
                        0,
                        created.ContentVersion
                    );

                    expected.Add(
                        created.ContentVersion,
                        CdcEnvelopeExpectations.Create(resource, body, created)
                    );
                    phase.Gate.Release();
                    await AwaitPublicationAsync(resource, created, body, ct);
                    await ScanAsync("create-consumed", uuid, false, ct);
                    AssertConsumed(created);
                    await CheckpointAsync(scenarioId, "create-consumed", created, created.ContentVersion);

                    // Publication acknowledgement can precede return from the item processor. Drain that
                    // call before rearming; no new work is submitted until the selected gate is armed.
                    await phase.Gate.WaitUntilIdleAsync(ct);
                    phase.Gate.Pause(created.DocumentId);
                    body[updatedField] = "Updated";
                    await _context.Api.PutAsync(resource, uuid, body, ct);
                    held = await phase.Gate.WaitUntilPausedAsync(ct);
                    var updated = (await _context.Documents.ReadSourceAsync(uuid, ct))
                        .Should()
                        .ContainSingle()
                        .Subject;
                    updated.DocumentId.Should().Be(created.DocumentId);
                    updated.ResourceKeyId.Should().Be(created.ResourceKeyId);
                    updated.EffectiveSchemaHash.Should().Be(created.EffectiveSchemaHash);
                    updated.ContentVersion.Should().BeGreaterThan(created.ContentVersion);
                    updated.ContentLastModifiedAt.Should().BeOnOrAfter(created.ContentLastModifiedAt);
                    await AssertHeldAsync(updated, held, ct);
                    var oldCache = (await _context.Documents.ReadCacheAsync(created.DocumentId, ct))
                        .Should()
                        .ContainSingle()
                        .Subject;
                    CdcCrudAssertions.AssertCache(oldCache, created, expected[created.ContentVersion]);
                    CdcCrudAssertions.AssertApiBody(
                        await _context.Api.GetAsync(resource, uuid, ct),
                        body,
                        uuid
                    );
                    await ScanAsync("held-update", uuid, false, ct);
                    AssertConsumed(created);
                    await CheckpointAsync(
                        scenarioId,
                        "held-update",
                        updated,
                        created.ContentVersion,
                        updated.ContentVersion
                    );

                    expected.Add(
                        updated.ContentVersion,
                        CdcEnvelopeExpectations.Create(resource, body, updated)
                    );
                    phase.Gate.Release();
                    await AwaitPublicationAsync(resource, updated, body, ct);
                    await ScanAsync("update-consumed", uuid, false, ct);
                    AssertConsumed(updated);
                    await CheckpointAsync(scenarioId, "update-consumed", updated, updated.ContentVersion);

                    await _context.Api.DeleteAsync(resource, uuid, ct);
                    (await _context.Documents.ReadSourceAsync(uuid, ct)).Should().BeEmpty();
                    (await _context.Documents.ReadCacheAsync(updated.DocumentId, ct)).Should().BeEmpty();
                    (await _context.Documents.ReadWorkAsync(updated.DocumentId, ct)).Should().BeEmpty();
                    var deleted = await ScanAsync("delete-consumed", uuid, true, ct);
                    deleted
                        .Records.Any(r => r.Value.IsNull)
                        .Should()
                        .BeTrue("deletion must publish a record-level null");
                    consumer.Documents.Should().NotContainKey(uuid.ToString("D"));
                    await CheckpointAsync(scenarioId, "delete-consumed", updated, 0);
                }
                finally
                {
                    phase.Gate.Release();
                }
                return true;

                void AssertConsumed(CdcSourceDocument source)
                {
                    string key = source.DocumentUuid.ToString("D");
                    consumer.Documents.Should().ContainKey(key);
                    consumer.Documents[key].ContentVersion.Should().Be(source.ContentVersion);
                    MessageContractJson.ShouldEqual(
                        consumer.Documents[key].Envelope,
                        expected[source.ContentVersion]
                    );
                }

                async Task<MessageContractKafkaScan> ScanAsync(
                    string checkpoint,
                    Guid uuid,
                    bool allowTombstones,
                    CancellationToken cancellation
                )
                {
                    var publicScan = await scanner.ScanAsync(checkpoint, cancellation);
                    CdcCrudAssertions.ApplyPublicScan(
                        binding,
                        uuid,
                        expected,
                        publicScan,
                        consumer,
                        allowTombstones
                    );
                    return publicScan;
                }
            },
            token
        );

    private async Task AssertHeldAsync(
        CdcSourceDocument source,
        CdcProjectionPauseObservation held,
        CancellationToken token
    )
    {
        held.DocumentId.Should().Be(source.DocumentId);
        held.RequiredContentVersion.Should().Be(source.ContentVersion);
        var work = (await _context.Documents.ReadWorkAsync(source.DocumentId, token))
            .Should()
            .ContainSingle()
            .Subject;
        work.RequiredContentVersion.Should().Be(source.ContentVersion);
        work.LastEnqueuedAt.Should().BeOnOrAfter(work.FirstEnqueuedAt);
    }

    private async Task AwaitPublicationAsync(
        CdcApiResource resource,
        CdcSourceDocument source,
        JsonObject body,
        CancellationToken token
    )
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        while (true)
        {
            var cache = await _context.Documents.ReadCacheAsync(source.DocumentId, timeout.Token);
            var work = await _context.Documents.ReadWorkAsync(source.DocumentId, timeout.Token);
            if (cache.Count == 1 && cache[0].ContentVersion == source.ContentVersion && work.Count == 0)
            {
                CdcCrudAssertions.AssertCache(
                    cache[0],
                    source,
                    CdcEnvelopeExpectations.Create(resource, body, source)
                );
                return;
            }
            await Task.Delay(TimeSpan.FromMilliseconds(100), timeout.Token);
        }
    }

    private Task CheckpointAsync(
        string scenarioId,
        string checkpoint,
        CdcSourceDocument source,
        long cacheVersion,
        long workVersion = 0
    ) =>
        WriteDiagnosticAsync(
            $"{scenarioId}:{checkpoint}: sourceVersion={source.ContentVersion} cacheVersion={cacheVersion} workVersion={workVersion}"
        );
}

/// <summary>Assertions shared by single-key CRUD and multi-key rebuild scans. Every public record is classified, including
/// duplicate/stale deliveries; unrelated/work-table records cannot hide behind a UUID filter.</summary>
internal static class CdcCrudAssertions
{
    public static void AssertRuntimeTarget(CdcBinding binding, DocumentCacheTargetKey target)
    {
        // E18 uses an empty default tenant; CDC bindings use the canonical "default" token.
        // Match the production admission mapping instead of treating a binding token as an E18 key.
        CdcTargetValidator
            .MapE18TenantKeyToBindingTenantKey(target.TenantKey.ToLowerInvariant())
            .Should()
            .Be(binding.TenantKey);
        target.DataStoreId.ToString(CultureInfo.InvariantCulture).Should().Be(binding.DataStoreId);
    }

    public static void AssertApiBody(JsonObject actual, JsonObject body, Guid uuid)
    {
        actual["id"]!.GetValue<string>().Should().Be(uuid.ToString("D"));
        var business = (JsonObject)actual.DeepClone();
        business.Remove("id");
        business.Remove("_etag");
        business.Remove("_lastModifiedDate");
        MessageContractJson.ShouldEqual(
            JsonSerializer.SerializeToElement(business),
            JsonSerializer.SerializeToElement(body)
        );
    }

    public static void AssertCache(CdcCacheDocument cache, CdcSourceDocument source, JsonElement envelope)
    {
        cache.DocumentUuid.Should().Be(source.DocumentUuid);
        cache.ContentVersion.Should().Be(source.ContentVersion);
        cache.LastModifiedAt.Should().BeCloseTo(source.ContentLastModifiedAt, TimeSpan.FromTicks(10));
        cache.ProjectName.Should().Be(source.ProjectName);
        cache.ResourceName.Should().Be(source.ResourceName);
        cache.ResourceVersion.Should().Be(source.ResourceVersion);
        var expected = JsonNode.Parse(envelope.GetProperty("document").GetRawText())!.AsObject();
        cache.StreamEtag.Should().Be(expected["_etag"]!.GetValue<string>());
        expected.Remove("_etag");
        MessageContractJson.ShouldEqual(
            JsonSerializer.SerializeToElement(cache.DocumentJson),
            JsonSerializer.SerializeToElement(expected)
        );
    }

    public static void ApplyPublicScan(
        CdcBinding binding,
        Guid uuid,
        IReadOnlyDictionary<long, JsonElement> expected,
        MessageContractKafkaScan scan,
        MessageContractConsumer consumer,
        bool allowTombstones
    )
    {
        ApplyPublicScan(
            binding,
            new Dictionary<Guid, IReadOnlyDictionary<long, JsonElement>> { [uuid] = expected },
            scan,
            consumer,
            allowTombstones
        );
    }

    public static void ApplyPublicScan(
        CdcBinding binding,
        IReadOnlyDictionary<Guid, IReadOnlyDictionary<long, JsonElement>> expectedByKey,
        MessageContractKafkaScan scan,
        MessageContractConsumer consumer,
        bool allowTombstones
    )
    {
        consumer.CaptureEndOffsets(scan.CompletedBoundaries.ToDictionary(b => b.Partition, b => b.EndOffset));
        foreach (var record in scan.Records)
        {
            record.Topic.Should().Be(binding.TopicName);
            record.Key.IsNull.Should().BeFalse();
            string key = Encoding.UTF8.GetString(record.Key.Bytes);
            Guid.TryParseExact(key, "D", out Guid uuid).Should().BeTrue();
            key = uuid.ToString("D");
            expectedByKey.Should().ContainKey(uuid);
            var expected = expectedByKey[uuid];
            int partition = MessageContractPartition.ForUuid(
                key,
                binding.PartitionCount,
                binding.PartitionerAlgorithm
            );
            record.Partition.Should().Be(partition);
            record.Key.Bytes.Should().Equal(Encoding.UTF8.GetBytes(key));
            record.Headers.Should().BeEmpty();
            if (record.Value.IsNull)
            {
                allowTombstones.Should().BeTrue("only canonical deletion may publish a tombstone");
                record.Value.Bytes.Should().BeEmpty();
            }
            else
            {
                using var document = JsonDocument.Parse(record.Value.Bytes);
                long version = document.RootElement.GetProperty("contentVersion").GetInt64();
                expected.Should().ContainKey(version);
                MessageContractRecordAssertions.AssertUpsert(
                    binding,
                    record,
                    key,
                    partition,
                    expected[version]
                );
            }
            consumer.Stage(
                new(
                    record.Partition,
                    record.Offset,
                    record.Key.Bytes,
                    record.Value.Bytes,
                    record.Value.IsNull
                )
            );
            consumer
                .CompleteApply(record.Partition)
                .Should()
                .NotBe(MessageContractConsumerApplyResult.ProducerContractViolation);
        }
        foreach (var bound in scan.CompletedBoundaries)
        {
            consumer.CompleteScan(bound.Partition, bound.EndOffset);
        }
    }
}
