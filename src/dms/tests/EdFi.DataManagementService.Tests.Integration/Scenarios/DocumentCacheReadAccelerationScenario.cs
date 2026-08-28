// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.Etag;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.External.Security;
using EdFi.DataManagementService.Core.Paging;
using EdFi.DataManagementService.Core.Security;
using EdFi.DataManagementService.Core.Security.Model;
using EdFi.DataManagementService.Tests.Integration.Doubles;
using FluentAssertions;
using Microsoft.Data.SqlClient;

namespace EdFi.DataManagementService.Tests.Integration.Scenarios;

internal static class DocumentCacheReadAccelerationScenario
{
    private const string StudentsEndpoint = "/data/ed-fi/students";
    private const string ProfileRootOnlyMergeItemsEndpoint = "/data/ed-fi/profileRootOnlyMergeItems";
    private const string SchoolTypeDescriptorsEndpoint = "/data/ed-fi/schoolTypeDescriptors";
    private const string StandardJsonContentType = "application/json";
    private const string VisibleReadableContentType =
        "application/vnd.ed-fi.profilerootonlymergeitem.profilerootonlymergeitem-visible.readable+json";
    private const string LastModifiedDateFormat = "yyyy-MM-ddTHH:mm:ss'Z'";
    private const string NextPageTokenHeaderName = "Next-Page-Token";

    public static IClaimSetProvider CreateStudentCreateOnlyClaimSetProvider() =>
        new StaticClaimSetProvider([
            new ClaimSet(
                ExternalDoublesConstants.SmokeClaimSetName,
                [
                    new ResourceClaim(
                        $"{Conventions.EdFiOdsResourceClaimBaseUri}/ed-fi/student",
                        "Create",
                        [
                            new AuthorizationStrategy(
                                AuthorizationStrategyNameConstants.NoFurtherAuthorizationRequired
                            ),
                        ]
                    ),
                ]
            ),
        ]);

    public static async Task It_serves_cached_get_and_query_for_ordinary_resources(
        ApiIntegrationHarness harness
    )
    {
        await SetTrackingLifecycleAsync(harness);

        CreatedDocument getStudent = await CreateStudentAsync(harness, "cache-get-001", "Relational Get");
        DocumentMetadata getMetadata = await ReadDocumentMetadataAsync(harness, getStudent.DocumentUuid);
        JsonObject getCacheDocument = CacheDocumentFrom(getStudent.Body, getMetadata);
        getCacheDocument["firstName"] = "Cached Get";
        await UpsertCacheRowAsync(harness, getMetadata, getCacheDocument);

        JsonObject cachedGet = await GetJsonObjectAsync(harness, getStudent.LocationPath);
        cachedGet["studentUniqueId"]!.GetValue<string>().Should().Be("cache-get-001");
        cachedGet["firstName"]!.GetValue<string>().Should().Be("Cached Get");

        CreatedDocument firstQueryStudent = await CreateStudentAsync(
            harness,
            "cache-query-001",
            "Relational Query One"
        );
        CreatedDocument secondQueryStudent = await CreateStudentAsync(
            harness,
            "cache-query-002",
            "Relational Query Two"
        );

        DocumentMetadata firstQueryMetadata = await ReadDocumentMetadataAsync(
            harness,
            firstQueryStudent.DocumentUuid
        );
        JsonObject firstQueryCacheDocument = CacheDocumentFrom(firstQueryStudent.Body, firstQueryMetadata);
        firstQueryCacheDocument["firstName"] = "Cached Query One";
        await UpsertCacheRowAsync(harness, firstQueryMetadata, firstQueryCacheDocument);

        DocumentMetadata secondQueryMetadata = await ReadDocumentMetadataAsync(
            harness,
            secondQueryStudent.DocumentUuid
        );
        JsonObject secondQueryCacheDocument = CacheDocumentFrom(secondQueryStudent.Body, secondQueryMetadata);
        secondQueryCacheDocument["firstName"] = "Cached Query Two";
        await UpsertCacheRowAsync(harness, secondQueryMetadata, secondQueryCacheDocument);

        using HttpResponseMessage queryResponse = await harness.HttpClient.GetAsync(
            $"{StudentsEndpoint}?offset=1&limit=2&totalCount=true"
        );
        string queryBody = await queryResponse.Content.ReadAsStringAsync();
        queryResponse.StatusCode.Should().Be(HttpStatusCode.OK, queryBody);
        queryResponse.Content.Headers.ContentType?.MediaType.Should().Be(StandardJsonContentType);
        queryResponse.Headers.GetValues("Total-Count").Single().Should().Be("3");

        JsonArray cachedQuery = JsonNode.Parse(queryBody)!.AsArray();
        cachedQuery
            .Select(node => node!["firstName"]!.GetValue<string>())
            .Should()
            .Equal("Cached Query One", "Cached Query Two");
        cachedQuery
            .Select(node => node!["_etag"]!.GetValue<string>())
            .Should()
            .OnlyContain(etag => !string.IsNullOrWhiteSpace(etag));

        // Every document on this page came from cache, and the continuation a cursor walk enters on
        // still names the page after it: what a client is handed cannot depend on cache state.
        string nextPageToken = queryResponse.Headers.GetValues(NextPageTokenHeaderName).Single();
        PageTokenCodec
            .TryDecode(nextPageToken, out var continuation, out _)
            .Should()
            .BeTrue("an emitted continuation must decode through the codec that produced it");
        continuation!
            .InclusiveMinimum.Should()
            .Be(
                secondQueryMetadata.DocumentId + 1,
                "the continuation resumes after the highest key this page selected"
            );
    }

