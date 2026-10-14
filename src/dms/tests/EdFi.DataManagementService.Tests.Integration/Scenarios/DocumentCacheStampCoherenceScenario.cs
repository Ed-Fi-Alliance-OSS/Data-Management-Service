// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using System.Net;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend;
using EdFi.DataManagementService.Core.DocumentCache;
using EdFi.DataManagementService.Tests.Integration.Doubles;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using static EdFi.DataManagementService.Tests.Integration.Scenarios.AuthoritativeResponseMetadataScenario;

namespace EdFi.DataManagementService.Tests.Integration.Scenarios;

internal static class DocumentCacheStampCoherenceScenario
{
    public static void Configure(IServiceCollection services, string connectionString, bool postgresql)
    {
        RecordHydrationCommands(services, connectionString, postgresql);
        services.AddSingleton<Boundary>();
        services.Replace(
            ServiceDescriptor.Scoped<IDocumentCacheSourceMetadataReader>(provider => new GatedSourceReader(
                ActivatorUtilities.CreateInstance<DocumentCacheSourceMetadataReader>(provider),
                provider.GetRequiredService<Boundary>()
            ))
        );
        services.Replace(
            ServiceDescriptor.Scoped<IDocumentCacheMaterializer>(provider => new GatedMaterializer(
                ActivatorUtilities.CreateInstance<DocumentCacheMaterializer>(provider),
                provider.GetRequiredService<Boundary>()
            ))
        );
    }

