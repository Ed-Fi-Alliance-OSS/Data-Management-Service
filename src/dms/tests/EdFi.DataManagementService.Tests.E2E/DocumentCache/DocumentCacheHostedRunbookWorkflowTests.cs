// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Tests.E2E.Authorization;
using FluentAssertions;

namespace EdFi.DataManagementService.Tests.E2E.DocumentCache;

public sealed partial class DocumentCacheHostedHappyPathTests
{
    private const string RunbookStatusTranscriptLabel = "dms-document-cache status";
    private const string RunbookScrubTranscriptLabel = "dms-document-cache scrub";
    private const string RunbookRebuildOnlineTranscriptLabel = "dms-document-cache rebuild-online";

    [Test]
    [Category("DocumentCacheRunbookWorkflow")]
    public async Task It_runs_operator_scrub_and_rebuild_online_workflows_against_the_hosted_stack()
    {
        await RegisterSystemAdministratorAsync();
        int dataStoreId = await GetConfiguredDataStoreIdAsync();
        dataStoreId
            .Should()
            .Be(
                TargetDataStoreId,
                "the focused DocumentCache E2E overlay target must match the provisioned CMS data store"
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

        string studentUniqueId = $"doc-cache-runbook-{Guid.NewGuid():N}"[..32];
        Guid studentId = await PostStudentAsync(studentUniqueId, "Runbook Scrub Source");
        await WaitForDocumentCacheCaughtUpAsync(dataStoreId);

        DocumentCacheProjection originalProjection = await ReadDocumentCacheProjectionAsync(studentId);
        originalProjection.CacheContentVersion.Should().Be(originalProjection.DocumentContentVersion);
        originalProjection.WorkRows.Should().Be(0);

        bool restoredDefaultPollInterval = false;
        try
        {
            await RestartDmsWithProjectorPollIntervalAsync(HostedProjectorOutagePollInterval);
            SetDmsBearerToken(dmsToken);

            JsonObject longPollTarget = await WaitForDocumentCacheCaughtUpAsync(dataStoreId);
            AssertTargetIsHostedReadAccelerationTarget(longPollTarget, dataStoreId);
            ReadDouble(longPollTarget, "effectiveSettings", "projector", "pollIntervalSeconds")
                .Should()
                .Be(600);

            await DeleteProjectionStateForDocumentAsync(studentId);
            DocumentCacheQueuedState missingWorkState = await ReadDocumentCacheQueuedStateAsync(studentId);
            missingWorkState.CacheContentVersion.Should().BeNull();
            missingWorkState.WorkRequiredContentVersion.Should().BeNull();

            JsonObject preScrubStatus = await RunDocumentCacheStatusCliForTargetAsync(dataStoreId);
            ReadString(preScrubStatus, "lifecycle", "state").Should().Be("tracking");
            ReadString(preScrubStatus, "queueSummary", "presence").Should().Be("empty");
            ReadString(preScrubStatus, "caughtUp", "status").Should().Be("unknown");

            JsonObject scrub = await RunDocumentCacheScrubCliAsync(
                dataStoreId,
                preScrubStatus["physicalSourceFingerprint"]!.GetValue<string>()
            );
            scrub["command"]!.GetValue<string>().Should().Be("explicitIntegrityScrub");
            scrub["status"]!.GetValue<string>().Should().Be("completed");
            scrub["classification"]!.GetValue<string>().Should().Be("succeeded");
            scrub["mutated"]!.GetValue<bool>().Should().BeTrue();
            scrub["lifecycle"]!.GetValue<string>().Should().Be("tracking");
            scrub["cacheAheadRecoveryRequired"]!.GetValue<bool>().Should().BeFalse();

            IReadOnlyList<DocumentCacheQueuedState> queuedStates = await WaitForQueuedProjectionWorkAsync([
                studentId,
            ]);
            queuedStates.Should().ContainSingle();
            queuedStates[0].DocumentId.Should().Be(originalProjection.DocumentId);
            queuedStates[0].WorkRequiredContentVersion.Should().Be(originalProjection.DocumentContentVersion);

            JsonObject backlogStatus = await GetDocumentCacheStatusAsync();
            JsonObject backlogTarget = TargetByDataStoreId(backlogStatus, dataStoreId);
            ReadString(backlogTarget, "lifecycle", "state").Should().Be("tracking");
            ReadString(backlogTarget, "queueSummary", "presence").Should().Be("notEmpty");
            ReadString(backlogTarget, "caughtUp", "status").Should().Be("notCaughtUp");

            await RestartDmsWithProjectorPollIntervalAsync(HostedProjectorDefaultPollInterval);
            restoredDefaultPollInterval = true;
            SetDmsBearerToken(dmsToken);
            await WaitForDocumentCacheCaughtUpAsync(dataStoreId);

            DocumentCacheProjection repairedProjection = await ReadDocumentCacheProjectionAsync(studentId);
            repairedProjection.CacheContentVersion.Should().Be(repairedProjection.DocumentContentVersion);
            repairedProjection.WorkRows.Should().Be(0);
            repairedProjection.DocumentJson.Should().Contain(studentUniqueId);
            repairedProjection.DocumentJson.Should().Contain("Runbook Scrub Source");
            await AssertGetByIdAsync(studentId, studentUniqueId, "Runbook Scrub Source");
            await AssertGetManyAsync(studentUniqueId, "Runbook Scrub Source");

            HostedRunbookRebuildResult rebuildResult =
                await RunRebuildOnlineWhileCanonicalWritesContinueAsync(dataStoreId);
            JsonObject rebuild = rebuildResult.CommandResult;
            rebuild["command"]!.GetValue<string>().Should().Be("onlineCacheRebuild");
            rebuild["status"]!.GetValue<string>().Should().Be("completed");
            rebuild["classification"]!.GetValue<string>().Should().Be("succeeded");
            rebuild["mutated"]!.GetValue<bool>().Should().BeTrue();
            rebuild["lifecycle"]!.GetValue<string>().Should().Be("tracking");
            rebuild["cacheAheadRecoveryRequired"]!.GetValue<bool>().Should().BeFalse();

            await WaitForDocumentCacheCaughtUpAsync(dataStoreId);
            await AssertGetByIdAsync(
                rebuildResult.StudentId,
                rebuildResult.StudentUniqueId,
                rebuildResult.ExpectedFirstName
            );
            await AssertGetManyAsync(rebuildResult.StudentUniqueId, rebuildResult.ExpectedFirstName);

            DocumentCacheProjection concurrentWriteProjection = await ReadDocumentCacheProjectionAsync(
                rebuildResult.StudentId
            );
            concurrentWriteProjection
                .CacheContentVersion.Should()
                .Be(concurrentWriteProjection.DocumentContentVersion);
            concurrentWriteProjection.WorkRows.Should().Be(0);
            concurrentWriteProjection.DocumentJson.Should().Contain(rebuildResult.StudentUniqueId);
        }
        finally
        {
            if (!restoredDefaultPollInterval)
            {
                await RestartDmsWithProjectorPollIntervalAsync(HostedProjectorDefaultPollInterval);
                SetDmsBearerToken(dmsToken);
            }
        }
    }

    private async Task<JsonObject> RunDocumentCacheStatusCliForTargetAsync(int dataStoreId)
    {
        WriteRunbookCommandTranscript(RunbookStatusTranscriptLabel, dataStoreId);
        JsonObject status = await RunDocumentCacheAdminWithHostReachableDataStoreAsync(
            dataStoreId,
            [
                "status",
                "--data-store-id",
                dataStoreId.ToString(CultureInfo.InvariantCulture),
                "--json",
                "--datastore",
                CliDatastoreOptionValue(),
                "--status-observation-timeout-seconds",
                "1",
                "--status-timeout-seconds",
                "5",
            ]
        );

        return TargetByDataStoreId(status, dataStoreId);
    }

    private async Task<JsonObject> RunDocumentCacheScrubCliAsync(
        int dataStoreId,
        string expectedPhysicalSourceFingerprint
    )
    {
        WriteRunbookCommandTranscript(RunbookScrubTranscriptLabel, dataStoreId);
        return await RunDocumentCacheAdminWithHostReachableDataStoreAsync(
            dataStoreId,
            [
                "scrub",
                "--data-store-id",
                dataStoreId.ToString(CultureInfo.InvariantCulture),
                "--confirm",
                "integrityScrub",
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

    private async Task<HostedRunbookRebuildResult> RunRebuildOnlineWhileCanonicalWritesContinueAsync(
        int dataStoreId
    )
    {
        CmsDataStore dataStore = await GetCmsDataStoreAsync(dataStoreId);
        RunningDocumentCacheAdminCommand? command = null;

        await UpdateCmsDataStoreConnectionStringAsync(dataStore, AppSettings.DataStoreAdminConnectionString);
        try
        {
            WriteRunbookCommandTranscript(RunbookRebuildOnlineTranscriptLabel, dataStoreId);
            command = StartDocumentCacheAdminCommand(
                RunbookRebuildOnlineTranscriptLabel,
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
                    "90",
                ]
            );

            await AssertDocumentCacheAdminCommandStillRunningAsync(
                command,
                "before the concurrent canonical API write"
            );

            string studentUniqueId = $"doc-cache-rebuild-{Guid.NewGuid():N}"[..32];
            const string firstName = "Runbook Rebuild Write";
            Guid studentId = await PostStudentAsync(studentUniqueId, firstName);

            DocumentCacheQueuedState queuedState = await ReadDocumentCacheQueuedStateAsync(studentId);
            queuedState.WorkRequiredContentVersion.Should().Be(queuedState.DocumentContentVersion);

            JsonObject commandResult = await AwaitDocumentCacheAdminCommandResultAsync(
                command,
                TimeSpan.FromSeconds(150)
            );
            return new HostedRunbookRebuildResult(studentId, studentUniqueId, firstName, commandResult);
        }
        finally
        {
            if (command is not null)
            {
                TryKill(command.Process);
                command.Process.Dispose();
            }

            await UpdateCmsDataStoreConnectionStringAsync(dataStore, AppSettings.DataStoreConnectionString);
        }
    }

    private RunningDocumentCacheAdminCommand StartDocumentCacheAdminCommand(
        string description,
        string[] arguments
    )
    {
        string settingsPath = CreateDocumentCacheAdminSettingsFile();
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                WorkingDirectory = _repositoryRoot,
            },
        };
        process.StartInfo.ArgumentList.Add("run");
        process.StartInfo.ArgumentList.Add("--project");
        process.StartInfo.ArgumentList.Add(DocumentCacheAdminProjectPath());
        process.StartInfo.ArgumentList.Add("--configuration");
        process.StartInfo.ArgumentList.Add(CurrentBuildConfiguration());
        process.StartInfo.ArgumentList.Add("--no-restore");
        process.StartInfo.ArgumentList.Add("--");

        foreach (string argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        process.StartInfo.ArgumentList.Add("--settings");
        process.StartInfo.ArgumentList.Add(settingsPath);
        process.StartInfo.Environment["DOTNET_ENVIRONMENT"] = string.Empty;
        process.StartInfo.Environment["ASPNETCORE_ENVIRONMENT"] = string.Empty;
        process.StartInfo.Environment["ConfigurationServiceSettings__ClientSecret"] = ConfigurationSecret();

        process.Start().Should().BeTrue($"{description} must start");
        return new RunningDocumentCacheAdminCommand(
            process,
            process.StandardOutput.ReadToEndAsync(),
            process.StandardError.ReadToEndAsync(),
            description
        );
    }

    private static async Task AssertDocumentCacheAdminCommandStillRunningAsync(
        RunningDocumentCacheAdminCommand command,
        string context
    )
    {
        if (!command.Process.HasExited)
        {
            return;
        }

        string output = await command.StandardOutput;
        string error = await command.StandardError;
        Assert.Fail(
            $"{command.Description} exited {context}.\nExit code: {command.Process.ExitCode.ToString(CultureInfo.InvariantCulture)}\nstdout:\n{output}\nstderr:\n{error}"
        );
    }

    private static async Task<JsonObject> AwaitDocumentCacheAdminCommandResultAsync(
        RunningDocumentCacheAdminCommand command,
        TimeSpan timeout
    )
    {
        using var cancellationSource = new CancellationTokenSource(timeout);
        try
        {
            await command.Process.WaitForExitAsync(cancellationSource.Token);
        }
        catch (OperationCanceledException)
        {
            TryKill(command.Process);
            Assert.Fail($"{command.Description} timed out.");
        }

        string output = await command.StandardOutput;
        string error = await command.StandardError;
        command
            .Process.ExitCode.Should()
            .Be(0, "{0} stderr:\n{1}\nstdout:\n{2}", command.Description, error, output);
        error.Should().NotContain(AppSettings.DataStoreConnectionString);
        error.Should().NotContain(AppSettings.DataStoreAdminConnectionString);

        JsonNode? parsed = JsonNode.Parse(output);
        return parsed as JsonObject
            ?? throw new InvalidOperationException("DocumentCache admin CLI stdout was not a JSON object.");
    }

    private static async Task DeleteProjectionStateForDocumentAsync(Guid documentUuid)
    {
        await using DbConnection connection = CreateDataStoreConnection();
        await connection.OpenAsync();
        await using DbCommand command = connection.CreateCommand();
        command.CommandText = IsMssql()
            ? """
                DECLARE @documentId bigint =
                    (SELECT [DocumentId] FROM [dms].[Document] WHERE [DocumentUuid] = @documentUuid);

                DELETE FROM [dms].[DocumentProjectionWork]
                WHERE [DocumentId] = @documentId;

                DELETE FROM [dms].[DocumentCache]
                WHERE [DocumentId] = @documentId;
                """
            : """
                DELETE FROM "dms"."DocumentProjectionWork"
                WHERE "DocumentId" = (
                    SELECT "DocumentId" FROM "dms"."Document" WHERE "DocumentUuid" = @documentUuid
                );

                DELETE FROM "dms"."DocumentCache"
                WHERE "DocumentId" = (
                    SELECT "DocumentId" FROM "dms"."Document" WHERE "DocumentUuid" = @documentUuid
                );
                """;
        AddParameter(command, "@documentUuid", documentUuid);
        await command.ExecuteNonQueryAsync();
    }

    private static void WriteRunbookCommandTranscript(string command, int dataStoreId)
    {
        TestContext.Out.WriteLine(
            $"{command} --data-store-id {dataStoreId.ToString(CultureInfo.InvariantCulture)} targets {AppSettings.DataStoreDatabaseName}"
        );
    }

    private sealed record HostedRunbookRebuildResult(
        Guid StudentId,
        string StudentUniqueId,
        string ExpectedFirstName,
        JsonObject CommandResult
    );

    private sealed record RunningDocumentCacheAdminCommand(
        Process Process,
        Task<string> StandardOutput,
        Task<string> StandardError,
        string Description
    );
}