    public static async Task It_falls_back_relationally_when_cache_row_is_missing_or_stale(
        ApiIntegrationHarness harness
    )
    {
        await SetTrackingLifecycleAsync(harness);

        string missingStudentLocationPath = await PostStudentAsync(
            harness,
            "cache-missing-001",
            "Relational Missing"
        );
        var missingStudentDocumentUuid = Guid.Parse(missingStudentLocationPath.Split('/')[^1]);
        DocumentMetadata missingStudentMetadata = await ReadDocumentMetadataAsync(
            harness,
            missingStudentDocumentUuid
        );
        await DeleteCacheRowsAsync(harness, missingStudentMetadata.DocumentId);
        await ReplaceProjectionWorkAsync(harness, missingStudentMetadata);
        (await CountCacheRowsAsync(harness, missingStudentMetadata.DocumentId))
            .Should()
            .Be(0, "the first successful GET must prove the cache-miss fallback path");
        (await CountProjectionWorkRowsAsync(harness, missingStudentMetadata.DocumentId))
            .Should()
            .Be(1, "direct-fill success requires current matching projection work");

        JsonObject missingFallback = await GetJsonObjectAsync(harness, missingStudentLocationPath);

        missingFallback["studentUniqueId"]!.GetValue<string>().Should().Be("cache-missing-001");
        missingFallback["firstName"]!
            .GetValue<string>()
            .Should()
            .Be("Relational Missing", "a missing cache row must use relational fallback");
        AssertReadTelemetryContains(
            harness,
            "RecordDirectFill",
            "Succeeded",
            "a missing cache row with matching projection work should be direct-filled after successful relational fallback"
        );
        DocumentCacheRow directFilledRow = await ReadCacheRowAsync(
            harness,
            missingStudentMetadata.DocumentId
        );
        AssertDirectFilledStudentCacheRow(
            directFilledRow,
            missingStudentMetadata,
            "cache-missing-001",
            "Relational Missing"
        );
        (await CountProjectionWorkRowsAsync(harness, missingStudentMetadata.DocumentId))
            .Should()
            .Be(0, "successful direct fill must acknowledge only the matching projection work row");

        CreatedDocument student = await CreateStudentAsync(
            harness,
            "cache-fallback-001",
            "Relational Fallback"
        );
        DocumentMetadata metadata = await ReadDocumentMetadataAsync(harness, student.DocumentUuid);
        JsonObject cacheDocument = CacheDocumentFrom(student.Body, metadata);
        cacheDocument["firstName"] = "Cached Stale";
        await UpsertCacheRowAsync(harness, metadata, cacheDocument, metadata.ContentVersion + 1);

        JsonObject fallback = await GetJsonObjectAsync(harness, student.LocationPath);

        fallback["studentUniqueId"]!.GetValue<string>().Should().Be("cache-fallback-001");
        fallback["firstName"]!
            .GetValue<string>()
            .Should()
            .Be("Relational Fallback", "a stale cache row must not replace relational fallback");
    }

    public static async Task It_times_out_direct_fill_without_replacing_relational_response(
        ApiIntegrationHarness harness
    )
    {
        await SetTrackingLifecycleAsync(harness);
        DocumentCacheDirectFillTimeoutRecorder recorder =
            harness.DocumentCacheDirectFillTimeoutRecorder
            ?? throw new InvalidOperationException(
                "The direct-fill timeout scenario requires the direct-fill timeout recorder."
            );

        string locationPath = await PostStudentAsync(harness, "cache-timeout-001", "Relational Timeout");
        var documentUuid = Guid.Parse(locationPath.Split('/')[^1]);
        DocumentMetadata metadata = await ReadDocumentMetadataAsync(harness, documentUuid);
        await DeleteCacheRowsAsync(harness, metadata.DocumentId);
        (await CountCacheRowsAsync(harness, metadata.DocumentId))
            .Should()
            .Be(0, "the timeout scenario must begin with a true cache miss");

        JsonObject fallback = await GetJsonObjectAsync(harness, locationPath);

        fallback["studentUniqueId"]!.GetValue<string>().Should().Be("cache-timeout-001");
        fallback["firstName"]!
            .GetValue<string>()
            .Should()
            .Be("Relational Timeout", "direct-fill timeout must not replace relational fallback");
        (await CountCacheRowsAsync(harness, metadata.DocumentId))
            .Should()
            .Be(0, "a timed-out direct fill must not write a partial cache row");

        recorder.MaterializationAttempts.Should().Be(1);
        recorder.MaterializationCancellations.Should().Be(1);
        recorder.CountTelemetryRecords("RecordDirectFill", "Attempted").Should().Be(1);
        recorder.CountTelemetryRecords("RecordDirectFill", "TimedOut").Should().Be(1);
        recorder.CountTelemetryRecords("RecordDirectFillDuration", "TimedOut").Should().Be(1);
        recorder.CountTelemetryRecords("RecordDirectFill", "Succeeded").Should().Be(0);
        recorder.CountTelemetryRecords("RecordDirectFill", "Failed").Should().Be(0);
    }

