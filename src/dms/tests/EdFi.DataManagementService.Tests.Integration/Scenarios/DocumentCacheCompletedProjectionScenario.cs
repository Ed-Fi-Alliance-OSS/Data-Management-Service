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
    private const string StandardJsonContentType = "application/json";

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

        JsonObject staleAvoidanceGet = await GetJsonObjectAsync(harness, createdStudent.LocationPath);
        staleAvoidanceGet["firstName"]!
            .GetValue<string>()
            .Should()
            .Be("Projected Update", "read acceleration must not serve the stale cache row");
        staleAvoidanceGet["_etag"]!.GetValue<string>().Should().Be(updateEtag);
        AssertReadTelemetryContains(
            recorder,
            "RecordMiss",
            "StaleCacheRow",
            "the stale cache row should be detected before relational fallback"
        );

        await DrainProjectionUntilIdleAsync(harness);

        (await CountProjectionWorkRowsAsync(harness, createdMetadata.DocumentId))
            .Should()
            .Be(0, "projection or direct fill must acknowledge the update work row");
        DocumentCacheRow updatedCacheRow = await ReadCacheRowAsync(harness, createdMetadata.DocumentId);
        AssertStudentCacheRow(updatedCacheRow, updatedMetadata, studentUniqueId, "Projected Update");

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

        JsonObject staleAvoidanceGet = await GetJsonObjectAsync(harness, createdDescriptor.LocationPath);
        staleAvoidanceGet["shortDescription"]!
            .GetValue<string>()
            .Should()
            .Be(
                "Projected descriptor update",
                "read acceleration must not serve the stale descriptor cache row"
            );
        staleAvoidanceGet["_etag"]!.GetValue<string>().Should().Be(updateEtag);
        AssertReadTelemetryContains(
            recorder,
            "RecordMiss",
            "StaleCacheRow",
            "the stale descriptor cache row should be detected before relational fallback"
        );

        await DrainProjectionUntilIdleAsync(harness);

        (await CountProjectionWorkRowsAsync(harness, createdMetadata.DocumentId))
            .Should()
            .Be(0, "projection or direct fill must acknowledge the descriptor update work row");
        DocumentCacheRow updatedCacheRow = await ReadCacheRowAsync(harness, createdMetadata.DocumentId);
        AssertSchoolTypeDescriptorCacheRow(
            updatedCacheRow,
            updatedMetadata,
            namespaceName,
            codeValue,
            "Projected descriptor update"
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
}