    public static async Task RunAsync(
        ApiIntegrationHarness harness,
        bool descriptor,
        bool restamp,
        bool publication
    )
    {
        await ExecuteAsync(
            harness,
            """
            UPDATE "dms"."DocumentCacheState" SET "ProjectionLifecycleState" = 'Tracking' WHERE "StateId" = 1;
            """
        );
        var supervisor = harness.Services.GetRequiredService<IDocumentCacheProjectionSupervisor>();
        await supervisor.RefreshAsync(DocumentCacheTargetRefreshReason.SupervisorTriggered);
        var target = supervisor.CurrentTargetContexts.Should().ContainSingle().Subject;
        var processor = harness.Services.GetRequiredService<IDocumentCacheProjectionDrainPageProcessor>();
        Resource resource = await CreateAsync(harness, descriptor);
        Resource neighbor = await CreateAsync(harness, descriptor);
        await BackdateAsync(harness, resource);
        await BackdateAsync(harness, neighbor, 1);
        Stamp before = await ReadStampAsync(harness, resource);
        Stamp neighborBefore = await ReadStampAsync(harness, neighbor);
        var boundary = harness.Services.GetRequiredService<Boundary>();
        boundary.DocumentId = before.DocumentId;
        boundary.Publication = publication;
        boundary.Armed = true;
        boundary.Gate.Arm();
        using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        Task<DocumentCacheProjectionDrainPageResult> pending = Process();
        Stamp after = null!;
        try
        {
            Task arrived = await Task.WhenAny(boundary.Gate.Arrived, pending).WaitAsync(watchdog.Token);
            arrived.Should().BeSameAs(boundary.Gate.Arrived, "projection must reach the selected boundary");
            await TestContext.Out.WriteLineAsync(
                $"Projection boundary: descriptor={descriptor}; restamp={restamp}; publication={publication}; before={before.Version}/{before.ModifiedAt:O}"
            );
            if (restamp)
            {
                await RestampAsync(harness, resource, before);
            }
            else
            {
                resource.Body[descriptor ? "shortDescription" : "displayName"] =
                    "Committed during projection";
                JsonObject payload = (JsonObject)resource.Body.DeepClone();
                payload["id"] = resource.Uuid.ToString();
                using HttpResponseMessage changed = await harness.HttpClient.PutAsync(
                    resource.Path,
                    new StringContent(payload.ToJsonString(), System.Text.Encoding.UTF8, "application/json")
                );
                changed
                    .StatusCode.Should()
                    .Be(HttpStatusCode.NoContent, await changed.Content.ReadAsStringAsync());
            }
            after = await ReadStampAsync(harness, resource);
            after.Version.Should().BeGreaterThan(before.Version);
            after.ModifiedAt.Should().BeAfter(before.ModifiedAt);
            (await ReadStampAsync(harness, neighbor)).Should().Be(neighborBefore);
            await TestContext.Out.WriteLineAsync(
                $"Committed before release: {after.Version}/{after.ModifiedAt:O}"
            );
            await using (var committedWork = harness.DbConnection.CreateCommand())
            {
                committedWork.CommandText =
                    $"SELECT \"RequiredContentVersion\" FROM \"dms\".\"DocumentProjectionWork\" WHERE \"DocumentId\" = {after.DocumentId};";
                Convert
                    .ToInt64(await committedWork.ExecuteScalarAsync())
                    .Should()
                    .Be(
                        after.Version,
                        "the committed write/restamp must advance durable work before any direct fill"
                    );
            }
            await AssertResponseAsync(harness, resource, after);
        }
        finally
        {
            boundary.Gate.Release();
            await pending.WaitAsync(watchdog.Token);
        }
        (await pending).AdministrativeFailure.Should().BeNull();
        (await pending).DocumentScopedFailureCount.Should().Be(0);
        var heldResult = await boundary.Materialized.Task;
        if (publication)
        {
            var heldCandidate = heldResult
                .Should()
                .BeOfType<DocumentCacheMaterializationResult.Success>()
                .Subject.Candidate;
            heldCandidate.ContentVersion.Should().Be(before.Version);
            heldCandidate.LastModifiedAt.Should().Be(before.ModifiedAt);
        }
        else
        {
            heldResult
                .Should()
                .BeSameAs(DocumentCacheMaterializationResult.SourceChangedDuringHydration.Instance);
        }
        for (int page = 0; page < 8; page++)
        {
            var result = await Process();
            result.DocumentScopedFailureCount.Should().Be(0);
            result.AdministrativeFailure.Should().BeNull();
            if (result.Outcome == DocumentCacheProjectionDrainPageOutcome.NoEligibleWork)
            {
                break;
            }
        }
        boundary.Hits.Should().Be(1);
        await AssertCacheAsync(harness, resource, after);
        await AssertCacheAsync(harness, neighbor, neighborBefore);
        await AssertResponseAsync(harness, resource, after);
        await AssertResponseAsync(harness, neighbor, neighborBefore);
        using HttpResponseMessage noOp = await PutAsync(
            harness,
            resource,
            (await ReadResponseAsync(harness, resource.Path))["_etag"]!.GetValue<string>()
        );
        noOp.StatusCode.Should().Be(HttpStatusCode.NoContent, await noOp.Content.ReadAsStringAsync());
        (await ReadStampAsync(harness, resource)).Should().Be(after);
        await using var work = harness.DbConnection.CreateCommand();
        work.CommandText = "SELECT COUNT(*) FROM \"dms\".\"DocumentProjectionWork\";";
        Convert
            .ToInt32(await work.ExecuteScalarAsync())
            .Should()
            .Be(0, "drain acknowledges latest work and no-op creates none");

        Task<DocumentCacheProjectionDrainPageResult> Process() =>
            processor.ProcessPageAsync(
                new DocumentCacheProjectionDrainPageRequest(
                    target,
                    DocumentCacheProjectionDrainInvocationKind.Ordinary
                ),
                watchdog.Token
            );
    }

    private static async Task AssertResponseAsync(
        ApiIntegrationHarness harness,
        Resource resource,
        Stamp stamp
    )
    {
        JsonObject body = await ReadResponseAsync(harness, resource.Path);
        AssertBody(body, resource, stamp);
        body["_etag"]!.GetValue<string>().Should().StartWith($"{stamp.Version}-");
        using HttpResponseMessage response = await harness.HttpClient.GetAsync(resource.Endpoint);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        JsonObject item = JsonNode
            .Parse(await response.Content.ReadAsStringAsync())!
            .AsArray()
            .Select(node => node!.AsObject())
            .Single(node => node["id"]!.GetValue<string>() == resource.Uuid.ToString());
        JsonNode
            .DeepEquals(item, body)
            .Should()
            .BeTrue("GET-by-id and GET-many must expose the same committed tuple");
    }