    public static async Task It_falls_back_relationally_when_cache_adapter_acquisition_fails(
        ApiIntegrationHarness harness
    )
    {
        await SetTrackingLifecycleAsync(harness);
        DocumentCacheReadAcquisitionFailureRecorder recorder =
            harness.DocumentCacheReadAcquisitionFailureRecorder
            ?? throw new InvalidOperationException(
                "The unavailable-cache scenario requires the cache read acquisition failure recorder."
            );

        string getStudentLocationPath = await PostStudentAsync(
            harness,
            "cache-unavailable-get-001",
            "Relational Unavailable Get"
        );
        DocumentMetadata getMetadata = await ReadDocumentMetadataAsync(
            harness,
            Guid.Parse(getStudentLocationPath.Split('/')[^1])
        );

        JsonObject getFallback = await GetJsonObjectAsync(harness, getStudentLocationPath);

        getFallback["studentUniqueId"]!.GetValue<string>().Should().Be("cache-unavailable-get-001");
        getFallback["firstName"]!
            .GetValue<string>()
            .Should()
            .Be(
                "Relational Unavailable Get",
                "cache adapter acquisition failure must use relational GET fallback"
            );
        (await CountCacheRowsAsync(harness, getMetadata.DocumentId))
            .Should()
            .Be(0, "cache-unavailable lookup must skip direct fill for the same request");

        string firstQueryStudentLocationPath = await PostStudentAsync(
            harness,
            "cache-unavailable-query-001",
            "Relational Unavailable Query One"
        );
        string secondQueryStudentLocationPath = await PostStudentAsync(
            harness,
            "cache-unavailable-query-002",
            "Relational Unavailable Query Two"
        );
        DocumentMetadata firstQueryMetadata = await ReadDocumentMetadataAsync(
            harness,
            Guid.Parse(firstQueryStudentLocationPath.Split('/')[^1])
        );
        DocumentMetadata secondQueryMetadata = await ReadDocumentMetadataAsync(
            harness,
            Guid.Parse(secondQueryStudentLocationPath.Split('/')[^1])
        );

        using HttpResponseMessage queryResponse = await harness.HttpClient.GetAsync(
            $"{StudentsEndpoint}?offset=1&limit=2&totalCount=true"
        );
        string queryBody = await queryResponse.Content.ReadAsStringAsync();
        queryResponse.StatusCode.Should().Be(HttpStatusCode.OK, queryBody);
        queryResponse.Content.Headers.ContentType?.MediaType.Should().Be(StandardJsonContentType);
        queryResponse.Headers.GetValues("Total-Count").Single().Should().Be("3");
        AssertNoCacheAccelerationDisclosure(queryResponse, queryBody);

        JsonArray queryFallback = JsonNode.Parse(queryBody)!.AsArray();
        queryFallback
            .Select(node => node!["firstName"]!.GetValue<string>())
            .Should()
            .Equal("Relational Unavailable Query One", "Relational Unavailable Query Two");
        (await CountCacheRowsAsync(harness, firstQueryMetadata.DocumentId))
            .Should()
            .Be(0, "cache-unavailable query fallback must not direct-fill selected page documents");
        (await CountCacheRowsAsync(harness, secondQueryMetadata.DocumentId))
            .Should()
            .Be(0, "cache-unavailable query fallback must not direct-fill selected page documents");

        recorder.CountLookupAttempts("getById").Should().Be(1);
        recorder.CountLookupAttempts("query").Should().Be(1);
        recorder
            .CountTelemetryRecords("RecordAdapterAcquisitionFailure", "CacheUnavailable")
            .Should()
            .Be(2, "both GET-by-id and GET-many should observe adapter acquisition failure");
        recorder
            .CountTelemetryRecords("RecordDirectFill", "SkippedCacheUnavailable")
            .Should()
            .Be(2, "adapter acquisition failure must skip direct fill for both operations");
        recorder
            .CountTelemetryRecords("RecordDirectFill", "SkippedTargetMismatch")
            .Should()
            .Be(0, "the target signature must remain matched for this scenario");
        recorder
            .CountTelemetryRecords("RecordFallback", "UnresolvedTarget")
            .Should()
            .Be(0, "this scenario must not exercise the unresolved-target bypass path");
        recorder.CountTelemetryRecords("RecordFallback", "CacheLookupUnavailable").Should().Be(2);
    }

