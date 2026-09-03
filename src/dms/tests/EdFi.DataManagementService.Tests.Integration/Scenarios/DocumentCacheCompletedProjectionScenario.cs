// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend;
using EdFi.DataManagementService.Core.DocumentCache;
using EdFi.DataManagementService.Tests.Integration.Doubles;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace EdFi.DataManagementService.Tests.Integration.Scenarios;

internal static class DocumentCacheCompletedProjectionScenario
{
    private const string StudentsEndpoint = "/data/ed-fi/students";
    private const string SchoolTypeDescriptorsEndpoint = "/data/ed-fi/schoolTypeDescriptors";
    private const string ProfileRootOnlyMergeItemsEndpoint = "/data/ed-fi/profileRootOnlyMergeItems";
    private const string StandardJsonContentType = "application/json";
    private const string ForcedEnqueueFailureMessage = "DMS-1317 forced DocumentCache enqueue failure";

    public static async Task It_projects_http_created_updated_and_deleted_ordinary_resource(
        ApiIntegrationHarness harness
    )
    {
        DocumentCacheReadTelemetryRecorder recorder =
            harness.DocumentCacheReadTelemetryRecorder
            ?? throw new InvalidOperationException(
                "The completed-projection scenario requires DocumentCache read telemetry."
            );

        await SetTrackingLifecycleAsync(harness);
        await RefreshProjectionTargetsAsync(harness);

        string studentUniqueId = $"dcp-{Guid.NewGuid():N}"[..32];
        CreatedDocument createdStudent = await PostStudentAsync(harness, studentUniqueId, "Projected Create");
        DocumentMetadata createdMetadata = await ReadDocumentMetadataAsync(
            harness,
            createdStudent.DocumentUuid
        );

        (await CountProjectionWorkRowsAsync(harness, createdMetadata.DocumentId))
            .Should()
            .Be(1, "HTTP create must enqueue durable DocumentCache projection work");
        (await CountCacheRowsAsync(harness, createdMetadata.DocumentId))
            .Should()
            .Be(0, "the cache row should come from projection, not from the canonical write");

        await DrainProjectionUntilIdleAsync(harness);

        (await CountProjectionWorkRowsAsync(harness, createdMetadata.DocumentId))
            .Should()
            .Be(0, "projection must acknowledge the create work row");
        DocumentCacheRow createdCacheRow = await ReadCacheRowAsync(harness, createdMetadata.DocumentId);
        createdCacheRow
            .StreamEtag.Should()
            .Be(createdStudent.Etag, "projection must cache the caller-agnostic stream version");
        AssertStudentCacheRow(createdCacheRow, createdMetadata, studentUniqueId, "Projected Create");

        JsonObject cachedGet = await GetJsonObjectAsync(harness, createdStudent.LocationPath);
        cachedGet["studentUniqueId"]!.GetValue<string>().Should().Be(studentUniqueId);
        cachedGet["firstName"]!.GetValue<string>().Should().Be("Projected Create");
        cachedGet["_etag"]!.GetValue<string>().Should().Be(createdCacheRow.StreamEtag);
        AssertReadTelemetryContains(
            recorder,
            "RecordHit",
            "Hit",
            "GET-by-id should be served from the projected cache row"
        );

        JsonArray cachedQuery = await GetJsonArrayAsync(
            harness,
            $"{StudentsEndpoint}?offset=0&limit=1&totalCount=true",
            expectedTotalCount: 1
        );
        cachedQuery.Should().ContainSingle();
        cachedQuery[0]!["studentUniqueId"]!.GetValue<string>().Should().Be(studentUniqueId);
        cachedQuery[0]!["firstName"]!.GetValue<string>().Should().Be("Projected Create");
        AssertReadTelemetryContains(
            recorder,
            "RecordPageHit",
            "PageHit",
            "GET-many should be served from the projected cache row"
        );

        string updateEtag = await PutStudentAsync(
            harness,
            createdStudent.LocationPath,
            createdStudent.DocumentUuid,
            studentUniqueId,
            "Projected Update",
            createdCacheRow.StreamEtag
        );
        DocumentMetadata updatedMetadata = await ReadDocumentMetadataAsync(
            harness,
            createdStudent.DocumentUuid
        );
        updatedMetadata.ContentVersion.Should().BeGreaterThan(createdMetadata.ContentVersion);
        (await CountProjectionWorkRowsAsync(harness, createdMetadata.DocumentId))
            .Should()
            .Be(1, "HTTP update must enqueue durable projection work");

        DocumentCacheRow staleCacheRow = await ReadCacheRowAsync(harness, createdMetadata.DocumentId);
        staleCacheRow
            .ContentVersion.Should()
            .Be(createdCacheRow.ContentVersion, "the pre-drain cache row is stale after HTTP update");

        await DrainProjectionUntilIdleAsync(harness);

        (await CountProjectionWorkRowsAsync(harness, createdMetadata.DocumentId))
            .Should()
            .Be(0, "projection must acknowledge the update work row before read acceleration can repair it");
        DocumentCacheRow updatedCacheRow = await ReadCacheRowAsync(harness, createdMetadata.DocumentId);
        updatedCacheRow
            .StreamEtag.Should()
            .Be(updateEtag, "projection must cache the updated caller-agnostic stream version");
        AssertStudentCacheRow(updatedCacheRow, updatedMetadata, studentUniqueId, "Projected Update");

        int hitCountBeforeUpdatedGet = recorder.CountTelemetryRecords("RecordHit", "Hit");
        JsonObject updatedCachedGet = await GetJsonObjectAsync(harness, createdStudent.LocationPath);
        updatedCachedGet["firstName"]!.GetValue<string>().Should().Be("Projected Update");
        updatedCachedGet["_etag"]!.GetValue<string>().Should().Be(updatedCacheRow.StreamEtag);
        AssertReadTelemetryCountIncreased(
            recorder,
            "RecordHit",
            "Hit",
            hitCountBeforeUpdatedGet,
            "updated GET-by-id should be served from the projected cache row"
        );

        int pageHitCountBeforeUpdatedQuery = recorder.CountTelemetryRecords("RecordPageHit", "PageHit");
        JsonArray updatedCachedQuery = await GetJsonArrayAsync(
            harness,
            $"{StudentsEndpoint}?offset=0&limit=1&totalCount=true",
            expectedTotalCount: 1
        );
        updatedCachedQuery.Should().ContainSingle();
        updatedCachedQuery[0]!["studentUniqueId"]!.GetValue<string>().Should().Be(studentUniqueId);
        updatedCachedQuery[0]!["firstName"]!.GetValue<string>().Should().Be("Projected Update");
        AssertReadTelemetryCountIncreased(
            recorder,
            "RecordPageHit",
            "PageHit",
            pageHitCountBeforeUpdatedQuery,
            "updated GET-many should be served from the projected cache row"
        );

        using HttpResponseMessage deleteResponse = await harness.HttpClient.DeleteAsync(
            createdStudent.LocationPath
        );
        string deleteBody = await deleteResponse.Content.ReadAsStringAsync();
        deleteResponse.StatusCode.Should().Be(HttpStatusCode.NoContent, deleteBody);

        using HttpResponseMessage getAfterDelete = await harness.HttpClient.GetAsync(
            createdStudent.LocationPath
        );
        string getAfterDeleteBody = await getAfterDelete.Content.ReadAsStringAsync();
        getAfterDelete.StatusCode.Should().Be(HttpStatusCode.NotFound, getAfterDeleteBody);
        getAfterDeleteBody.Should().NotContain("Projected Update");

        (await CountDocumentRowsAsync(harness, createdStudent.DocumentUuid))
            .Should()
            .Be(0, "DELETE must remove the canonical document row");
        (await CountCacheRowsAsync(harness, createdMetadata.DocumentId))
            .Should()
            .Be(0, "DELETE must cascade-remove the projected cache row");
        (await CountProjectionWorkRowsAsync(harness, createdMetadata.DocumentId))
            .Should()
            .Be(0, "DELETE must not leave orphaned projection work");
    }

