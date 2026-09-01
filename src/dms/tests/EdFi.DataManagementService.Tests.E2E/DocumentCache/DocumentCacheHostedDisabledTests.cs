// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using System.Globalization;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Tests.E2E.Authorization;
using FluentAssertions;

namespace EdFi.DataManagementService.Tests.E2E.DocumentCache;

public sealed partial class DocumentCacheHostedHappyPathTests
{
    private const string DisabledLifecycleCacheOnlyFirstNameSentinel =
        "disabled cache-only hosted student sentinel";
    private const string LastModifiedDateFormat = "yyyy-MM-ddTHH:mm:ss'Z'";

    [Test]
    [Category("DocumentCacheHostedDisabled")]
    [Category("DocumentCacheDisabled")]
    public async Task It_bypasses_hosted_cache_and_projection_while_lifecycle_is_disabled()
    {
        await RegisterSystemAdministratorAsync();
        int dataStoreId = await GetConfiguredDataStoreIdAsync();
        dataStoreId
            .Should()
            .Be(
                TargetDataStoreId,
                "the DocumentCache hosted E2E target configured in .env.e2e must match the provisioned CMS data store"
            );

        ClientCredentials credentials = await CreateClientCredentialsForDataStoreAsync(dataStoreId);
        string dmsToken = await GetDmsTokenAsync(credentials);
        SetDmsBearerToken(dmsToken);

        JsonObject disabledTarget = await WaitForDocumentCacheDisabledAsync(dataStoreId);
        AssertTargetIsHostedDisabledTarget(disabledTarget, dataStoreId);
        await AssertStudentRouteRemainsAvailableAsync();

        string studentUniqueId = $"doc-cache-disabled-{Guid.NewGuid():N}"[..32];
        const string canonicalFirstName = "Disabled Lifecycle Source";
        Guid studentId = await PostStudentAsync(studentUniqueId, canonicalFirstName);

        await AssertNoProjectionStateForDisabledDocumentAsync(studentId);
        await AssertGetByIdAsync(studentId, studentUniqueId, canonicalFirstName);
        await AssertGetManyAsync(studentUniqueId, canonicalFirstName);
        await AssertNoProjectionStateForDisabledDocumentAsync(studentId);

        await InsertMisleadingDisabledCacheRowAsync(
            studentId,
            studentUniqueId,
            DisabledLifecycleCacheOnlyFirstNameSentinel
        );
        DocumentCacheProjection misleadingProjection = await ReadDocumentCacheProjectionAsync(studentId);
        misleadingProjection.CacheContentVersion.Should().Be(misleadingProjection.DocumentContentVersion);
        misleadingProjection.WorkRows.Should().Be(0);
        misleadingProjection.DocumentJson.Should().Contain(DisabledLifecycleCacheOnlyFirstNameSentinel);
        ParseDocumentJsonObject(misleadingProjection.DocumentJson)
            .ContainsKey("_etag")
            .Should()
            .BeFalse("DocumentJson stores fixed stream content");

        await AssertCanonicalStudentFirstNameAsync(studentId, canonicalFirstName);
        await AssertGetByIdAsync(studentId, studentUniqueId, canonicalFirstName);
        await AssertGetManyAsync(studentUniqueId, canonicalFirstName);
        await AssertCanonicalStudentFirstNameAsync(studentId, canonicalFirstName);

        DocumentCacheProjection stableMisleadingProjection = await ReadDocumentCacheProjectionAsync(
            studentId
        );
        stableMisleadingProjection
            .DocumentJson.Should()
            .Contain(
                DisabledLifecycleCacheOnlyFirstNameSentinel,
                "disabled reads must not direct-fill or overwrite misleading cache evidence"
            );
        stableMisleadingProjection.WorkRows.Should().Be(0);

        JsonObject finalDisabledTarget = TargetByDataStoreId(
            await GetDocumentCacheStatusAsync(),
            dataStoreId
        );
        AssertTargetIsHostedDisabledTarget(finalDisabledTarget, dataStoreId);

        await DeleteStudentAsync(studentId);
        await AssertDocumentCacheRowsDeletedAsync(studentId);
    }

    private async Task<JsonObject> WaitForDocumentCacheDisabledAsync(long dataStoreId)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        string lastStatus = string.Empty;