    public static async Task It_serves_descriptor_query_from_cache_and_falls_back_for_incomplete_pages(
        ApiIntegrationHarness harness
    )
    {
        await SetTrackingLifecycleAsync(harness);

        string namespaceName = $"uri://ed-fi.org/SchoolTypeDescriptor/DMS-1315/query/{Guid.NewGuid():N}";
        CreatedDocument cachedDescriptor = await CreateSchoolTypeDescriptorAsync(
            harness,
            namespaceName,
            codeValue: "DMS-1315-query-a",
            shortDescription: "Relational descriptor query cached"
        );
        CreatedDocument missingDescriptor = await CreateSchoolTypeDescriptorAsync(
            harness,
            namespaceName,
            codeValue: "DMS-1315-query-b",
            shortDescription: "Relational descriptor query missing"
        );
        CreatedDocument staleDescriptor = await CreateSchoolTypeDescriptorAsync(
            harness,
            namespaceName,
            codeValue: "DMS-1315-query-c",
            shortDescription: "Relational descriptor query stale"
        );

        DocumentMetadata cachedMetadata = await ReadDocumentMetadataAsync(
            harness,
            cachedDescriptor.DocumentUuid
        );
        DocumentMetadata missingMetadata = await ReadDocumentMetadataAsync(
            harness,
            missingDescriptor.DocumentUuid
        );
        DocumentMetadata staleMetadata = await ReadDocumentMetadataAsync(
            harness,
            staleDescriptor.DocumentUuid
        );
        await DeleteCacheRowsAsync(
            harness,
            cachedMetadata.DocumentId,
            missingMetadata.DocumentId,
            staleMetadata.DocumentId
        );

        JsonObject cachedDocument = CacheDocumentFrom(cachedDescriptor.Body, cachedMetadata);
        cachedDocument["shortDescription"] = "Cached descriptor query";
        await UpsertCacheRowAsync(harness, cachedMetadata, cachedDocument);

        JsonObject staleDocument = CacheDocumentFrom(staleDescriptor.Body, staleMetadata);
        staleDocument["shortDescription"] = "Cached stale descriptor query";
        await UpsertCacheRowAsync(harness, staleMetadata, staleDocument, staleMetadata.ContentVersion + 1);
        (await CountCacheRowsAsync(harness, missingMetadata.DocumentId))
            .Should()
            .Be(0, "the descriptor page must include one true missing cache row");

        JsonArray cachedQuery = await GetJsonArrayAsync(
            harness,
            $"{SchoolTypeDescriptorsEndpoint}?namespace={Escape(namespaceName)}&codeValue=DMS-1315-query-a&totalCount=true",
            expectedTotalCount: 1
        );

        cachedQuery.Count.Should().Be(1);
        cachedQuery[0]!["shortDescription"]!.GetValue<string>().Should().Be("Cached descriptor query");
        cachedQuery[0]!["_etag"]!.GetValue<string>().Should().NotBeNullOrWhiteSpace();

        JsonArray fallbackPage = await GetJsonArrayAsync(
            harness,
            $"{SchoolTypeDescriptorsEndpoint}?namespace={Escape(namespaceName)}&offset=0&limit=3&totalCount=true",
            expectedTotalCount: 3
        );

        fallbackPage
            .Select(node => node!["id"]!.GetValue<string>())
            .Should()
            .Equal(
                cachedDescriptor.DocumentUuid.ToString(),
                missingDescriptor.DocumentUuid.ToString(),
                staleDescriptor.DocumentUuid.ToString()
            );
        fallbackPage
            .Select(node => node!["shortDescription"]!.GetValue<string>())
            .Should()
            .Equal(
                "Relational descriptor query cached",
                "Relational descriptor query missing",
                "Relational descriptor query stale"
            );
    }

    public static async Task It_shapes_cached_profile_and_descriptor_conditional_get(
        ApiIntegrationHarness harness
    )
    {
        await SetTrackingLifecycleAsync(harness);

        CreatedDocument profileItem = await CreateProfileRootOnlyMergeItemAsync(
            harness,
            itemId: 6101,
            displayName: "Relational Profile",
            clearableText: "relational-clearable",
            preservedText: "relational-preserved"
        );
        DocumentMetadata profileMetadata = await ReadDocumentMetadataAsync(harness, profileItem.DocumentUuid);
        await DeleteCacheRowsAsync(harness, profileMetadata.DocumentId);
        JsonObject profileCacheDocument = CacheDocumentFrom(profileItem.Body, profileMetadata);
        profileCacheDocument["displayName"] = "Cached Profile";
        profileCacheDocument["profileScope"] = new JsonObject
        {
            ["clearableText"] = "cached-clearable",
            ["preservedText"] = "cached-preserved",
        };
        await UpsertCacheRowAsync(harness, profileMetadata, profileCacheDocument);

        using HttpResponseMessage profiledResponse = await SendProfiledGetAsync(
            harness,
            profileItem.LocationPath
        );
        string profiledBody = await profiledResponse.Content.ReadAsStringAsync();
        profiledResponse.StatusCode.Should().Be(HttpStatusCode.OK, profiledBody);
        JsonObject profiled = JsonNode.Parse(profiledBody)!.AsObject();
        profiled["displayName"]!.GetValue<string>().Should().Be("Cached Profile");
        JsonObject profiledScope = profiled["profileScope"]!.AsObject();
        profiledScope["clearableText"]!.GetValue<string>().Should().Be("cached-clearable");
        profiledScope
            .ContainsKey("preservedText")
            .Should()
            .BeFalse("readable profile projection must run over cached JSON");
        profiledResponse.TryReadRawEtag(out string profiledHeaderEtag).Should().BeTrue();
        profiled["_etag"]!.GetValue<string>().Should().Be(profiledHeaderEtag);

        CreatedDocument descriptor = await CreateSchoolTypeDescriptorAsync(harness);
        DocumentMetadata descriptorMetadata = await ReadDocumentMetadataAsync(
            harness,
            descriptor.DocumentUuid
        );
        await DeleteCacheRowsAsync(harness, descriptorMetadata.DocumentId);
        JsonObject descriptorCacheDocument = CacheDocumentFrom(descriptor.Body, descriptorMetadata);
        descriptorCacheDocument["shortDescription"] = "Cached school type";
        await UpsertCacheRowAsync(harness, descriptorMetadata, descriptorCacheDocument);

        using HttpResponseMessage descriptorGetResponse = await harness.HttpClient.GetAsync(
            descriptor.LocationPath
        );
        string descriptorGetBody = await descriptorGetResponse.Content.ReadAsStringAsync();
        descriptorGetResponse.StatusCode.Should().Be(HttpStatusCode.OK, descriptorGetBody);
        descriptorGetResponse.TryReadRawEtag(out string descriptorEtag).Should().BeTrue();
        JsonObject descriptorGet = JsonNode.Parse(descriptorGetBody)!.AsObject();
        descriptorGet["shortDescription"]!.GetValue<string>().Should().Be("Cached school type");
        descriptorGet["_etag"]!.GetValue<string>().Should().Be(descriptorEtag);

        using var conditionalRequest = new HttpRequestMessage(HttpMethod.Get, descriptor.LocationPath);
        conditionalRequest.Headers.TryAddWithoutValidation("If-None-Match", $"\"{descriptorEtag}\"");
        using HttpResponseMessage conditionalResponse = await harness.HttpClient.SendAsync(
            conditionalRequest
        );

        conditionalResponse.StatusCode.Should().Be(HttpStatusCode.NotModified);
        (await conditionalResponse.Content.ReadAsStringAsync()).Should().BeEmpty();
        conditionalResponse.TryReadRawEtag(out string notModifiedEtag).Should().BeTrue();
        notModifiedEtag.Should().Be(descriptorEtag);
    }

