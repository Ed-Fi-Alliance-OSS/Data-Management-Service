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
    private const int DocumentCacheRejectedNoMutationExitCode = 10;
    private const string CacheAheadUnsafeStudentFirstName = "cache-ahead unsafe student sentinel";

    [Test]
    [Category("DocumentCacheHostedCacheAhead")]
    [Category("DocumentCacheCacheAhead")]
    public async Task It_fails_closed_when_the_hosted_cache_ahead_latch_is_set()
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

        JsonObject activation = await RunDocumentCacheAdminWithHostReachableDataStoreAsync(
            dataStoreId,
            [
                "activate-new-empty",
                "--data-store-id",
                dataStoreId.ToString(CultureInfo.InvariantCulture),
                "--confirm",
                "newEmptyActivation",
                "--json",
                "--datastore",
                CliDatastoreOptionValue(),
                "--command-timeout-seconds",
                "90",
            ]
        );
        activation["command"]!.GetValue<string>().Should().Be("guardedNewEmptyActivation");
        activation["status"]!.GetValue<string>().Should().Be("completed");
        activation["classification"]!.GetValue<string>().Should().Be("succeeded");

        await WaitForDocumentCacheCaughtUpAsync(dataStoreId);

        string studentUniqueId = $"doc-cache-cache-ahead-{Guid.NewGuid():N}"[..32];
        const string originalFirstName = "Cache Ahead Source";
        Guid studentId = await PostStudentAsync(studentUniqueId, originalFirstName);
        await WaitForDocumentCacheCaughtUpAsync(dataStoreId);

        DocumentCacheProjection originalProjection = await ReadDocumentCacheProjectionAsync(studentId);
        originalProjection.CacheContentVersion.Should().Be(originalProjection.DocumentContentVersion);
        originalProjection.WorkRows.Should().Be(0);

        DocumentCacheProjection cacheAheadProjection = await CreateCacheAheadIncidentAsync(
            studentId,
            CacheAheadUnsafeStudentFirstName
        );
        long unsafeCacheContentVersion = cacheAheadProjection.CacheContentVersion;

        JsonObject latchedTarget = await WaitForCacheAheadRecoveryRequiredStatusAsync(
            dataStoreId,
            expectedQueuePresence: "empty"
        );
        AssertCacheAheadRecoveryRequiredStatus(latchedTarget, expectedQueuePresence: "empty");

        await AssertGetByIdAsync(studentId, studentUniqueId, originalFirstName);
        await AssertGetManyAsync(studentUniqueId, originalFirstName);
        await AssertCanonicalStudentFirstNameAsync(studentId, originalFirstName);
        await AssertCacheAheadEvidenceRemainsStableAsync(
            studentId,
            unsafeCacheContentVersion,
            expectedWorkRows: 0,
            TimeSpan.FromSeconds(2)
        );

        const string updatedFirstName = "Cache Ahead Canonical Update";
        await PutStudentAsync(studentId, studentUniqueId, updatedFirstName);
        DocumentCacheQueuedState queuedState = await WaitForCacheAheadProjectionWorkAsync(
            studentId,
            unsafeCacheContentVersion
        );
        queuedState.WorkRequiredContentVersion.Should().Be(queuedState.DocumentContentVersion);
        queuedState.DocumentContentVersion.Should().BeLessThan(unsafeCacheContentVersion);

        JsonObject latchedBacklogTarget = await WaitForCacheAheadRecoveryRequiredStatusAsync(
            dataStoreId,
            expectedQueuePresence: "notEmpty"
        );
        AssertCacheAheadRecoveryRequiredStatus(latchedBacklogTarget, expectedQueuePresence: "notEmpty");

        await AssertGetByIdAsync(studentId, studentUniqueId, updatedFirstName);
        await AssertGetManyAsync(studentUniqueId, updatedFirstName);
        await AssertCanonicalStudentFirstNameAsync(studentId, updatedFirstName);
        await AssertCacheAheadEvidenceRemainsStableAsync(
            studentId,
            unsafeCacheContentVersion,
            expectedWorkRows: 1,
            TimeSpan.FromSeconds(3)
        );

        JsonObject rebuild = await RunDocumentCacheRebuildOnlineExpectingCacheAheadRejectionAsync(
            dataStoreId
        );
        rebuild["command"]!.GetValue<string>().Should().Be("onlineCacheRebuild");
        AssertRejectedNoMutationResult(
            rebuild,
            expectedClassification: "cacheAheadLatchSet",
            expectedLifecycle: "tracking",
            expectedCacheAheadRecoveryRequired: true
        );
        await AssertCacheAheadEvidenceRemainsStableAsync(
            studentId,
            unsafeCacheContentVersion,
            expectedWorkRows: 1,
            TimeSpan.FromSeconds(1)
        );

        JsonObject statusTarget = await RunDocumentCacheStatusCliForTargetAsync(dataStoreId);
        JsonObject recovery = await RunDocumentCacheRecoverCacheAheadExpectingUnknownHistoryRejectionAsync(
            dataStoreId,
            statusTarget["physicalSourceFingerprint"]!.GetValue<string>()
        );
        recovery["command"]!.GetValue<string>().Should().Be("internalOnlyCacheAheadRecovery");
        AssertRejectedNoMutationResult(
            recovery,
            expectedClassification: "downstreamHistoryPresentOrUnknown",
            expectedLifecycle: "tracking",
            expectedCacheAheadRecoveryRequired: true
        );
        await AssertCacheAheadEvidenceRemainsStableAsync(
            studentId,
            unsafeCacheContentVersion,
            expectedWorkRows: 1,
            TimeSpan.FromSeconds(1)
        );
    }

    private async Task<JsonObject> RunDocumentCacheRebuildOnlineExpectingCacheAheadRejectionAsync(
        int dataStoreId
    )
    {
        WriteRunbookCommandTranscript(RunbookRebuildOnlineTranscriptLabel, dataStoreId);
        return await RunDocumentCacheAdminWithHostReachableDataStoreAsync(
            dataStoreId,
            DocumentCacheRejectedNoMutationExitCode,
            [
                "rebuild-online",
                "--data-store-id",
                dataStoreId.ToString(CultureInfo.InvariantCulture),
                "--confirm",
                "onlineCacheRebuild",
                "--json",
                "--datastore",
                CliDatastoreOptionValue(),
                "--command-timeout-seconds",
                "60",
            ]
        );
    }

    private async Task<JsonObject> RunDocumentCacheRecoverCacheAheadExpectingUnknownHistoryRejectionAsync(
        int dataStoreId,
        string expectedPhysicalSourceFingerprint
    )
    {
        WriteRunbookCommandTranscript("dms-document-cache recover-cache-ahead", dataStoreId);
        return await RunDocumentCacheAdminWithHostReachableDataStoreAsync(
            dataStoreId,
            DocumentCacheRejectedNoMutationExitCode,
            [
                "recover-cache-ahead",
                "--data-store-id",
                dataStoreId.ToString(CultureInfo.InvariantCulture),
                "--confirm",
                "internalCacheAheadRecovery",
                "--offline-writer-admission",
                "closedAndDrained",
                "--expected-physical-source-fingerprint",
                expectedPhysicalSourceFingerprint,
                "--json",
                "--datastore",
                CliDatastoreOptionValue(),
                "--command-timeout-seconds",
                "60",
            ]
        );
    }

    private static void AssertRejectedNoMutationResult(
        JsonObject commandResult,
        string expectedClassification,
        string expectedLifecycle,
        bool expectedCacheAheadRecoveryRequired
    )
    {
        commandResult["status"]!.GetValue<string>().Should().Be("rejectedNoMutation");
        commandResult["classification"]!.GetValue<string>().Should().Be(expectedClassification);
        commandResult["mutated"]!.GetValue<bool>().Should().BeFalse();
        commandResult["lifecycle"]!.GetValue<string>().Should().Be(expectedLifecycle);
        commandResult["cacheAheadRecoveryRequired"]!
            .GetValue<bool>()
            .Should()
            .Be(expectedCacheAheadRecoveryRequired);
    }

    private async Task<JsonObject> WaitForCacheAheadRecoveryRequiredStatusAsync(
        long dataStoreId,
        string expectedQueuePresence
    )
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        string lastStatus = string.Empty;

        while (DateTimeOffset.UtcNow < deadline)
        {
            JsonObject status = await GetDocumentCacheStatusAsync();
            JsonObject target = TargetByDataStoreId(status, dataStoreId);
            lastStatus = status.ToJsonString();

            if (
                ReadString(target, "lifecycle", "state") == "tracking"
                && ReadString(target, "cacheAhead", "state") == "recoveryRequired"
                && ReadBool(target, "cacheAhead", "recoveryRequired")
                && ReadString(target, "queueSummary", "presence") == expectedQueuePresence
                && ReadString(target, "operationalHealth", "status") == "nonOperational"
                && ReadString(target, "operationalHealth", "reason") == "cacheAheadRecoveryRequired"
                && ReadString(target, "caughtUp", "status") == "notCaughtUp"
                && ReadString(target, "caughtUp", "reason") == "cacheAheadRecoveryRequired"
            )
            {
                return target;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500));
        }

        throw new AssertionException(
            $"DocumentCache target {dataStoreId} did not report cache-ahead recovery-required with queue {expectedQueuePresence}. Last status: {lastStatus}"
        );
    }

    private static void AssertCacheAheadRecoveryRequiredStatus(
        JsonObject target,
        string expectedQueuePresence
    )
    {
        ReadString(target, "lifecycle", "state").Should().Be("tracking");
        ReadString(target, "cacheAhead", "state").Should().Be("recoveryRequired");
        ReadBool(target, "cacheAhead", "recoveryRequired").Should().BeTrue();
        ReadString(target, "queueSummary", "presence").Should().Be(expectedQueuePresence);
        ReadString(target, "operationalHealth", "status").Should().Be("nonOperational");
        ReadString(target, "operationalHealth", "reason").Should().Be("cacheAheadRecoveryRequired");
        ReadString(target, "caughtUp", "status").Should().Be("notCaughtUp");
        ReadString(target, "caughtUp", "reason").Should().Be("cacheAheadRecoveryRequired");
    }

    private static async Task<DocumentCacheProjection> CreateCacheAheadIncidentAsync(
        Guid documentUuid,
        string unsafeFirstName
    )
    {
        DocumentCacheProjection originalProjection = await ReadDocumentCacheProjectionAsync(documentUuid);
        originalProjection.CacheContentVersion.Should().Be(originalProjection.DocumentContentVersion);
        originalProjection.WorkRows.Should().Be(0);

        JsonObject documentJson = ParseDocumentJsonObject(originalProjection.DocumentJson);
        documentJson["firstName"] = unsafeFirstName;
        documentJson.ContainsKey("_etag").Should().BeFalse("DocumentJson stores fixed stream content");

        long cacheAheadContentVersion = originalProjection.DocumentContentVersion + 1_000_000;
        await using DbConnection connection = CreateDataStoreConnection();
        await connection.OpenAsync();

        await using (DbCommand cacheCommand = connection.CreateCommand())
        {
            cacheCommand.CommandText = IsMssql()
                ? """
                    UPDATE [dms].[DocumentCache]
                    SET [ContentVersion] = @contentVersion,
                        [DocumentJson] = @documentJson
                    WHERE [DocumentId] = @documentId;
                    """
                : """
                    UPDATE "dms"."DocumentCache"
                    SET "ContentVersion" = @contentVersion,
                        "DocumentJson" = CAST(@documentJson AS jsonb)
                    WHERE "DocumentId" = @documentId;
                    """;
            AddParameter(cacheCommand, "@documentId", originalProjection.DocumentId);
            AddParameter(cacheCommand, "@contentVersion", cacheAheadContentVersion);
            AddParameter(cacheCommand, "@documentJson", documentJson.ToJsonString());

            int cacheRows = await cacheCommand.ExecuteNonQueryAsync();
            cacheRows.Should().Be(1, "cache-ahead setup should update exactly one cache row");
        }

        await using (DbCommand stateCommand = connection.CreateCommand())
        {
            stateCommand.CommandText = IsMssql()
                ? """
                    UPDATE [dms].[DocumentCacheState]
                    SET [ProjectionLifecycleState] = 'Tracking',
                        [CacheAheadRecoveryRequired] = CAST(1 AS bit)
                    WHERE [StateId] = 1;
                    """
                : """
                    UPDATE "dms"."DocumentCacheState"
                    SET "ProjectionLifecycleState" = 'Tracking',
                        "CacheAheadRecoveryRequired" = true
                    WHERE "StateId" = 1;
                    """;

            int stateRows = await stateCommand.ExecuteNonQueryAsync();
            stateRows.Should().Be(1, "cache-ahead setup should latch the singleton state row");
        }

        DocumentCacheProjection cacheAheadProjection = await ReadDocumentCacheProjectionAsync(documentUuid);
        cacheAheadProjection.CacheContentVersion.Should().Be(cacheAheadContentVersion);
        cacheAheadProjection
            .CacheContentVersion.Should()
            .BeGreaterThan(cacheAheadProjection.DocumentContentVersion);
        cacheAheadProjection.DocumentJson.Should().Contain(unsafeFirstName);

        HostedDocumentCacheState state = await ReadHostedDocumentCacheStateAsync();
        state.ProjectionLifecycleState.Should().Be("Tracking");
        state.CacheAheadRecoveryRequired.Should().BeTrue();

        return cacheAheadProjection;
    }

    private static async Task<DocumentCacheQueuedState> WaitForCacheAheadProjectionWorkAsync(
        Guid documentUuid,
        long expectedCacheContentVersion
    )
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(20);
        DocumentCacheQueuedState? lastState = null;

        while (DateTimeOffset.UtcNow < deadline)
        {
            lastState = await ReadDocumentCacheQueuedStateAsync(documentUuid);
            if (
                lastState.WorkRequiredContentVersion == lastState.DocumentContentVersion
                && lastState.CacheContentVersion == expectedCacheContentVersion
                && lastState.DocumentContentVersion < expectedCacheContentVersion
            )
            {
                return lastState;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250));
        }

        throw new AssertionException(
            $"DocumentCache work did not remain queued under cache-ahead latch. Last state: {lastState}"
        );
    }

    private static async Task AssertCacheAheadEvidenceRemainsStableAsync(
        Guid documentUuid,
        long expectedCacheContentVersion,
        long expectedWorkRows,
        TimeSpan stableDuration
    )
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.Add(stableDuration);
        do
        {
            DocumentCacheProjection projection = await ReadDocumentCacheProjectionAsync(documentUuid);
            projection.CacheContentVersion.Should().Be(expectedCacheContentVersion);
            projection.CacheContentVersion.Should().BeGreaterThan(projection.DocumentContentVersion);
            projection.WorkRows.Should().Be(expectedWorkRows);
            ParseDocumentJsonObject(projection.DocumentJson)["firstName"]!
                .GetValue<string>()
                .Should()
                .Be(CacheAheadUnsafeStudentFirstName);

            HostedDocumentCacheState state = await ReadHostedDocumentCacheStateAsync();
            state.ProjectionLifecycleState.Should().Be("Tracking");
            state.CacheAheadRecoveryRequired.Should().BeTrue();

            await Task.Delay(TimeSpan.FromMilliseconds(250));
        } while (DateTimeOffset.UtcNow < deadline);
    }

    private static async Task<HostedDocumentCacheState> ReadHostedDocumentCacheStateAsync()
    {
        await using DbConnection connection = CreateDataStoreConnection();
        await connection.OpenAsync();
        await using DbCommand command = connection.CreateCommand();
        command.CommandText = IsMssql()
            ? """
                SELECT [ProjectionLifecycleState], [CacheAheadRecoveryRequired]
                FROM [dms].[DocumentCacheState]
                WHERE [StateId] = 1;
                """
            : """
                SELECT "ProjectionLifecycleState", "CacheAheadRecoveryRequired"
                FROM "dms"."DocumentCacheState"
                WHERE "StateId" = 1;
                """;

        await using DbDataReader reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            throw new AssertionException("dms.DocumentCacheState singleton row was not found.");
        }

        return new HostedDocumentCacheState(
            ReadRequiredString(reader, "ProjectionLifecycleState"),
            ReadRequiredBoolean(reader, "CacheAheadRecoveryRequired")
        );
    }

    private static bool ReadRequiredBoolean(DbDataReader reader, string name)
    {
        object value = reader[name];
        value.Should().NotBe(DBNull.Value, $"{name} should not be null");
        return Convert.ToBoolean(value, CultureInfo.InvariantCulture);
    }

    private sealed record HostedDocumentCacheState(
        string ProjectionLifecycleState,
        bool CacheAheadRecoveryRequired
    );
}