    public static async Task It_projects_http_created_updated_and_deleted_descriptor(
        ApiIntegrationHarness harness
    )
    {
        DocumentCacheReadTelemetryRecorder recorder =
            harness.DocumentCacheReadTelemetryRecorder
            ?? throw new InvalidOperationException(
                "The completed-projection scenario requires DocumentCache read telemetry."
            );

        await SetTrackingLifecycleAsync(harness);
        await RefreshProjectionTargetsAsync(harness);

        string suffix = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)[..12];
        string namespaceName = $"uri://ed-fi.org/SchoolTypeDescriptor/DMS-1317/{suffix}";
        string codeValue = $"DMS-1317-{suffix}";

        CreatedDocument createdDescriptor = await PostSchoolTypeDescriptorAsync(
            harness,
            namespaceName,
            codeValue,
            "Projected descriptor create"
        );
        DocumentMetadata createdMetadata = await ReadDocumentMetadataAsync(
            harness,
            createdDescriptor.DocumentUuid
        );

        (await CountProjectionWorkRowsAsync(harness, createdMetadata.DocumentId))
            .Should()
            .Be(1, "HTTP descriptor create must enqueue durable DocumentCache projection work");
        (await CountCacheRowsAsync(harness, createdMetadata.DocumentId))
            .Should()
            .Be(0, "the descriptor cache row should come from projection, not from the canonical write");

        await DrainProjectionUntilIdleAsync(harness);

        (await CountProjectionWorkRowsAsync(harness, createdMetadata.DocumentId))
            .Should()
            .Be(0, "projection must acknowledge the descriptor create work row");
        DocumentCacheRow createdCacheRow = await ReadCacheRowAsync(harness, createdMetadata.DocumentId);
        createdCacheRow
            .StreamEtag.Should()
            .Be(createdDescriptor.Etag, "projection must cache the descriptor stream version");
        AssertSchoolTypeDescriptorCacheRow(
            createdCacheRow,
            createdMetadata,
            namespaceName,
            codeValue,
            "Projected descriptor create"
        );

        JsonObject cachedGet = await GetJsonObjectAsync(harness, createdDescriptor.LocationPath);
        cachedGet["namespace"]!.GetValue<string>().Should().Be(namespaceName);
        cachedGet["codeValue"]!.GetValue<string>().Should().Be(codeValue);
        cachedGet["shortDescription"]!.GetValue<string>().Should().Be("Projected descriptor create");
        cachedGet["_etag"]!.GetValue<string>().Should().Be(createdCacheRow.StreamEtag);
        AssertReadTelemetryContains(
            recorder,
            "RecordHit",
            "Hit",
            "GET-by-id should be served from the projected descriptor cache row"
        );

        JsonArray cachedQuery = await GetJsonArrayAsync(
            harness,
            $"{SchoolTypeDescriptorsEndpoint}?namespace={Uri.EscapeDataString(namespaceName)}&codeValue={Uri.EscapeDataString(codeValue)}&totalCount=true",
            expectedTotalCount: 1
        );
        cachedQuery.Should().ContainSingle();
        cachedQuery[0]!["id"]!.GetValue<string>().Should().Be(createdDescriptor.DocumentUuid.ToString());
        cachedQuery[0]!["shortDescription"]!.GetValue<string>().Should().Be("Projected descriptor create");
        AssertReadTelemetryContains(
            recorder,
            "RecordPageHit",
            "PageHit",
            "descriptor GET-many should be served from the projected cache row"
        );

        string updateEtag = await PutSchoolTypeDescriptorAsync(
            harness,
            createdDescriptor.LocationPath,
            createdDescriptor.DocumentUuid,
            namespaceName,
            codeValue,
            "Projected descriptor update",
            createdCacheRow.StreamEtag
        );
        DocumentMetadata updatedMetadata = await ReadDocumentMetadataAsync(
            harness,
            createdDescriptor.DocumentUuid
        );
        updatedMetadata.ContentVersion.Should().BeGreaterThan(createdMetadata.ContentVersion);
        (await CountProjectionWorkRowsAsync(harness, createdMetadata.DocumentId))
            .Should()
            .Be(1, "HTTP descriptor update must enqueue durable projection work");