    public static async Task It_strips_links_from_cached_resource_when_resource_links_are_disabled(
        ApiIntegrationHarness harness
    )
    {
        await SetTrackingLifecycleAsync(harness);

        CreatedDocument student = await CreateStudentAsync(harness, "cache-links-student", "Link Target");
        CreatedDocument mergeItem = await CreateProfileRootOnlyMergeItemAsync(
            harness,
            itemId: 6201,
            displayName: "Relational Links",
            clearableText: "links-clearable",
            preservedText: "links-preserved",
            studentUniqueId: student.Body["studentUniqueId"]!.GetValue<string>()
        );
        DocumentMetadata metadata = await ReadDocumentMetadataAsync(harness, mergeItem.DocumentUuid);
        JsonObject cacheDocument = CacheDocumentFrom(mergeItem.Body, metadata);
        cacheDocument["displayName"] = "Cached Links";
        cacheDocument["studentReference"] = new JsonObject
        {
            ["studentUniqueId"] = "cache-links-student",
            ["link"] = new JsonObject
            {
                ["rel"] = "Student",
                ["href"] = "/data/ed-fi/students/not-public-from-cache",
            },
        };
        await UpsertCacheRowAsync(harness, metadata, cacheDocument);

        JsonObject returned = await GetJsonObjectAsync(harness, mergeItem.LocationPath);

        returned["displayName"]!.GetValue<string>().Should().Be("Cached Links");
        JsonObject studentReference = returned["studentReference"]!.AsObject();
        studentReference["studentUniqueId"]!.GetValue<string>().Should().Be("cache-links-student");
        studentReference
            .ContainsKey("link")
            .Should()
            .BeFalse("ResourceLinks disabled must strip link subtrees from cached JSON");
    }

    public static async Task It_does_not_serve_cached_body_when_read_authorization_is_denied(
        ApiIntegrationHarness harness
    )
    {
        await SetTrackingLifecycleAsync(harness);

        string locationPath = await PostStudentAsync(harness, "cache-denied-001", "Relational Denied");
        var documentUuid = Guid.Parse(locationPath.Split('/')[^1]);
        DocumentMetadata metadata = await ReadDocumentMetadataAsync(harness, documentUuid);
        JsonObject cacheDocument = new()
        {
            ["id"] = documentUuid.ToString(),
            ["studentUniqueId"] = "cache-denied-001",
            ["firstName"] = "Cached Denied",
            ["_lastModifiedDate"] = FormatLastModifiedDate(metadata.ContentLastModifiedAt),
        };
        await UpsertCacheRowAsync(harness, metadata, cacheDocument);

        using HttpResponseMessage deniedResponse = await harness.HttpClient.GetAsync(locationPath);
        string deniedBody = await deniedResponse.Content.ReadAsStringAsync();

        deniedResponse.StatusCode.Should().Be(HttpStatusCode.Forbidden, deniedBody);
        deniedResponse.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");
        deniedBody.Should().NotContain("Cached Denied");
    }

    private static async Task<CreatedDocument> CreateStudentAsync(
        ApiIntegrationHarness harness,
        string studentUniqueId,
        string firstName
    )
    {
        string locationPath = await PostStudentAsync(harness, studentUniqueId, firstName);
        return await ReadCreatedDocumentAsync(harness, locationPath);
    }

    private static async Task<string> PostStudentAsync(
        ApiIntegrationHarness harness,
        string studentUniqueId,
        string firstName
    )
    {
        var payload = new JsonObject { ["studentUniqueId"] = studentUniqueId, ["firstName"] = firstName };
        using HttpResponseMessage createResponse = await PostJsonAsync(harness, StudentsEndpoint, payload);
        string createBody = await createResponse.Content.ReadAsStringAsync();
        createResponse.StatusCode.Should().Be(HttpStatusCode.Created, createBody);
        createResponse.Headers.Location.Should().NotBeNull();
        return ToPath(createResponse.Headers.Location!);
    }

    private static async Task<CreatedDocument> CreateProfileRootOnlyMergeItemAsync(
        ApiIntegrationHarness harness,
        int itemId,
        string displayName,
        string clearableText,
        string preservedText,
        string? studentUniqueId = null
    )
    {
        var payload = new JsonObject
        {
            ["profileRootOnlyMergeItemId"] = itemId,
            ["displayName"] = displayName,
            ["profileScope"] = new JsonObject
            {
                ["clearableText"] = clearableText,
                ["preservedText"] = preservedText,
            },
        };

        if (studentUniqueId is not null)
        {
            payload["studentReference"] = new JsonObject { ["studentUniqueId"] = studentUniqueId };
        }

        using HttpResponseMessage createResponse = await PostJsonAsync(
            harness,
            ProfileRootOnlyMergeItemsEndpoint,
            payload
        );
        string createBody = await createResponse.Content.ReadAsStringAsync();
        createResponse.StatusCode.Should().Be(HttpStatusCode.Created, createBody);
        createResponse.Headers.Location.Should().NotBeNull();

        return await ReadCreatedDocumentAsync(harness, ToPath(createResponse.Headers.Location!));
    }

