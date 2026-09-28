// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.Cdc;
using EdFi.DataManagementService.Backend.Cdc.Tests.Integration;
using EdFi.DataManagementService.Backend.Cdc.Tests.Unit;
using EdFi.DataManagementService.Core.DocumentCache.Cdc;
using FluentAssertions;

namespace EdFi.DataManagementService.Tests.E2E.Cdc;

internal sealed partial class CdcApiScenarios
{
    public Task ExecutorRestartAsync(CancellationToken token) =>
        _context.InPhaseAsync(
            async (phase, ct) =>
            {
                const string scenarioId = "CDC-E2E-06";
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
                Dictionary<Guid, IReadOnlyDictionary<long, JsonElement>> expected = [];
                Dictionary<Guid, (CdcSourceDocument Source, JsonObject Body)> students = [];

                await phase.Gate.WaitUntilIdleAsync(ct);
                await phase.Runtime.StartProcessingAsync(ct);
                Guid initial = await CreateAsync("BeforeRestart", "before-shutdown");
                await AwaitPublicationAsync(
                    CdcApiResource.Student,
                    students[initial].Source,
                    students[initial].Body,
                    ct
                );
                await ScanAsync("before-shutdown-consumed", false, ct);
                CdcRebuildAssertions.AssertConsumer(consumer, expected);
                var oldRuntime = phase.Runtime;
                await phase.StopRuntimeAsync();
                await EventAsync("old-runtime-disposed");

                // HTTP stays running. Five distinct new Students must remain queued after successful
                // GETs, which also excludes read-through direct fill during the fully stopped interval.
                List<Guid> outage = [];
                for (int index = 0; index < 5; index++)
                {
                    outage.Add(await CreateAsync("Outage" + index, "fully-stopped"));
                }
                await UpdateAsync(outage[4], "OutageUpdated", "fully-stopped");
                foreach (var uuid in outage)
                {
                    await AssertQueuedAsync(uuid, false);
                }
                await ScanAsync("outage-work-only", false, ct);
                consumer.Documents.Keys.Should().Equal(initial.ToString("D"));

                var observations = new CdcRestartObservations(TimeSpan.FromMinutes(2));
                await phase.OpenRestartRuntimeAsync(observations, ct);
                phase.Runtime.Should().NotBeSameAs(oldRuntime);
                await phase.Runtime.InitializeAsync(ct);
                observations.Snapshot().Pages.Should().BeEmpty("initialization must not start processing");
                await EventAsync("replacement-initialized");
                await phase.Runtime.StartProcessingAsync(ct);
                await observations.WaitUntilHeldAsync(ct);
                try
                {
                    var held = observations.Snapshot();
                    held.Pages.Should().HaveCount(2);
                    held.Pages.Should().OnlyContain(p => p.PageSize == 2 && p.Items.Count == 2);
                    // A second read alone is not proof of a successful first page. Observe actual
                    // acknowledgement/cache state for the first two selected outage documents.
                    foreach (var item in held.Pages[0].Items)
                    {
                        var student = students.Values.Single(s => s.Source.DocumentId == item.DocumentId);
                        await AwaitPublicationAsync(CdcApiResource.Student, student.Source, student.Body, ct);
                    }
                    await EventAsync("second-work-page-held");
                    await UpdateAsync(initial, "DuringDrain", "draining");
                    await AssertQueuedAsync(initial, true);
                    Guid duringDrain = await CreateAsync("DrainCreate", "draining");
                    await AssertQueuedAsync(duringDrain, false);
                    observations
                        .IsHeld.Should()
                        .BeTrue("API operations must finish within the held drain interval");
                }
                finally
                {
                    observations.Release();
                }

                foreach (var student in students.Values)
                {
                    await AwaitPublicationAsync(CdcApiResource.Student, student.Source, student.Body, ct);
                    await CheckpointAsync(
                        scenarioId,
                        "converged-and-acknowledged",
                        student.Source,
                        student.Source.ContentVersion
                    );
                }
                await phase.Gate.WaitUntilIdleAsync(ct);
                var execution = observations.Snapshot();
                foreach (var page in execution.Pages)
                {
                    await WriteDiagnosticAsync(
                        $"{scenarioId}:work-page: size={page.PageSize} selected={page.Items.Count}"
                    );
                }
                await WriteDiagnosticAsync(
                    $"{scenarioId}:replacement-operations: baselineBoundaries={execution.BaselineBoundaries} baselinePages={execution.BaselinePages} inventoryPages={execution.InventoryPages} dropped={execution.DroppedPages}"
                );
                CdcRestartAssertions.AssertRecovery(
                    execution,
                    students.Values.Select(s => s.Source.DocumentId).ToArray()
                );
                await ScanAsync("recovery-consumed", false, ct);
                CdcRebuildAssertions.AssertConsumer(
                    consumer,
                    expected.ToDictionary(
                        pair => pair.Key,
                        pair =>
                            (IReadOnlyDictionary<long, JsonElement>)
                                new Dictionary<long, JsonElement>
                                {
                                    [students[pair.Key].Source.ContentVersion] = pair.Value[
                                        students[pair.Key].Source.ContentVersion
                                    ],
                                }
                    )
                );
                // Phase/controller operations resolve the current replacement, never the old runtime.
                var status = (await phase.StatusAsync(ct)).Targets.Should().ContainSingle().Subject.Status;
                status.SourceHistory.Continuity.Should().Be(CdcSourceHistoryContinuity.Healthy);
                status.SourceHistory.IncidentLatched.Should().BeFalse();
                status.Binding.State.Should().Be(CdcComponentState.Satisfied);
                CdcRebuildAssertions.AssertTracking(
                    await phase.Runtime.ObserveEstablishedDatabaseAsync(ct),
                    binding
                );
                await _context.Provider.AssertCaptureInventoryAsync(ct);
                await _context.Fences.AssertConnectorIncludeListAsync(ct);

                foreach (var uuid in students.Keys)
                {
                    await _context.Api.DeleteAsync(CdcApiResource.Student, uuid, ct);
                }
                await ScanAsync("cleanup-deletes-consumed", true, ct);
                consumer.Documents.Should().BeEmpty();
                return true;

                async Task<Guid> CreateAsync(string name, string interval)
                {
                    var body = CdcApiClient.NewStudent(name);
                    Guid uuid = await _context.Api.PostAsync(CdcApiResource.Student, body, ct);
                    await ObserveApiAsync(uuid, body, interval);
                    return uuid;
                }

                async Task UpdateAsync(Guid uuid, string name, string interval)
                {
                    var body = (JsonObject)students[uuid].Body.DeepClone();
                    body["firstName"] = name;
                    long previous = students[uuid].Source.ContentVersion;
                    await _context.Api.PutAsync(CdcApiResource.Student, uuid, body, ct);
                    await ObserveApiAsync(uuid, body, interval);
                    students[uuid].Source.ContentVersion.Should().BeGreaterThan(previous);
                }

                async Task ObserveApiAsync(Guid uuid, JsonObject body, string interval)
                {
                    CdcCrudAssertions.AssertApiBody(
                        await _context.Api.GetAsync(CdcApiResource.Student, uuid, ct),
                        body,
                        uuid
                    );
                    var source = (await _context.Documents.ReadSourceAsync(uuid, ct))
                        .Should()
                        .ContainSingle()
                        .Subject;
                    students[uuid] = (source, body);
                    Dictionary<long, JsonElement> versions = expected.TryGetValue(uuid, out var existing)
                        ? new(existing)
                        : [];
                    versions.Add(
                        source.ContentVersion,
                        CdcEnvelopeExpectations.Create(CdcApiResource.Student, body, source)
                    );
                    expected[uuid] = versions;
                    await EventAsync("api-write-and-read-" + interval);
                }

                async Task AssertQueuedAsync(Guid uuid, bool staleCache)
                {
                    var source = students[uuid].Source;
                    var work = (await _context.Documents.ReadWorkAsync(source.DocumentId, ct))
                        .Should()
                        .ContainSingle()
                        .Subject;
                    work.RequiredContentVersion.Should().Be(source.ContentVersion);
                    var cache = await _context.Documents.ReadCacheAsync(source.DocumentId, ct);
                    if (staleCache)
                    {
                        cache
                            .Should()
                            .ContainSingle()
                            .Which.ContentVersion.Should()
                            .BeLessThan(source.ContentVersion);
                    }
                    else
                    {
                        cache.Should().BeEmpty();
                    }
                    await CheckpointAsync(
                        scenarioId,
                        "queued-without-direct-fill",
                        source,
                        cache.Count == 0 ? 0 : cache[0].ContentVersion,
                        work.RequiredContentVersion
                    );
                }

                Task EventAsync(string name) =>
                    WriteDiagnosticAsync($"{scenarioId}:{name}: utc={DateTimeOffset.UtcNow:O}");

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

                async Task ScanAsync(string checkpoint, bool allowTombstones, CancellationToken cancellation)
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
                    CdcCrudAssertions.ApplyPublicScan(
                        binding,
                        expected,
                        new(
                            scan.Records.Where(r => r.Topic == binding.TopicName).ToArray(),
                            scan.CompletedBoundaries.Where(b => b.Topic == binding.TopicName).ToArray()
                        ),
                        consumer,
                        allowTombstones
                    );
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
                }
            },
            token
        );
}

internal static class CdcRestartAssertions
{
    public static void AssertRecovery(CdcRestartSnapshot observation, IReadOnlyCollection<long> documentIds)
    {
        observation.DroppedPages.Should().Be(0);
        observation.BaselineBoundaries.Should().Be(0, "restart must not capture a canonical baseline");
        observation.BaselinePages.Should().Be(0, "restart must drain retained work without baseline seeding");
        observation.InventoryPages.Should().Be(0, "restart must not scrub canonical/cache inventory");
        observation.Pages.Should().HaveCountGreaterThanOrEqualTo(4);
        observation
            .Pages.Should()
            .OnlyContain(page => page.PageSize == 2 && page.Items.Count > 0 && page.Items.Count <= 2);
        observation
            .Pages.SelectMany(page => page.Items)
            .Select(item => item.DocumentId)
            .Distinct()
            .Should()
            .BeEquivalentTo(documentIds);
    }
}