        DocumentCacheRow staleCacheRow = await ReadCacheRowAsync(harness, createdMetadata.DocumentId);
        staleCacheRow
            .ContentVersion.Should()
            .Be(
                createdCacheRow.ContentVersion,
                "the pre-drain descriptor cache row is stale after HTTP update"
            );

        await DrainProjectionUntilIdleAsync(harness);

        (await CountProjectionWorkRowsAsync(harness, createdMetadata.DocumentId))
            .Should()
            .Be(
                0,
                "projection must acknowledge the descriptor update work row before read acceleration can repair it"
            );
        DocumentCacheRow updatedCacheRow = await ReadCacheRowAsync(harness, createdMetadata.DocumentId);
        updatedCacheRow
            .StreamEtag.Should()
            .Be(updateEtag, "projection must cache the updated descriptor stream version");
        AssertSchoolTypeDescriptorCacheRow(
            updatedCacheRow,
            updatedMetadata,
            namespaceName,
            codeValue,
            "Projected descriptor update"
        );

        int hitCountBeforeUpdatedGet = recorder.CountTelemetryRecords("RecordHit", "Hit");
        JsonObject updatedCachedGet = await GetJsonObjectAsync(harness, createdDescriptor.LocationPath);
        updatedCachedGet["shortDescription"]!.GetValue<string>().Should().Be("Projected descriptor update");
        updatedCachedGet["_etag"]!.GetValue<string>().Should().Be(updatedCacheRow.StreamEtag);
        AssertReadTelemetryCountIncreased(
            recorder,
            "RecordHit",
            "Hit",
            hitCountBeforeUpdatedGet,
            "updated descriptor GET-by-id should be served from the projected cache row"
        );

        int pageHitCountBeforeUpdatedQuery = recorder.CountTelemetryRecords("RecordPageHit", "PageHit");
        JsonArray updatedCachedQuery = await GetJsonArrayAsync(
            harness,
            $"{SchoolTypeDescriptorsEndpoint}?namespace={Uri.EscapeDataString(namespaceName)}&codeValue={Uri.EscapeDataString(codeValue)}&totalCount=true",
            expectedTotalCount: 1
        );
        updatedCachedQuery.Should().ContainSingle();
        updatedCachedQuery[0]!["id"]!
            .GetValue<string>()
            .Should()
            .Be(createdDescriptor.DocumentUuid.ToString());
        updatedCachedQuery[0]!["shortDescription"]!
            .GetValue<string>()
            .Should()
            .Be("Projected descriptor update");
        AssertReadTelemetryCountIncreased(
            recorder,
            "RecordPageHit",
            "PageHit",
            pageHitCountBeforeUpdatedQuery,
            "updated descriptor GET-many should be served from the projected cache row"
        );

        using HttpResponseMessage deleteResponse = await harness.HttpClient.DeleteAsync(
            createdDescriptor.LocationPath
        );
        string deleteBody = await deleteResponse.Content.ReadAsStringAsync();
        deleteResponse.StatusCode.Should().Be(HttpStatusCode.NoContent, deleteBody);

        using HttpResponseMessage getAfterDelete = await harness.HttpClient.GetAsync(
            createdDescriptor.LocationPath
        );
        string getAfterDeleteBody = await getAfterDelete.Content.ReadAsStringAsync();
        getAfterDelete.StatusCode.Should().Be(HttpStatusCode.NotFound, getAfterDeleteBody);
        getAfterDeleteBody.Should().NotContain("Projected descriptor update");