    private static async Task<CreatedDocument> CreateSchoolTypeDescriptorAsync(
        ApiIntegrationHarness harness,
        string? namespaceName = null,
        string? codeValue = null,
        string shortDescription = "Relational school type"
    )
    {
        string suffix = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        string resolvedNamespace = namespaceName ?? $"uri://ed-fi.org/SchoolTypeDescriptor/DMS-1315/{suffix}";
        var payload = new JsonObject
        {
            ["namespace"] = resolvedNamespace,
            ["codeValue"] = codeValue ?? $"DMS-1315-{suffix[..12]}",
            ["shortDescription"] = shortDescription,
        };

        using HttpResponseMessage createResponse = await PostJsonAsync(
            harness,
            SchoolTypeDescriptorsEndpoint,
            payload
        );
        string createBody = await createResponse.Content.ReadAsStringAsync();
        createResponse.StatusCode.Should().Be(HttpStatusCode.Created, createBody);
        createResponse.Headers.Location.Should().NotBeNull();

        return await ReadCreatedDocumentAsync(harness, ToPath(createResponse.Headers.Location!));
    }

    private static async Task<CreatedDocument> ReadCreatedDocumentAsync(
        ApiIntegrationHarness harness,
        string locationPath
    )
    {
        using HttpResponseMessage response = await harness.HttpClient.GetAsync(locationPath);
        string body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        response.Content.Headers.ContentType?.MediaType.Should().Be(StandardJsonContentType);
        JsonObject document = JsonNode.Parse(body)!.AsObject();
        var documentUuid = Guid.Parse(document["id"]!.GetValue<string>());
        return new CreatedDocument(locationPath, documentUuid, document);
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
        AssertHeaderAndBodyEtagMatch(document, response);
        return document;
    }

    private static async Task<JsonArray> GetJsonArrayAsync(
        ApiIntegrationHarness harness,
        string endpoint,
        int? expectedTotalCount = null
    )
    {
        using HttpResponseMessage response = await harness.HttpClient.GetAsync(endpoint);
        string body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        response.Content.Headers.ContentType?.MediaType.Should().Be(StandardJsonContentType);
        AssertNoCacheAccelerationDisclosure(response, body);

        if (expectedTotalCount is not null)
        {
            response
                .Headers.GetValues("Total-Count")
                .Single()
                .Should()
                .Be(expectedTotalCount.Value.ToString(CultureInfo.InvariantCulture));
        }

        return JsonNode.Parse(body)!.AsArray();
    }

