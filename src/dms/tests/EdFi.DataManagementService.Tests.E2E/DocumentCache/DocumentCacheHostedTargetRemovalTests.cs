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
    [Test]
    [Category("DocumentCacheHostedTargetRemoval")]
    [Category("DocumentCacheTargetRemoval")]
    public async Task It_pauses_projection_when_the_hosted_target_is_removed_and_resumes_after_readd()
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
        activation["lifecycle"]!.GetValue<string>().Should().Be("tracking");

        await WaitForDocumentCacheCaughtUpAsync(dataStoreId);

        int removedTargetDataStoreId = dataStoreId + 10_000;
        bool restoredConfiguredTarget = false;
        try
        {
            await RestartDmsWithDocumentCacheTargetDataStoreIdAsync(removedTargetDataStoreId);
            SetDmsBearerToken(dmsToken);

            JsonObject targetlessStatus = await WaitForDocumentCacheTargetAbsentAsync(dataStoreId);
            FindTargetByDataStoreId(targetlessStatus, dataStoreId)
                .Should()
                .BeNull("the hosted runtime target for the E2E data store has been removed");

            DocumentCacheDurableState targetlessState = await ReadDocumentCacheDurableStateAsync();
            targetlessState.ProjectionLifecycleState.Should().Be("Tracking");
            targetlessState.CacheAheadRecoveryRequired.Should().BeFalse();

            await AssertStudentRouteRemainsAvailableAsync();

            List<HostedOutageStudent> students = [];
            for (int index = 1; index <= 3; index++)
            {
                string studentUniqueId = $"doc-cache-target-gap-{Guid.NewGuid():N}"[..32];
                Guid studentId = await PostStudentAsync(studentUniqueId, $"Target Gap Create {index}");
                string updatedFirstName = $"Target Gap Update {index}";
                await PutStudentAsync(studentId, studentUniqueId, updatedFirstName);
                students.Add(new HostedOutageStudent(studentId, studentUniqueId, updatedFirstName));
            }

            Guid[] studentIds = students.Select(student => student.Id).ToArray();
            IReadOnlyList<DocumentCacheQueuedState> queuedStates = await WaitForQueuedProjectionWorkAsync(
                studentIds
            );
            queuedStates.Should().HaveCount(students.Count);
            queuedStates
                .Should()
                .OnlyContain(state =>
                    state.WorkRequiredContentVersion == state.DocumentContentVersion
                    && !state.CacheContentVersion.HasValue
                );

            foreach (HostedOutageStudent student in students)
            {
                await AssertGetByIdAsync(student.Id, student.UniqueId, student.ExpectedFirstName);
                await AssertGetManyAsync(student.UniqueId, student.ExpectedFirstName);
            }

            await AssertQueuedProjectionWorkRemainsStableAsync(
                studentIds,
                queuedStates,
                TimeSpan.FromSeconds(2)
            );

            JsonObject cliBacklogTarget = await RunDocumentCacheStatusCliForTargetAsync(dataStoreId);
            ReadString(cliBacklogTarget, "lifecycle", "state").Should().Be("tracking");
            ReadString(cliBacklogTarget, "queueSummary", "presence").Should().Be("notEmpty");
            ReadString(cliBacklogTarget, "operationalHealth", "reason").Should().Be("runtimeNotObserved");
            ReadString(cliBacklogTarget, "caughtUp", "status").Should().Be("unknown");
            ReadString(cliBacklogTarget, "caughtUp", "reason").Should().Be("runtimeNotObserved");

            await RestartDmsWithDocumentCacheTargetDataStoreIdAsync(dataStoreId);
            restoredConfiguredTarget = true;
            SetDmsBearerToken(dmsToken);

            JsonObject caughtUpTarget = await WaitForDocumentCacheCaughtUpAsync(dataStoreId);
            AssertTargetIsHostedReadAccelerationTarget(caughtUpTarget, dataStoreId);
            ReadDouble(caughtUpTarget, "effectiveSettings", "projector", "pollIntervalSeconds")
                .Should()
                .Be(1);

            foreach (HostedOutageStudent student in students)
            {
                DocumentCacheProjection projection = await ReadDocumentCacheProjectionAsync(student.Id);
                projection.CacheContentVersion.Should().Be(projection.DocumentContentVersion);
                projection.WorkRows.Should().Be(0);
                projection.DocumentJson.Should().Contain(student.UniqueId);
                projection.DocumentJson.Should().Contain(student.ExpectedFirstName);

                await AssertGetByIdAsync(student.Id, student.UniqueId, student.ExpectedFirstName);
                await AssertGetManyAsync(student.UniqueId, student.ExpectedFirstName);
            }
        }
        finally
        {
            if (!restoredConfiguredTarget)
            {
                await RestartDmsWithDocumentCacheTargetDataStoreIdAsync(dataStoreId);
                SetDmsBearerToken(dmsToken);
            }
        }
    }

    private async Task<JsonObject> WaitForDocumentCacheTargetAbsentAsync(long dataStoreId)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        string lastStatus = string.Empty;

        while (DateTimeOffset.UtcNow < deadline)
        {
            JsonObject status = await GetDocumentCacheStatusAsync();
            lastStatus = status.ToJsonString();

            if (FindTargetByDataStoreId(status, dataStoreId) is null)
            {
                return status;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500));
        }

        throw new AssertionException(
            $"DocumentCache hosted status continued to include removed dataStoreId {dataStoreId}. Last status: {lastStatus}"
        );
    }

    private static async Task AssertQueuedProjectionWorkRemainsStableAsync(
        IReadOnlyList<Guid> documentUuids,
        IReadOnlyList<DocumentCacheQueuedState> expectedStates,
        TimeSpan stablePeriod
    )
    {
        await Task.Delay(stablePeriod);

        IReadOnlyList<DocumentCacheQueuedState> actualStates = await ReadDocumentCacheQueuedStatesAsync(
            documentUuids
        );
        actualStates.Should().HaveCount(expectedStates.Count);
        for (int index = 0; index < expectedStates.Count; index++)
        {
            actualStates[index]
                .Should()
                .Be(
                    expectedStates[index],
                    "removing the hosted target must pause projector processing without direct-fill cache mutation"
                );
        }
    }

    private static async Task<DocumentCacheDurableState> ReadDocumentCacheDurableStateAsync()
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
            throw new AssertionException("DocumentCache durable lifecycle state row was not found.");
        }

        return new DocumentCacheDurableState(
            ReadRequiredString(reader, "ProjectionLifecycleState"),
            Convert.ToBoolean(reader["CacheAheadRecoveryRequired"], CultureInfo.InvariantCulture)
        );
    }

    private sealed record DocumentCacheDurableState(
        string ProjectionLifecycleState,
        bool CacheAheadRecoveryRequired
    );
}
