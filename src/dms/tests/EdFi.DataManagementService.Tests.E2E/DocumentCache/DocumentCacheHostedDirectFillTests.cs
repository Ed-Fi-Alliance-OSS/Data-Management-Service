// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Tests.E2E.Authorization;
using FluentAssertions;

namespace EdFi.DataManagementService.Tests.E2E.DocumentCache;

public sealed partial class DocumentCacheHostedHappyPathTests
{
    [Test]
    [Category("DocumentCacheHostedDirectFill")]
    [Category("DocumentCacheDirectFill")]
    public async Task It_direct_fills_missing_cache_for_hosted_reads_during_projector_outage()
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
        string dmsToken = string.Empty;
        bool restoredDefaultPollInterval = false;

        try
        {
            await RestartDmsWithProjectorPollIntervalAsync(HostedProjectorOutagePollInterval);
            dmsToken = await GetDmsTokenAsync(credentials);
            SetDmsBearerToken(dmsToken);
            await AssertStudentRouteRemainsAvailableAsync();

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

            JsonObject longPollTarget = await WaitForDocumentCacheCaughtUpAsync(dataStoreId);
            AssertTargetIsHostedReadAccelerationTarget(longPollTarget, dataStoreId);
            ReadDouble(longPollTarget, "effectiveSettings", "projector", "pollIntervalSeconds")
                .Should()
                .Be(600);
            ReadDouble(longPollTarget, "effectiveSettings", "readAcceleration", "directFillTimeoutSeconds")
                .Should()
                .Be(5);

            string studentUniqueId = $"doc-cache-direct-fill-{Guid.NewGuid():N}"[..32];
            const string firstName = "Direct Fill Create";
            Guid studentId = await PostStudentAsync(studentUniqueId, firstName);

            IReadOnlyList<DocumentCacheQueuedState> queuedStates = await WaitForQueuedProjectionWorkAsync([
                studentId,
            ]);
            DocumentCacheQueuedState queuedState = queuedStates.Should().ContainSingle().Subject;
            queuedState.WorkRequiredContentVersion.Should().Be(queuedState.DocumentContentVersion);
            queuedState.CacheContentVersion.Should().BeNull();

            JsonObject backlogStatus = await GetDocumentCacheStatusAsync();
            JsonObject backlogTarget = TargetByDataStoreId(backlogStatus, dataStoreId);
            ReadString(backlogTarget, "lifecycle", "state").Should().Be("tracking");
            ReadString(backlogTarget, "queueSummary", "presence").Should().Be("notEmpty");
            ReadString(backlogTarget, "caughtUp", "status").Should().Be("notCaughtUp");

            await AssertGetByIdAsync(studentId, studentUniqueId, firstName);
            DocumentCacheProjection directFillProjection = await WaitForDirectFillProjectionAsync(studentId);
            directFillProjection.CacheContentVersion.Should().Be(directFillProjection.DocumentContentVersion);
            directFillProjection.WorkRows.Should().Be(0);
            directFillProjection.ResourceName.Should().Be("Student");
            directFillProjection.DocumentJson.Should().Contain(studentUniqueId);
            directFillProjection.DocumentJson.Should().Contain(firstName);

            await AssertGetManyAsync(studentUniqueId, firstName);
            await AssertCanonicalStudentFirstNameAsync(studentId, firstName);

            await RestartDmsWithProjectorPollIntervalAsync(HostedProjectorDefaultPollInterval);
            restoredDefaultPollInterval = true;
            SetDmsBearerToken(dmsToken);

            JsonObject caughtUpTarget = await WaitForDocumentCacheCaughtUpAsync(dataStoreId);
            AssertTargetIsHostedReadAccelerationTarget(caughtUpTarget, dataStoreId);
            ReadDouble(caughtUpTarget, "effectiveSettings", "projector", "pollIntervalSeconds")
                .Should()
                .Be(1);

            DocumentCacheProjection finalProjection = await ReadDocumentCacheProjectionAsync(studentId);
            finalProjection.CacheContentVersion.Should().Be(finalProjection.DocumentContentVersion);
            finalProjection.WorkRows.Should().Be(0);
            finalProjection.DocumentJson.Should().Contain(studentUniqueId);
            finalProjection.DocumentJson.Should().Contain(firstName);
        }
        finally
        {
            if (!restoredDefaultPollInterval)
            {
                await RestartDmsWithProjectorPollIntervalAsync(HostedProjectorDefaultPollInterval);
                if (!string.IsNullOrWhiteSpace(dmsToken))
                {
                    SetDmsBearerToken(dmsToken);
                }
            }
        }
    }

    private static async Task<DocumentCacheProjection> WaitForDirectFillProjectionAsync(Guid documentUuid)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        DocumentCacheQueuedState? lastState = null;

        while (DateTimeOffset.UtcNow < deadline)
        {
            lastState = await ReadDocumentCacheQueuedStateAsync(documentUuid);
            if (
                lastState.CacheContentVersion == lastState.DocumentContentVersion
                && lastState.WorkRequiredContentVersion is null
            )
            {
                return await ReadDocumentCacheProjectionAsync(documentUuid);
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250));
        }

        throw new AssertionException(
            $"DocumentCache direct-fill did not create a fresh cache row and acknowledge work. Last state: {lastState}"
        );
    }
}