        while (DateTimeOffset.UtcNow < deadline)
        {
            JsonObject status = await GetDocumentCacheStatusAsync();
            JsonObject target = TargetByDataStoreId(status, dataStoreId);
            lastStatus = status.ToJsonString();

            if (
                ReadString(target, "lifecycle", "state") == "disabled"
                && ReadString(target, "operationalHealth", "status") == "nonOperational"
                && ReadString(target, "operationalHealth", "reason") == "lifecycleDisabled"
            )
            {
                return target;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500));
        }

        throw new AssertionException(
            $"DocumentCache target {dataStoreId} did not report Disabled lifecycle. Last status: {lastStatus}"
        );
    }

    private static void AssertTargetIsHostedDisabledTarget(JsonObject target, long dataStoreId)
    {
        target["targetKey"]!["dataStoreId"]!.GetValue<long>().Should().Be(dataStoreId);
        ReadString(target, "lifecycle", "state").Should().Be("disabled");
        ReadString(target, "operationalHealth", "status").Should().Be("nonOperational");
        ReadString(target, "operationalHealth", "reason").Should().Be("lifecycleDisabled");
        ReadString(target, "caughtUp", "status").Should().NotBe("caughtUp");
        ReadBool(target, "effectiveSettings", "readAcceleration", "enabled").Should().BeTrue();
    }

    private static async Task AssertNoProjectionStateForDisabledDocumentAsync(Guid documentUuid)
    {
        DocumentCacheQueuedState state = await ReadDocumentCacheQueuedStateAsync(documentUuid);
        state
            .WorkRequiredContentVersion.Should()
            .BeNull("Disabled ProjectionLifecycleState must not enqueue projection work");
        state
            .CacheContentVersion.Should()
            .BeNull("Disabled ProjectionLifecycleState must not create cache rows");
    }

    private static async Task InsertMisleadingDisabledCacheRowAsync(
        Guid documentUuid,
        string studentUniqueId,
        string cacheOnlyFirstName
    )
    {
        DisabledDocumentCacheMetadata metadata = await ReadDisabledDocumentCacheMetadataAsync(documentUuid);
        var documentJson = new JsonObject
        {
            ["id"] = metadata.DocumentUuid.ToString(),
            ["studentUniqueId"] = studentUniqueId,
            ["firstName"] = cacheOnlyFirstName,
            ["lastSurname"] = "Cache",
            ["birthDate"] = "2010-05-01",
            ["_lastModifiedDate"] = FormatLastModifiedDate(metadata.ContentLastModifiedAt),
        };

        await using DbConnection connection = CreateDataStoreConnection();
        await connection.OpenAsync();
        await using DbCommand command = connection.CreateCommand();
        command.CommandText = IsMssql()
            ? """
                INSERT INTO [dms].[DocumentCache] (
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
                    @documentId,
                    @documentUuid,
                    @projectName,
                    @resourceName,
                    @resourceVersion,
                    @contentVersion,
                    @streamEtag,
                    @lastModifiedAt,
                    @documentJson,
                    @computedAt
                );
                """
            : """
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
                );
                """;
        AddParameter(command, "@documentId", metadata.DocumentId);
        AddParameter(command, "@documentUuid", metadata.DocumentUuid);
        AddParameter(command, "@projectName", metadata.ProjectName);
        AddParameter(command, "@resourceName", metadata.ResourceName);
        AddParameter(command, "@resourceVersion", metadata.ResourceVersion);
        AddParameter(command, "@contentVersion", metadata.ContentVersion);
        AddParameter(command, "@streamEtag", $"disabled-e2e-{metadata.ContentVersion}");
        AddParameter(command, "@lastModifiedAt", ToProviderDateTime(metadata.ContentLastModifiedAt));
        AddParameter(command, "@documentJson", documentJson.ToJsonString());
        AddParameter(command, "@computedAt", ToProviderDateTime(DateTimeOffset.UtcNow));

        int insertedRows = await command.ExecuteNonQueryAsync();
        insertedRows.Should().Be(1, "misleading disabled cache setup should insert one row");
    }

    private static async Task<DisabledDocumentCacheMetadata> ReadDisabledDocumentCacheMetadataAsync(
        Guid documentUuid
    )
    {
        await using DbConnection connection = CreateDataStoreConnection();
        await connection.OpenAsync();
        await using DbCommand command = connection.CreateCommand();
        command.CommandText = IsMssql()
            ? """
                SELECT
                    d.[DocumentId],
                    d.[DocumentUuid],
                    d.[ContentVersion],
                    d.[ContentLastModifiedAt],
                    rk.[ProjectName],
                    rk.[ResourceName],
                    rk.[ResourceVersion]
                FROM [dms].[Document] AS d
                INNER JOIN [dms].[ResourceKey] AS rk ON rk.[ResourceKeyId] = d.[ResourceKeyId]
                WHERE d.[DocumentUuid] = @documentUuid;
                """
            : """
                SELECT
                    d."DocumentId",
                    d."DocumentUuid",
                    d."ContentVersion",
                    d."ContentLastModifiedAt",
                    rk."ProjectName",
                    rk."ResourceName",
                    rk."ResourceVersion"
                FROM "dms"."Document" AS d
                INNER JOIN "dms"."ResourceKey" AS rk ON rk."ResourceKeyId" = d."ResourceKeyId"
                WHERE d."DocumentUuid" = @documentUuid;
                """;
        AddParameter(command, "@documentUuid", documentUuid);

        await using DbDataReader reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            throw new AssertionException($"Document {documentUuid} was not found in dms.Document.");
        }

        return new DisabledDocumentCacheMetadata(
            DocumentId: ReadInt64(reader, "DocumentId"),
            DocumentUuid: (Guid)reader["DocumentUuid"],
            ContentVersion: ReadInt64(reader, "ContentVersion"),
            ContentLastModifiedAt: ToUtcDateTimeOffset(reader["ContentLastModifiedAt"]),
            ProjectName: ReadRequiredString(reader, "ProjectName"),
            ResourceName: ReadRequiredString(reader, "ResourceName"),
            ResourceVersion: ReadRequiredString(reader, "ResourceVersion")
        );
    }

    private static object ToProviderDateTime(DateTimeOffset value) => IsMssql() ? value.UtcDateTime : value;

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

    private sealed record DisabledDocumentCacheMetadata(
        long DocumentId,
        Guid DocumentUuid,
        long ContentVersion,
        DateTimeOffset ContentLastModifiedAt,
        string ProjectName,
        string ResourceName,
        string ResourceVersion
    );
}
