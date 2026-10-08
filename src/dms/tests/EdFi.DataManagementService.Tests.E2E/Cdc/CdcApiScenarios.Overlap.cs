// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using EdFi.DataManagementService.Backend;
using EdFi.DataManagementService.Backend.Cdc.Tests.Integration;
using EdFi.DataManagementService.Backend.Cdc.Tests.Unit;
using EdFi.DataManagementService.Core.DocumentCache.Cdc;
using FluentAssertions;

namespace EdFi.DataManagementService.Tests.E2E.Cdc;

internal sealed partial class CdcApiScenarios
{
    public Task OverlapAsync(CancellationToken token) =>
        _context.InPhaseAsync(
            async (phase, ct) =>
            {
                const string scenarioId = "CDC-E2E-03";
                var binding = _context.Request.Binding;
                var scanner = new ScenarioScan(this, scenarioId);
                var consumer = new MessageContractConsumer(DateTimeOffset.UtcNow);
                consumer.Assign(await scanner.InitializeAsync(ct));
                Dictionary<long, JsonElement> expected = [];
                var body = CdcApiClient.NewStudent("OverlapN");
                await phase.Gate.WaitUntilIdleAsync(ct);
                phase.Gate.PauseForOverlap();
                try
                {
                    await phase.Runtime.StartProcessingAsync(ct);
                    Guid uuid = await _context.Api.PostAsync(CdcApiResource.Student, body, ct);
                    var materialized = await phase.Gate.WaitUntilMaterializedAsync(ct);
                    var original = (await _context.Documents.ReadSourceAsync(uuid, ct))
                        .Should()
                        .ContainSingle()
                        .Subject;
                    await AssertHeldAsync(original, materialized, ct);
                    (await _context.Documents.ReadCacheAsync(original.DocumentId, ct)).Should().BeEmpty();
                    expected.Add(
                        original.ContentVersion,
                        CdcEnvelopeExpectations.Create(CdcApiResource.Student, body, original)
                    );
                    await CheckpointAsync(scenarioId, "materialized-N", original, 0, original.ContentVersion);

                    body["firstName"] = "OverlapNext";
                    await _context.Api.PutAsync(CdcApiResource.Student, uuid, body, ct);
                    var updated = (await _context.Documents.ReadSourceAsync(uuid, ct))
                        .Should()
                        .ContainSingle()
                        .Subject;
                    updated.DocumentId.Should().Be(original.DocumentId);
                    updated.ContentVersion.Should().BeGreaterThan(original.ContentVersion);
                    var queued = (await _context.Documents.ReadWorkAsync(original.DocumentId, ct))
                        .Should()
                        .ContainSingle()
                        .Subject;
                    queued.RequiredContentVersion.Should().Be(updated.ContentVersion);
                    CdcCrudAssertions.AssertApiBody(
                        await _context.Api.GetAsync(CdcApiResource.Student, uuid, ct),
                        body,
                        uuid
                    );
                    expected.Add(
                        updated.ContentVersion,
                        CdcEnvelopeExpectations.Create(CdcApiResource.Student, body, updated)
                    );
                    await CheckpointAsync(scenarioId, "API-N+1", updated, 0, queued.RequiredContentVersion);

                    phase.Gate.ReleaseCandidate();
                    var written = await phase.Gate.WaitUntilWrittenAsync(ct);
                    written.DocumentId.Should().Be(original.DocumentId);
                    written.ContentVersion.Should().Be(original.ContentVersion);
                    written.Outcome.Should().Be(DocumentCacheWriterOutcome.StaleCandidateSuppressed);
                    await WriteDiagnosticAsync(
                        $"{scenarioId}:N-completed: candidateVersion={written.ContentVersion} outcome={written.Outcome}"
                    );
                    var next = await phase.Gate.WaitUntilPausedAsync(ct);
                    var current = (await _context.Documents.ReadSourceAsync(uuid, ct))
                        .Should()
                        .ContainSingle()
                        .Subject;
                    current.ContentVersion.Should().Be(updated.ContentVersion);
                    await AssertHeldAsync(current, next, ct);
                    var cache = await _context.Documents.ReadCacheAsync(original.DocumentId, ct);
                    CdcOverlapAssertions.AssertPendingNewerWork(
                        original.ContentVersion,
                        current.ContentVersion,
                        (await _context.Documents.ReadWorkAsync(original.DocumentId, ct)),
                        cache
                    );
                    await CheckpointAsync(
                        scenarioId,
                        "next-blocked",
                        current,
                        cache.Count == 0 ? 0 : cache[0].ContentVersion,
                        current.ContentVersion
                    );

                    // All intermediate source/work/cache assertions finish before the next attempt can enter.
                    phase.Gate.Release();
                    await AwaitPublicationAsync(CdcApiResource.Student, updated, body, ct);
                    await ScanAsync("converged", uuid, false, ct);
                    string key = uuid.ToString("D");
                    consumer.Documents.Should().ContainKey(key);
                    consumer.Documents[key].ContentVersion.Should().Be(updated.ContentVersion);
                    MessageContractJson.ShouldEqual(
                        consumer.Documents[key].Envelope,
                        expected[updated.ContentVersion]
                    );
                    await CheckpointAsync(scenarioId, "converged", updated, updated.ContentVersion);

                    // Remove this phase's API resource and consume its deletion before the next phase.
                    await _context.Api.DeleteAsync(CdcApiResource.Student, uuid, ct);
                    await ScanAsync("cleanup", uuid, true, ct);
                    consumer.Documents.Should().NotContainKey(key);
                }
                finally
                {
                    phase.Gate.Release();
                }
                return true;

                async Task ScanAsync(
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
                }
            },
            token
        );
}

internal static class CdcOverlapAssertions
{
    public static void AssertPendingNewerWork(
        long candidateVersion,
        long sourceVersion,
        IReadOnlyList<CdcProjectionWork> work,
        IReadOnlyList<CdcCacheDocument> cache
    )
    {
        sourceVersion.Should().BeGreaterThan(candidateVersion);
        work.Should().ContainSingle().Which.RequiredContentVersion.Should().Be(sourceVersion);
        cache.Should().HaveCountLessThanOrEqualTo(1);
        foreach (var row in cache)
        {
            row.ContentVersion.Should().BeLessThan(sourceVersion);
        }
    }
}