    private static async Task<HttpResponseMessage> SendProfiledGetAsync(
        ApiIntegrationHarness harness,
        string locationPath
    )
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, locationPath);
        request.Headers.Accept.Clear();
        request.Headers.Accept.Add(MediaTypeWithQualityHeaderValue.Parse(VisibleReadableContentType));
        return await harness.HttpClient.SendAsync(request);
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

    private static async Task SetTrackingLifecycleAsync(ApiIntegrationHarness harness)
    {
        await ExecuteNonQueryAsync(
            harness,
            """
            UPDATE "dms"."DocumentCacheState"
            SET "ProjectionLifecycleState" = @lifecycleState,
                "CacheAheadRecoveryRequired" = @cacheAheadRecoveryRequired
            WHERE "StateId" = 1;
            """,
            ("@lifecycleState", "Tracking"),
            ("@cacheAheadRecoveryRequired", false)
        );
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
        (await reader.ReadAsync()).Should().BeTrue("the created document row must exist");

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

    private static async Task UpsertCacheRowAsync(
        ApiIntegrationHarness harness,
        DocumentMetadata metadata,
        JsonObject documentJson,
        long? contentVersionOverride = null
    )
    {
        await using DbCommand command = harness.DbConnection.CreateCommand();
        command.CommandText = UsesPostgresql(harness)
            ? """
                INSERT INTO "dms"."DocumentCache" (
                    "DocumentId",
                    "DocumentUuid",
                    "ProjectName",
                    "ResourceName",
                    "ResourceVersion",
                    "ContentVersion",
                    "StreamEtag",
                    "LastModifiedAt",
                    "DocumentJson",
                    "ComputedAt"
                )
                VALUES (
                    @documentId,
                    @documentUuid,
                    @projectName,
                    @resourceName,
                    @resourceVersion,
                    @contentVersion,
                    @streamEtag,
                    @lastModifiedAt,
                    CAST(@documentJson AS jsonb),
                    @computedAt
                )
                ON CONFLICT ("DocumentId") DO UPDATE
                SET "DocumentUuid" = EXCLUDED."DocumentUuid",
                    "ProjectName" = EXCLUDED."ProjectName",
                    "ResourceName" = EXCLUDED."ResourceName",
                    "ResourceVersion" = EXCLUDED."ResourceVersion",
                    "ContentVersion" = EXCLUDED."ContentVersion",
                    "StreamEtag" = EXCLUDED."StreamEtag",
                    "LastModifiedAt" = EXCLUDED."LastModifiedAt",
                    "DocumentJson" = EXCLUDED."DocumentJson",
                    "ComputedAt" = EXCLUDED."ComputedAt";
                """
            : """
                MERGE [dms].[DocumentCache] WITH (HOLDLOCK) AS target
                USING (
                    SELECT
                        @documentId AS [DocumentId],
                        @documentUuid AS [DocumentUuid],
                        @projectName AS [ProjectName],
                        @resourceName AS [ResourceName],
                        @resourceVersion AS [ResourceVersion],
                        @contentVersion AS [ContentVersion],
                        @streamEtag AS [StreamEtag],
                        @lastModifiedAt AS [LastModifiedAt],
                        @documentJson AS [DocumentJson],
                        @computedAt AS [ComputedAt]
                ) AS source
                ON target.[DocumentId] = source.[DocumentId]
                WHEN MATCHED THEN
                    UPDATE SET
                        [DocumentUuid] = source.[DocumentUuid],
                        [ProjectName] = source.[ProjectName],
                        [ResourceName] = source.[ResourceName],
                        [ResourceVersion] = source.[ResourceVersion],
                        [ContentVersion] = source.[ContentVersion],
                        [StreamEtag] = source.[StreamEtag],
                        [LastModifiedAt] = source.[LastModifiedAt],
                        [DocumentJson] = source.[DocumentJson],
                        [ComputedAt] = source.[ComputedAt]
                WHEN NOT MATCHED THEN
                    INSERT (
                        [DocumentId],
                        [DocumentUuid],
                        [ProjectName],
                        [ResourceName],
                        [ResourceVersion],
                        [ContentVersion],
                        [StreamEtag],
                        [LastModifiedAt],
                        [DocumentJson],
                        [ComputedAt]
                    )
                    VALUES (
                        source.[DocumentId],
                        source.[DocumentUuid],
                        source.[ProjectName],
                        source.[ResourceName],
                        source.[ResourceVersion],
                        source.[ContentVersion],
                        source.[StreamEtag],
                        source.[LastModifiedAt],
                        source.[DocumentJson],
                        source.[ComputedAt]
                    );
                """;

        long contentVersion = contentVersionOverride ?? metadata.ContentVersion;
        command.Parameters.Add(CreateParameter(command, "@documentId", metadata.DocumentId));
        command.Parameters.Add(CreateParameter(command, "@documentUuid", metadata.DocumentUuid));
        command.Parameters.Add(CreateParameter(command, "@projectName", metadata.ProjectName));
        command.Parameters.Add(CreateParameter(command, "@resourceName", metadata.ResourceName));
        command.Parameters.Add(CreateParameter(command, "@resourceVersion", metadata.ResourceVersion));
        command.Parameters.Add(CreateParameter(command, "@contentVersion", contentVersion));
        command.Parameters.Add(
            CreateParameter(command, "@streamEtag", ComposeStreamEtag(metadata, contentVersion))
        );
        command.Parameters.Add(CreateParameter(command, "@lastModifiedAt", metadata.ContentLastModifiedAt));
        command.Parameters.Add(CreateParameter(command, "@documentJson", documentJson.ToJsonString()));
        command.Parameters.Add(CreateParameter(command, "@computedAt", DateTimeOffset.UtcNow));

        await command.ExecuteNonQueryAsync();
    }

    private static JsonObject CacheDocumentFrom(JsonObject publicDocument, DocumentMetadata metadata)
    {
        JsonObject cacheDocument = JsonNode.Parse(publicDocument.ToJsonString())!.AsObject();
        cacheDocument.Remove("_etag");
        cacheDocument["_lastModifiedDate"] = FormatLastModifiedDate(metadata.ContentLastModifiedAt);
        return cacheDocument;
    }

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

    private static async Task DeleteCacheRowsAsync(ApiIntegrationHarness harness, params long[] documentIds)
    {
        if (documentIds.Length == 0)
        {
            return;
        }

        await using DbCommand command = harness.DbConnection.CreateCommand();
        string parameterList = string.Join(", ", documentIds.Select((_, index) => $"@documentId{index}"));
        command.CommandText = $"""
            DELETE FROM "dms"."DocumentCache"
            WHERE "DocumentId" IN ({parameterList});
            """;

        for (int index = 0; index < documentIds.Length; index++)
        {
            command.Parameters.Add(CreateParameter(command, $"@documentId{index}", documentIds[index]));
        }

        await command.ExecuteNonQueryAsync();
    }

    private static async Task ReplaceProjectionWorkAsync(
        ApiIntegrationHarness harness,
        DocumentMetadata metadata
    )
    {
        await ExecuteNonQueryAsync(
            harness,
            """
            DELETE FROM "dms"."DocumentProjectionWork"
            WHERE "DocumentId" = @documentId;

            INSERT INTO "dms"."DocumentProjectionWork" (
                "DocumentId",
                "RequiredContentVersion",
                "FirstEnqueuedAt",
                "LastEnqueuedAt"
            )
            VALUES (
                @documentId,
                @requiredContentVersion,
                @enqueuedAt,
                @enqueuedAt
            );
            """,
            ("@documentId", metadata.DocumentId),
            ("@requiredContentVersion", metadata.ContentVersion),
            ("@enqueuedAt", DateTimeOffset.UtcNow)
        );
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

    private static async Task<DocumentCacheRow> ReadCacheRowAsync(
        ApiIntegrationHarness harness,
        long documentId
    )
    {
        await using DbCommand command = harness.DbConnection.CreateCommand();
        command.CommandText = """
            SELECT "DocumentId",
                   "ContentVersion",
                   "StreamEtag",
                   "LastModifiedAt",
                   "DocumentJson"
            FROM "dms"."DocumentCache"
            WHERE "DocumentId" = @documentId;
            """;
        command.Parameters.Add(CreateParameter(command, "@documentId", documentId));

        await using DbDataReader reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue("direct fill should write one cache row");

        string documentJson =
            Convert.ToString(reader.GetValue(4), CultureInfo.InvariantCulture)
            ?? throw new InvalidOperationException("DocumentCache.DocumentJson was null.");
        var row = new DocumentCacheRow(
            Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture),
            Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture),
            reader.GetString(2),
            ToUtcDateTimeOffset(reader.GetValue(3)),
            JsonNode.Parse(documentJson)!.AsObject()
        );

        (await reader.ReadAsync()).Should().BeFalse("DocumentCache.DocumentId is unique");
        return row;
    }

    private static void AssertDirectFilledStudentCacheRow(
        DocumentCacheRow row,
        DocumentMetadata metadata,
        string studentUniqueId,
        string firstName
    )
    {
        row.DocumentId.Should().Be(metadata.DocumentId);
        row.ContentVersion.Should().Be(metadata.ContentVersion);
        row.StreamEtag.Should().Be(ComposeStreamEtag(metadata, metadata.ContentVersion));
        row.LastModifiedAt.Should().BeCloseTo(metadata.ContentLastModifiedAt, TimeSpan.FromTicks(10));
        row.DocumentJson["id"]!.GetValue<string>().Should().Be(metadata.DocumentUuid.ToString());
        row.DocumentJson["studentUniqueId"]!.GetValue<string>().Should().Be(studentUniqueId);
        row.DocumentJson["firstName"]!.GetValue<string>().Should().Be(firstName);
        row.DocumentJson["_lastModifiedDate"]!
            .GetValue<string>()
            .Should()
            .Be(FormatLastModifiedDate(metadata.ContentLastModifiedAt));
        row.DocumentJson.ContainsKey("_etag").Should().BeFalse("cache JSON stores fixed stream content");
    }

    private static DbParameter CreateParameter(DbCommand command, string name, object? value)
    {
        DbParameter parameter = command.CreateParameter();
        parameter.ParameterName = name;

        if (parameter is SqlParameter sqlParameter && value is DateTimeOffset dateTimeOffset)
        {
            sqlParameter.SqlDbType = SqlDbType.DateTime2;
            sqlParameter.Value = dateTimeOffset.UtcDateTime;
            return parameter;
        }

        parameter.Value = value switch
        {
            null => DBNull.Value,
            _ => value,
        };
        return parameter;
    }

    private static bool UsesPostgresql(ApiIntegrationHarness harness) =>
        harness.DbConnection.GetType().Namespace?.Contains("Npgsql", StringComparison.Ordinal) == true;

    private static string ComposeStreamEtag(DocumentMetadata metadata, long contentVersion) =>
        new ServedEtagComposer().Compose(
            new ServedEtagContext(
                metadata.EffectiveSchemaHash,
                ResponseFormat.Json,
                ProfileName: null,
                LinksEnabled: !metadata.ResourceName.EndsWith("Descriptor", StringComparison.Ordinal),
                contentVersion,
                ResponseContentCoding.Identity
            )
        );

    private static DateTimeOffset ToUtcDateTimeOffset(object value) =>
        value switch
        {
            DateTimeOffset dateTimeOffset => dateTimeOffset.ToUniversalTime(),
            DateTime dateTime => new DateTimeOffset(DateTime.SpecifyKind(dateTime, DateTimeKind.Utc)),
            _ => throw new InvalidOperationException(
                $"Unsupported ContentLastModifiedAt value type '{value.GetType().Name}'."
            ),
        };

    private static string FormatLastModifiedDate(DateTimeOffset lastModifiedAt) =>
        lastModifiedAt.UtcDateTime.ToString(LastModifiedDateFormat, CultureInfo.InvariantCulture);

    private static string ToPath(Uri location) =>
        location.IsAbsoluteUri ? location.AbsolutePath : location.OriginalString;

    private static string Escape(string value) => Uri.EscapeDataString(value);

    private static void AssertHeaderAndBodyEtagMatch(JsonObject document, HttpResponseMessage response)
    {
        response.TryReadRawEtag(out string headerEtag).Should().BeTrue("successful reads must emit ETag");
        document["_etag"]!.GetValue<string>().Should().Be(headerEtag);
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

    private static void AssertReadTelemetryContains(
        ApiIntegrationHarness harness,
        string eventName,
        string outcome,
        string because
    )
    {
        DocumentCacheReadTelemetryRecorder? recorder = harness.DocumentCacheReadTelemetryRecorder;
        if (recorder is null)
        {
            return;
        }

        string[] records = recorder
            .TelemetryRecords.Select(record => $"{record.EventName}:{record.Operation}:{record.Outcome}")
            .ToArray();
        recorder
            .CountTelemetryRecords(eventName, outcome)
            .Should()
            .BeGreaterThan(0, $"{because}. Recorded telemetry: {string.Join(", ", records)}");
    }

    private sealed record CreatedDocument(string LocationPath, Guid DocumentUuid, JsonObject Body);

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
        long ContentVersion,
        string StreamEtag,
        DateTimeOffset LastModifiedAt,
        JsonObject DocumentJson
    );
}