    private static void AssertBody(JsonObject body, Resource resource, Stamp stamp)
    {
        body["_lastModifiedDate"]!
            .GetValue<string>()
            .Should()
            .Be(LastModifiedDateFormatter.Format(stamp.ModifiedAt));
        foreach (var (field, value) in resource.Body)
        {
            if (field != "id")
            {
                JsonNode
                    .DeepEquals(body[field], value)
                    .Should()
                    .BeTrue($"canonical {field} must accompany its stamp");
            }
        }
    }

    private static async Task<JsonObject> ReadResponseAsync(ApiIntegrationHarness harness, string path)
    {
        using HttpResponseMessage response = await harness.HttpClient.GetAsync(path);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();
    }

    private static async Task AssertCacheAsync(ApiIntegrationHarness harness, Resource resource, Stamp stamp)
    {
        await using var command = harness.DbConnection.CreateCommand();
        command.CommandText = $"""
            SELECT "ContentVersion", "LastModifiedAt", "DocumentJson", "StreamEtag", "EffectiveSchemaHash"
            FROM "dms"."DocumentCache" CROSS JOIN "dms"."EffectiveSchema" WHERE "DocumentId" = {stamp.DocumentId};
            """;
        await using var reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();
        reader.GetInt64(0).Should().Be(stamp.Version);
        new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(1), DateTimeKind.Utc))
            .Should()
            .Be(stamp.ModifiedAt);
        AssertBody(JsonNode.Parse(reader.GetString(2))!.AsObject(), resource, stamp);
        string links = resource.Table == "Descriptor" ? "n" : "l";
        reader.GetString(3).Should().Be($"{stamp.Version}-{reader.GetString(4)[..8]}.j._.{links}.i");
        (await reader.ReadAsync()).Should().BeFalse();
    }

    private static async Task ExecuteAsync(ApiIntegrationHarness harness, string sql)
    {
        await using var command = harness.DbConnection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private sealed class Boundary
    {
        public HydrationGate Gate { get; } = new();
        public TaskCompletionSource<DocumentCacheMaterializationResult> Materialized { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public long DocumentId { get; set; }
        public bool Publication { get; set; }
        public bool Armed { get; set; }
        public int Hits;

        public async Task WaitAsync(DocumentCacheMaterializationRequest request, bool publication)
        {
            if (
                Armed
                && request.DocumentId == DocumentId
                && publication == Publication
                && Interlocked.CompareExchange(ref Hits, 1, 0) == 0
            )
            {
                await Gate.WaitIfArmedAsync().WaitAsync(request.CancellationToken);
            }
        }
    }

    private sealed class GatedSourceReader(IDocumentCacheSourceMetadataReader inner, Boundary boundary)
        : IDocumentCacheSourceMetadataReader
    {
        public Task<DocumentCacheSourceMetadataReadResult> ReadAsync(
            DocumentCacheMaterializationRequest request,
            CancellationToken cancellationToken = default
        ) => inner.ReadAsync(request, cancellationToken);

        public async Task<DocumentCacheCurrentSourceMetadataReadResult> ReadCurrentAsync(
            DocumentCacheMaterializationRequest request,
            CancellationToken cancellationToken = default
        )
        {
            await boundary.WaitAsync(request, false);
            return await inner.ReadCurrentAsync(request, cancellationToken);
        }
    }

    private sealed class GatedMaterializer(IDocumentCacheMaterializer inner, Boundary boundary)
        : IDocumentCacheMaterializer
    {
        public async Task<DocumentCacheMaterializationResult> MaterializeAsync(
            DocumentCacheMaterializationRequest request
        )
        {
            var result = await inner.MaterializeAsync(request);
            if (
                request.DocumentId == boundary.DocumentId
                && request.Purpose == DocumentCacheMaterializationPurpose.DurableWorkProjection
            )
            {
                boundary.Materialized.TrySetResult(result);
            }
            await boundary.WaitAsync(request, true);
            return result;
        }
    }
}