        (await CountDocumentRowsAsync(harness, createdDescriptor.DocumentUuid))
            .Should()
            .Be(0, "descriptor DELETE must remove the canonical document row");
        (await CountCacheRowsAsync(harness, createdMetadata.DocumentId))
            .Should()
            .Be(0, "descriptor DELETE must cascade-remove the projected cache row");
        (await CountProjectionWorkRowsAsync(harness, createdMetadata.DocumentId))
            .Should()
            .Be(0, "descriptor DELETE must not leave orphaned projection work");
    }

    public static async Task It_rolls_back_http_create_and_update_when_document_cache_enqueue_fails(
        ApiIntegrationHarness harness
    )
    {
        await SetDocumentCacheLifecycleAsync(harness, "Disabled", cacheAheadRecoveryRequired: false);

        string studentUniqueId = $"dcr-{Guid.NewGuid():N}"[..32];
        _ = await PostStudentAsync(harness, studentUniqueId, "Rollback Seed");
        CreatedDocument existingItem = await PostProfileRootOnlyMergeItemAsync(
            harness,
            itemId: 1301,
            displayName: "Rollback original",
            clearableText: "Clearable original",
            preservedText: "Preserved original",
            studentUniqueId
        );

        RollbackSnapshot before = await ReadRollbackSnapshotAsync(harness);
        AssertRollbackSnapshotPreconditions(before, studentUniqueId);

        await SetDocumentCacheLifecycleAsync(harness, "Tracking", cacheAheadRecoveryRequired: false);
        await InstallProjectionWorkFailureTriggerAsync(harness);

        using HttpResponseMessage failedPost = await PostJsonAsync(
            harness,
            ProfileRootOnlyMergeItemsEndpoint,
            CreateProfileRootOnlyMergeItemPayload(
                itemId: 1302,
                displayName: "Rollback failed create",
                clearableText: "Clearable failed create",
                preservedText: "Preserved failed create",
                studentUniqueId
            )
        );
        await AssertInternalServerErrorAsync(
            failedPost,
            "POST create must fail when enqueue cannot persist work"
        );

        RollbackSnapshot afterFailedPost = await ReadRollbackSnapshotAsync(harness);
        afterFailedPost.Should().BeEquivalentTo(before, options => options.WithStrictOrdering());

        using HttpResponseMessage failedPut = await PutProfileRootOnlyMergeItemAsync(
            harness,
            existingItem.LocationPath,
            existingItem.DocumentUuid,
            itemId: 1301,
            displayName: "Rollback updated",
            clearableText: "Clearable updated",
            preservedText: "Preserved updated",
            studentUniqueId,
            existingItem.Etag
        );
        await AssertInternalServerErrorAsync(
            failedPut,
            "PUT update must fail when enqueue cannot persist work"
        );

        RollbackSnapshot afterFailedPut = await ReadRollbackSnapshotAsync(harness);
        afterFailedPut.Should().BeEquivalentTo(before, options => options.WithStrictOrdering());
    }

    private static async Task<CreatedDocument> PostStudentAsync(
        ApiIntegrationHarness harness,
        string studentUniqueId,
        string firstName
    )
    {
        var payload = new JsonObject { ["studentUniqueId"] = studentUniqueId, ["firstName"] = firstName };
        using HttpResponseMessage response = await PostJsonAsync(harness, StudentsEndpoint, payload);
        string body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.Created, body);
        response.Headers.Location.Should().NotBeNull();
        response.TryReadRawEtag(out string etag).Should().BeTrue("POST create must emit an ETag");

        string locationPath = ToPath(response.Headers.Location!);
        return new(locationPath, Guid.Parse(locationPath.Split('/')[^1]), etag);
    }

    private static async Task<CreatedDocument> PostProfileRootOnlyMergeItemAsync(
        ApiIntegrationHarness harness,
        int itemId,
        string displayName,
        string clearableText,
        string preservedText,
        string studentUniqueId
    )
    {
        using HttpResponseMessage response = await PostJsonAsync(
            harness,
            ProfileRootOnlyMergeItemsEndpoint,
            CreateProfileRootOnlyMergeItemPayload(
                itemId,
                displayName,
                clearableText,
                preservedText,
                studentUniqueId
            )
        );
        string body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.Created, body);
        response.Headers.Location.Should().NotBeNull();
        response.TryReadRawEtag(out string etag).Should().BeTrue("POST create must emit an ETag");

        string locationPath = ToPath(response.Headers.Location!);
        return new(locationPath, Guid.Parse(locationPath.Split('/')[^1]), etag);
    }

    private static async Task<string> PutStudentAsync(
        ApiIntegrationHarness harness,
        string locationPath,
        Guid documentUuid,
        string studentUniqueId,
        string firstName,
        string ifMatch
    )
    {
        var payload = new JsonObject
        {
            ["id"] = documentUuid.ToString(),
            ["studentUniqueId"] = studentUniqueId,
            ["firstName"] = firstName,
        };
        using var request = new HttpRequestMessage(HttpMethod.Put, locationPath)
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, StandardJsonContentType),
        };
        request.Headers.TryAddWithoutValidation("If-Match", ifMatch);

        using HttpResponseMessage response = await harness.HttpClient.SendAsync(request);
        string body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.NoContent, body);
        response.TryReadRawEtag(out string etag).Should().BeTrue("PUT must emit the advanced ETag");
        etag.Should().NotBe(ifMatch);
        return etag;
    }

    private static async Task<HttpResponseMessage> PutProfileRootOnlyMergeItemAsync(
        ApiIntegrationHarness harness,
        string locationPath,
        Guid documentUuid,
        int itemId,
        string displayName,
        string clearableText,
        string preservedText,
        string studentUniqueId,
        string ifMatch
    )
    {
        JsonObject payload = CreateProfileRootOnlyMergeItemPayload(
            itemId,
            displayName,
            clearableText,
            preservedText,
            studentUniqueId
        );
        payload["id"] = documentUuid.ToString();
        using var request = new HttpRequestMessage(HttpMethod.Put, locationPath)
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, StandardJsonContentType),
        };
        request.Headers.TryAddWithoutValidation("If-Match", ifMatch);

        return await harness.HttpClient.SendAsync(request);
    }

    private static async Task<CreatedDocument> PostSchoolTypeDescriptorAsync(
        ApiIntegrationHarness harness,
        string namespaceName,
        string codeValue,
        string shortDescription
    )
    {
        var payload = new JsonObject
        {
            ["namespace"] = namespaceName,
            ["codeValue"] = codeValue,
            ["shortDescription"] = shortDescription,
        };
        using HttpResponseMessage response = await PostJsonAsync(
            harness,
            SchoolTypeDescriptorsEndpoint,
            payload
        );
        string body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.Created, body);
        response.Headers.Location.Should().NotBeNull();
        response.TryReadRawEtag(out string etag).Should().BeTrue("POST descriptor create must emit an ETag");

        string locationPath = ToPath(response.Headers.Location!);
        return new(locationPath, Guid.Parse(locationPath.Split('/')[^1]), etag);
    }

    private static async Task<string> PutSchoolTypeDescriptorAsync(
        ApiIntegrationHarness harness,
        string locationPath,
        Guid documentUuid,
        string namespaceName,
        string codeValue,
        string shortDescription,
        string ifMatch
    )
    {
        var payload = new JsonObject
        {
            ["id"] = documentUuid.ToString(),
            ["namespace"] = namespaceName,
            ["codeValue"] = codeValue,
            ["shortDescription"] = shortDescription,
        };
        using var request = new HttpRequestMessage(HttpMethod.Put, locationPath)
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, StandardJsonContentType),
        };
        request.Headers.TryAddWithoutValidation("If-Match", ifMatch);

        using HttpResponseMessage response = await harness.HttpClient.SendAsync(request);
        string body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.NoContent, body);
        response
            .TryReadRawEtag(out string etag)
            .Should()
            .BeTrue("PUT descriptor must emit the advanced ETag");
        etag.Should().NotBe(ifMatch);
        return etag;
    }

    private static async Task<JsonObject> GetJsonObjectAsync(
        ApiIntegrationHarness harness,
        string locationPath
    )
    {
        using HttpResponseMessage response = await harness.HttpClient.GetAsync(locationPath);
        string body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        response.Content.Headers.ContentType?.MediaType.Should().Be(StandardJsonContentType);
        AssertNoCacheAccelerationDisclosure(response, body);

        JsonObject document = JsonNode.Parse(body)!.AsObject();
        response.TryReadRawEtag(out string headerEtag).Should().BeTrue("successful reads must emit ETag");
        document["_etag"]!.GetValue<string>().Should().Be(headerEtag);
        return document;
    }

    private static async Task<JsonArray> GetJsonArrayAsync(
        ApiIntegrationHarness harness,
        string endpoint,
        int expectedTotalCount
    )
    {
        using HttpResponseMessage response = await harness.HttpClient.GetAsync(endpoint);
        string body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        response.Content.Headers.ContentType?.MediaType.Should().Be(StandardJsonContentType);
        AssertNoCacheAccelerationDisclosure(response, body);
        response
            .Headers.GetValues("Total-Count")
            .Single()
            .Should()
            .Be(expectedTotalCount.ToString(CultureInfo.InvariantCulture));

        return JsonNode.Parse(body)!.AsArray();
    }

    private static async Task<HttpResponseMessage> PostJsonAsync(
        ApiIntegrationHarness harness,
        string endpoint,
        JsonObject payload
    )
    {
        using var content = new StringContent(payload.ToJsonString(), Encoding.UTF8, StandardJsonContentType);
        return await harness.HttpClient.PostAsync(endpoint, content);
    }

    private static JsonObject CreateProfileRootOnlyMergeItemPayload(
        int itemId,
        string displayName,
        string clearableText,
        string preservedText,
        string studentUniqueId
    ) =>
        new()
        {
            ["profileRootOnlyMergeItemId"] = itemId,
            ["displayName"] = displayName,
            ["profileScope"] = new JsonObject
            {
                ["clearableText"] = clearableText,
                ["preservedText"] = preservedText,
            },
            ["studentReference"] = new JsonObject { ["studentUniqueId"] = studentUniqueId },
        };

    private static async Task SetTrackingLifecycleAsync(ApiIntegrationHarness harness)
    {
        await SetDocumentCacheLifecycleAsync(harness, "Tracking", cacheAheadRecoveryRequired: false);
    }

    private static async Task SetDocumentCacheLifecycleAsync(
        ApiIntegrationHarness harness,
        string lifecycleState,
        bool cacheAheadRecoveryRequired
    )
    {
        await ExecuteNonQueryAsync(
            harness,
            """
            UPDATE "dms"."DocumentCacheState"
            SET "ProjectionLifecycleState" = @lifecycleState,
                "CacheAheadRecoveryRequired" = @cacheAheadRecoveryRequired
            WHERE "StateId" = 1;
            """,
            ("@lifecycleState", lifecycleState),
            ("@cacheAheadRecoveryRequired", cacheAheadRecoveryRequired)
        );
    }

    private static async Task RefreshProjectionTargetsAsync(ApiIntegrationHarness harness)
    {
        IDocumentCacheProjectionSupervisor supervisor =
            harness.Services.GetRequiredService<IDocumentCacheProjectionSupervisor>();
        await supervisor.RefreshAsync(DocumentCacheTargetRefreshReason.SupervisorTriggered);
        supervisor
            .CurrentTargetContexts.Should()
            .ContainSingle("the API harness configures one cache target");
    }

    private static async Task DrainProjectionUntilIdleAsync(ApiIntegrationHarness harness)
    {
        IDocumentCacheProjectionSupervisor supervisor =
            harness.Services.GetRequiredService<IDocumentCacheProjectionSupervisor>();
        IDocumentCacheProjectionDrainPageProcessor processor =
            harness.Services.GetRequiredService<IDocumentCacheProjectionDrainPageProcessor>();
        DocumentCacheProjectionTargetRuntimeContext targetContext = supervisor
            .CurrentTargetContexts.Should()
            .ContainSingle("the API harness configures one cache target")
            .Subject;

        using CancellationTokenSource timeoutSource = new(TimeSpan.FromSeconds(10));
        for (int attempt = 0; attempt < 8; attempt++)
        {
            DocumentCacheProjectionDrainPageResult result = await processor.ProcessPageAsync(
                new DocumentCacheProjectionDrainPageRequest(
                    targetContext,
                    DocumentCacheProjectionDrainInvocationKind.Ordinary
                ),
                timeoutSource.Token
            );

            if (result.Outcome == DocumentCacheProjectionDrainPageOutcome.NoEligibleWork)
            {
                return;
            }

            result.Outcome.Should().Be(DocumentCacheProjectionDrainPageOutcome.PageProcessed);
            result.DocumentScopedFailureCount.Should().Be(0);
            result.AdministrativeFailure.Should().BeNull();
        }

        false.Should().BeTrue("projection should drain this single-document scenario within 8 pages");
    }

    private static async Task<DocumentMetadata> ReadDocumentMetadataAsync(
        ApiIntegrationHarness harness,
        Guid documentUuid
    )
    {
        await using DbCommand command = harness.DbConnection.CreateCommand();
        command.CommandText = """
            SELECT d."DocumentId",
                   d."DocumentUuid",
                   d."ResourceKeyId",
                   d."ContentVersion",
                   d."ContentLastModifiedAt",
                   es."EffectiveSchemaHash",
                   rk."ProjectName",
                   rk."ResourceName",
                   rk."ResourceVersion"
            FROM "dms"."Document" d
            INNER JOIN "dms"."ResourceKey" rk
                ON rk."ResourceKeyId" = d."ResourceKeyId"
            CROSS JOIN "dms"."EffectiveSchema" es
            WHERE d."DocumentUuid" = @documentUuid;
            """;
        command.Parameters.Add(CreateParameter(command, "@documentUuid", documentUuid));

        await using DbDataReader reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue("the document row must exist");

        var metadata = new DocumentMetadata(
            Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture),
            (Guid)reader.GetValue(1),
            Convert.ToInt16(reader.GetValue(2), CultureInfo.InvariantCulture),
            Convert.ToInt64(reader.GetValue(3), CultureInfo.InvariantCulture),
            ToUtcDateTimeOffset(reader.GetValue(4)),
            reader.GetString(5),
            reader.GetString(6),
            reader.GetString(7),
            reader.GetString(8)
        );

        (await reader.ReadAsync()).Should().BeFalse("DocumentUuid is unique");
        return metadata;
    }

    private static async Task<DocumentCacheRow> ReadCacheRowAsync(
        ApiIntegrationHarness harness,
        long documentId
    )
    {
        await using DbCommand command = harness.DbConnection.CreateCommand();
        command.CommandText = """
            SELECT "DocumentId",
                   "DocumentUuid",
                   "ContentVersion",
                   "StreamEtag",
                   "LastModifiedAt",
                   "DocumentJson"
            FROM "dms"."DocumentCache"
            WHERE "DocumentId" = @documentId;
            """;
        command.Parameters.Add(CreateParameter(command, "@documentId", documentId));

        await using DbDataReader reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue("the projected cache row must exist");

        string documentJson =
            Convert.ToString(reader.GetValue(5), CultureInfo.InvariantCulture)
            ?? throw new InvalidOperationException("DocumentCache.DocumentJson was null.");
        var row = new DocumentCacheRow(
            Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture),
            (Guid)reader.GetValue(1),
            Convert.ToInt64(reader.GetValue(2), CultureInfo.InvariantCulture),
            reader.GetString(3),
            ToUtcDateTimeOffset(reader.GetValue(4)),
            JsonNode.Parse(documentJson)!.AsObject()
        );

        (await reader.ReadAsync()).Should().BeFalse("DocumentCache.DocumentId is unique");
        return row;
    }

    private static async Task<int> CountDocumentRowsAsync(ApiIntegrationHarness harness, Guid documentUuid)
    {
        await using DbCommand command = harness.DbConnection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*)
            FROM "dms"."Document"
            WHERE "DocumentUuid" = @documentUuid;
            """;
        command.Parameters.Add(CreateParameter(command, "@documentUuid", documentUuid));

        object? result = await command.ExecuteScalarAsync();
        return Convert.ToInt32(result, CultureInfo.InvariantCulture);
    }

    private static async Task<int> CountCacheRowsAsync(ApiIntegrationHarness harness, long documentId)
    {
        await using DbCommand command = harness.DbConnection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*)
            FROM "dms"."DocumentCache"
            WHERE "DocumentId" = @documentId;
            """;
        command.Parameters.Add(CreateParameter(command, "@documentId", documentId));

        object? result = await command.ExecuteScalarAsync();
        return Convert.ToInt32(result, CultureInfo.InvariantCulture);
    }

    private static async Task<int> CountProjectionWorkRowsAsync(
        ApiIntegrationHarness harness,
        long documentId
    )
    {
        await using DbCommand command = harness.DbConnection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*)
            FROM "dms"."DocumentProjectionWork"
            WHERE "DocumentId" = @documentId;
            """;
        command.Parameters.Add(CreateParameter(command, "@documentId", documentId));

        object? result = await command.ExecuteScalarAsync();
        return Convert.ToInt32(result, CultureInfo.InvariantCulture);
    }

    private static async Task<RollbackSnapshot> ReadRollbackSnapshotAsync(ApiIntegrationHarness harness) =>
        new(
            await ReadDocumentSnapshotRowsAsync(harness),
            await ReadReferentialIdentitySnapshotRowsAsync(harness),
            await ReadProfileRootOnlyMergeItemSnapshotRowsAsync(harness),
            await CountRowsAsync(harness, "\"dms\".\"DocumentProjectionWork\""),
            await CountRowsAsync(harness, "\"dms\".\"DocumentCache\""),
            await CountRowsAsync(harness, "\"tracked_changes_edfi\".\"ProfileRootOnlyMergeItem\"")
        );

    private static async Task<IReadOnlyList<DocumentSnapshotRow>> ReadDocumentSnapshotRowsAsync(
        ApiIntegrationHarness harness
    )
    {
        var rows = new List<DocumentSnapshotRow>();
        await using DbCommand command = harness.DbConnection.CreateCommand();
        command.CommandText = """
            SELECT d."DocumentId",
                   d."DocumentUuid",
                   rk."ProjectName",
                   rk."ResourceName",
                   d."ContentVersion",
                   d."ContentLastModifiedAt"
            FROM "dms"."Document" d
            INNER JOIN "dms"."ResourceKey" rk
                ON rk."ResourceKeyId" = d."ResourceKeyId"
            ORDER BY d."DocumentId";
            """;

        await using DbDataReader reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(
                new(
                    Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture),
                    (Guid)reader.GetValue(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    Convert.ToInt64(reader.GetValue(4), CultureInfo.InvariantCulture),
                    ToUtcDateTimeOffset(reader.GetValue(5))
                )
            );
        }

        return rows;
    }

    private static async Task<
        IReadOnlyList<ReferentialIdentitySnapshotRow>
    > ReadReferentialIdentitySnapshotRowsAsync(ApiIntegrationHarness harness)
    {
        var rows = new List<ReferentialIdentitySnapshotRow>();
        await using DbCommand command = harness.DbConnection.CreateCommand();
        command.CommandText = """
            SELECT "ReferentialId",
                   "DocumentId",
                   "ResourceKeyId"
            FROM "dms"."ReferentialIdentity"
            ORDER BY "DocumentId", "ResourceKeyId", "ReferentialId";
            """;

        await using DbDataReader reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(
                new(
                    (Guid)reader.GetValue(0),
                    Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture),
                    Convert.ToInt16(reader.GetValue(2), CultureInfo.InvariantCulture)
                )
            );
        }

        return rows;
    }

    private static async Task<
        IReadOnlyList<ProfileRootOnlyMergeItemSnapshotRow>
    > ReadProfileRootOnlyMergeItemSnapshotRowsAsync(ApiIntegrationHarness harness)
    {
        var rows = new List<ProfileRootOnlyMergeItemSnapshotRow>();
        await using DbCommand command = harness.DbConnection.CreateCommand();
        command.CommandText = """
            SELECT "DocumentId",
                   "ProfileRootOnlyMergeItemId",
                   "DisplayName",
                   "ProfileScopeClearableText",
                   "ProfileScopePreservedText",
                   "StudentReference_DocumentId",
                   "StudentReference_StudentUniqueId"
            FROM "edfi"."ProfileRootOnlyMergeItem"
            ORDER BY "ProfileRootOnlyMergeItemId";
            """;

        await using DbDataReader reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(
                new(
                    Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture),
                    Convert.ToInt32(reader.GetValue(1), CultureInfo.InvariantCulture),
                    await reader.IsDBNullAsync(2) ? null : reader.GetString(2),
                    await reader.IsDBNullAsync(3) ? null : reader.GetString(3),
                    await reader.IsDBNullAsync(4) ? null : reader.GetString(4),
                    await reader.IsDBNullAsync(5)
                        ? null
                        : Convert.ToInt64(reader.GetValue(5), CultureInfo.InvariantCulture),
                    await reader.IsDBNullAsync(6) ? null : reader.GetString(6)
                )
            );
        }

        return rows;
    }

    private static async Task<long> CountRowsAsync(ApiIntegrationHarness harness, string qualifiedTableName)
    {
        await using DbCommand command = harness.DbConnection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {qualifiedTableName};";

        object? result = await command.ExecuteScalarAsync();
        return Convert.ToInt64(result, CultureInfo.InvariantCulture);
    }

    private static async Task InstallProjectionWorkFailureTriggerAsync(ApiIntegrationHarness harness)
    {
        string commandText = IsSqlServerConnection(harness.DbConnection)
            ? """
                CREATE OR ALTER TRIGGER [dms].[TR_DMS1317_ForceDocumentProjectionWorkFailure]
                ON [dms].[DocumentProjectionWork]
                AFTER INSERT, UPDATE
                AS
                BEGIN
                    SET NOCOUNT ON;
                    THROW 50000, N'DMS-1317 forced DocumentCache enqueue failure', 1;
                END
                """
            : """
                CREATE OR REPLACE FUNCTION "dms"."TF_DMS1317_ForceDocumentProjectionWorkFailure"()
                RETURNS trigger
                LANGUAGE plpgsql
                AS $dms1317$
                BEGIN
                    RAISE EXCEPTION 'DMS-1317 forced DocumentCache enqueue failure';
                END;
                $dms1317$;

                DROP TRIGGER IF EXISTS "TR_DMS1317_ForceDocumentProjectionWorkFailure" ON "dms"."DocumentProjectionWork";

                CREATE TRIGGER "TR_DMS1317_ForceDocumentProjectionWorkFailure"
                BEFORE INSERT OR UPDATE ON "dms"."DocumentProjectionWork"
                FOR EACH ROW
                EXECUTE FUNCTION "dms"."TF_DMS1317_ForceDocumentProjectionWorkFailure"();
                """;

        await ExecuteNonQueryAsync(harness, commandText);
    }

    private static bool IsSqlServerConnection(DbConnection connection) =>
        connection.GetType().Name.Equals("SqlConnection", StringComparison.Ordinal);

    private static async Task ExecuteNonQueryAsync(
        ApiIntegrationHarness harness,
        string commandText,
        params (string Name, object? Value)[] parameters
    )
    {
        await using DbCommand command = harness.DbConnection.CreateCommand();
        command.CommandText = commandText;
        foreach ((string name, object? value) in parameters)
        {
            command.Parameters.Add(CreateParameter(command, name, value));
        }

        await command.ExecuteNonQueryAsync();
    }

    private static DbParameter CreateParameter(DbCommand command, string name, object? value)
    {
        DbParameter parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value switch
        {
            null => DBNull.Value,
            _ => value,
        };
        return parameter;
    }

    private static DateTimeOffset ToUtcDateTimeOffset(object value) =>
        value switch
        {
            DateTimeOffset dateTimeOffset => dateTimeOffset.ToUniversalTime(),
            DateTime dateTime => new DateTimeOffset(
                dateTime.Kind == DateTimeKind.Utc
                    ? dateTime
                    : DateTime.SpecifyKind(dateTime, DateTimeKind.Utc)
            ),
            _ => DateTimeOffset
                .Parse(
                    value.ToString() ?? throw new InvalidOperationException("Document timestamp was null."),
                    CultureInfo.InvariantCulture
                )
                .ToUniversalTime(),
        };

    private static void AssertStudentCacheRow(
        DocumentCacheRow row,
        DocumentMetadata metadata,
        string studentUniqueId,
        string firstName
    )
    {
        row.DocumentId.Should().Be(metadata.DocumentId);
        row.DocumentUuid.Should().Be(metadata.DocumentUuid);
        row.ContentVersion.Should().Be(metadata.ContentVersion);
        row.LastModifiedAt.Should().BeCloseTo(metadata.ContentLastModifiedAt, TimeSpan.FromTicks(10));
        row.DocumentJson["id"]!.GetValue<string>().Should().Be(metadata.DocumentUuid.ToString());
        row.DocumentJson["studentUniqueId"]!.GetValue<string>().Should().Be(studentUniqueId);
        row.DocumentJson["firstName"]!.GetValue<string>().Should().Be(firstName);
        row.DocumentJson["_lastModifiedDate"]!.GetValue<string>().Should().NotBeNullOrWhiteSpace();
        row.DocumentJson.ContainsKey("_etag")
            .Should()
            .BeFalse("cache JSON should store caller-agnostic stream content");
    }

    private static void AssertSchoolTypeDescriptorCacheRow(
        DocumentCacheRow row,
        DocumentMetadata metadata,
        string namespaceName,
        string codeValue,
        string shortDescription
    )
    {
        row.DocumentId.Should().Be(metadata.DocumentId);
        row.DocumentUuid.Should().Be(metadata.DocumentUuid);
        row.ContentVersion.Should().Be(metadata.ContentVersion);
        row.LastModifiedAt.Should().BeCloseTo(metadata.ContentLastModifiedAt, TimeSpan.FromTicks(10));
        row.DocumentJson["id"]!.GetValue<string>().Should().Be(metadata.DocumentUuid.ToString());
        row.DocumentJson["namespace"]!.GetValue<string>().Should().Be(namespaceName);
        row.DocumentJson["codeValue"]!.GetValue<string>().Should().Be(codeValue);
        row.DocumentJson["shortDescription"]!.GetValue<string>().Should().Be(shortDescription);
        row.DocumentJson["_lastModifiedDate"]!.GetValue<string>().Should().NotBeNullOrWhiteSpace();
        row.DocumentJson.ContainsKey("_etag")
            .Should()
            .BeFalse("descriptor cache JSON should store caller-agnostic stream content");
    }

    private static void AssertNoCacheAccelerationDisclosure(HttpResponseMessage response, string body)
    {
        response
            .Headers.Select(header => header.Key)
            .Concat(response.Content.Headers.Select(header => header.Key))
            .Should()
            .NotContain(
                header =>
                    header.Contains("DocumentCache", StringComparison.OrdinalIgnoreCase)
                    || header.Contains("ReadAcceleration", StringComparison.OrdinalIgnoreCase),
                "public response headers must not reveal whether the read was cache-backed"
            );
        body.Should().NotContain("DocumentCache").And.NotContain("ReadAcceleration");
    }

    private static async Task AssertInternalServerErrorAsync(HttpResponseMessage response, string because)
    {
        string body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError, $"{because}: {body}");
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");
        JsonObject problem = JsonNode.Parse(body)!.AsObject();
        problem["status"]!.GetValue<int>().Should().Be(500);
        body.Should().NotContain(ForcedEnqueueFailureMessage, "provider diagnostics must not leak");
    }

    private static void AssertRollbackSnapshotPreconditions(RollbackSnapshot snapshot, string studentUniqueId)
    {
        snapshot
            .Documents.Select(document => document.ResourceName)
            .Should()
            .BeEquivalentTo("Student", "ProfileRootOnlyMergeItem");
        snapshot.ProfileRootOnlyMergeItems.Should().ContainSingle();
        ProfileRootOnlyMergeItemSnapshotRow item = snapshot.ProfileRootOnlyMergeItems.Single();
        snapshot
            .ReferentialIdentities.Should()
            .HaveCount(2, "Student and ProfileRootOnlyMergeItem each stamp a referential identity");
        snapshot
            .ReferentialIdentities.Should()
            .ContainSingle(identity => identity.DocumentId == item.DocumentId);
        item.ProfileRootOnlyMergeItemId.Should().Be(1301);
        item.DisplayName.Should().Be("Rollback original");
        item.ProfileScopeClearableText.Should().Be("Clearable original");
        item.ProfileScopePreservedText.Should().Be("Preserved original");
        item.StudentReferenceDocumentId.Should().NotBeNull();
        item.StudentReferenceStudentUniqueId.Should().Be(studentUniqueId);
        snapshot.ProjectionWorkCount.Should().Be(0);
        snapshot.CacheCount.Should().Be(0);
        snapshot.ProfileRootOnlyMergeTrackedChangeCount.Should().Be(0);
    }

    private static void AssertReadTelemetryContains(
        DocumentCacheReadTelemetryRecorder recorder,
        string eventName,
        string outcome,
        string because
    )
    {
        string[] records = recorder
            .TelemetryRecords.Select(record => $"{record.EventName}:{record.Operation}:{record.Outcome}")
            .ToArray();
        recorder
            .CountTelemetryRecords(eventName, outcome)
            .Should()
            .BeGreaterThan(0, $"{because}. Recorded telemetry: {string.Join(", ", records)}");
    }

    private static void AssertReadTelemetryCountIncreased(
        DocumentCacheReadTelemetryRecorder recorder,
        string eventName,
        string outcome,
        int previousCount,
        string because
    )
    {
        string[] records = recorder
            .TelemetryRecords.Select(record => $"{record.EventName}:{record.Operation}:{record.Outcome}")
            .ToArray();
        recorder
            .CountTelemetryRecords(eventName, outcome)
            .Should()
            .BeGreaterThan(previousCount, $"{because}. Recorded telemetry: {string.Join(", ", records)}");
    }

    private static string ToPath(Uri location) =>
        location.IsAbsoluteUri ? location.AbsolutePath : location.OriginalString;

    private sealed record CreatedDocument(string LocationPath, Guid DocumentUuid, string Etag);

    private sealed record DocumentMetadata(
        long DocumentId,
        Guid DocumentUuid,
        short ResourceKeyId,
        long ContentVersion,
        DateTimeOffset ContentLastModifiedAt,
        string EffectiveSchemaHash,
        string ProjectName,
        string ResourceName,
        string ResourceVersion
    );

    private sealed record DocumentCacheRow(
        long DocumentId,
        Guid DocumentUuid,
        long ContentVersion,
        string StreamEtag,
        DateTimeOffset LastModifiedAt,
        JsonObject DocumentJson
    );

    private sealed record RollbackSnapshot(
        IReadOnlyList<DocumentSnapshotRow> Documents,
        IReadOnlyList<ReferentialIdentitySnapshotRow> ReferentialIdentities,
        IReadOnlyList<ProfileRootOnlyMergeItemSnapshotRow> ProfileRootOnlyMergeItems,
        long ProjectionWorkCount,
        long CacheCount,
        long ProfileRootOnlyMergeTrackedChangeCount
    );

    private sealed record DocumentSnapshotRow(
        long DocumentId,
        Guid DocumentUuid,
        string ProjectName,
        string ResourceName,
        long ContentVersion,
        DateTimeOffset ContentLastModifiedAt
    );

    private sealed record ReferentialIdentitySnapshotRow(
        Guid ReferentialId,
        long DocumentId,
        short ResourceKeyId
    );

    private sealed record ProfileRootOnlyMergeItemSnapshotRow(
        long DocumentId,
        int ProfileRootOnlyMergeItemId,
        string? DisplayName,
        string? ProfileScopeClearableText,
        string? ProfileScopePreservedText,
        long? StudentReferenceDocumentId,
        string? StudentReferenceStudentUniqueId
    );
}
