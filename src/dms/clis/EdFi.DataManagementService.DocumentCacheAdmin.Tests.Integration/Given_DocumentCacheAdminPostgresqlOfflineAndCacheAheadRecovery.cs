// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.DocumentCacheAdmin;
using FluentAssertions;

namespace EdFi.DataManagementService.DocumentCacheAdmin.Tests.Integration;

[TestFixture]
[NonParallelizable]
[Category("PostgresqlIntegration")]
[Category("Offline")]
public sealed class Given_DocumentCacheAdminPostgresqlOfflineCommands
{
    [TestCase(
        DocumentCacheAdminCommandSurface.ActivateOfflineCommandName,
        "offlineActivation",
        "Disabled",
        false,
        null,
        TestName = "activate-offline missing admission"
    )]
    [TestCase(
        DocumentCacheAdminCommandSurface.ActivateOfflineCommandName,
        "offlineActivation",
        "Disabled",
        false,
        "ClosedAndDrained",
        TestName = "activate-offline wrong admission"
    )]
    [TestCase(
        DocumentCacheAdminCommandSurface.DeactivateOfflineCommandName,
        "offlineDeactivation",
        "Tracking",
        false,
        null,
        TestName = "deactivate-offline missing admission"
    )]
    [TestCase(
        DocumentCacheAdminCommandSurface.DeactivateOfflineCommandName,
        "offlineDeactivation",
        "Tracking",
        false,
        "ClosedAndDrained",
        TestName = "deactivate-offline wrong admission"
    )]
    public async Task It_rejects_missing_or_wrong_offline_writer_admission_as_argument_errors(
        string commandName,
        string confirmation,
        string lifecycleState,
        bool cacheAheadRecoveryRequired,
        string? offlineWriterAdmission
    )
    {
        await using DocumentCacheAdminCliTarget target =
            await DocumentCacheAdminCliTarget.CreatePostgresqlAsync();
        await target.State.SetLifecycleAsync(lifecycleState, cacheAheadRecoveryRequired);
        DocumentCacheAdminCliLifecycleState originalLifecycle = await target.State.ReadLifecycleAsync();

        await using DocumentCacheAdminCliProcessHarness harness =
            await DocumentCacheAdminCliProcessHarness.CreateAsync(target);

        DocumentCacheAdminCliProcessResult result = await RunWriterFencedCommandAsync(
            harness,
            target,
            commandName,
            confirmation,
            offlineWriterAdmission
        );

        result.ExitCode.Should().Be(DocumentCacheAdminExitCodes.ArgumentError);
        result
            .StandardOutput.Should()
            .Contain(DocumentCacheAdminCommandSurface.OfflineWriterAdmissionOptionName);
        result.StandardError.Should().NotContain(target.ConnectionString);
        harness.ConfigurationService.TokenRequestCount.Should().Be(0);
        harness.ConfigurationService.DataStoresRequestCount.Should().Be(0);
        (await target.State.ReadLifecycleAsync()).Should().Be(originalLifecycle);
    }

    [TestCase(
        DocumentCacheAdminCommandSurface.ActivateOfflineCommandName,
        "offlineActivation",
        "offlineActivation",
        "Disabled",
        false,
        TestName = "activate-offline"
    )]
    [TestCase(
        DocumentCacheAdminCommandSurface.DeactivateOfflineCommandName,
        "offlineDeactivation",
        "offlineDeactivation",
        "Tracking",
        false,
        TestName = "deactivate-offline"
    )]
    public async Task It_rejects_production_default_unknown_downstream_history_without_mutation(
        string commandName,
        string confirmation,
        string expectedJsonCommand,
        string lifecycleState,
        bool cacheAheadRecoveryRequired
    )
    {
        await using DocumentCacheAdminCliTarget target =
            await DocumentCacheAdminCliTarget.CreatePostgresqlAsync();
        IReadOnlyList<DocumentCacheAdminCliSeededDocument> documents =
            await PostgresqlOfflineWorkflowTestHelpers.InsertProjectedDescriptorRowsAsync(
                target,
                commandName,
                documentCount: 2
            );
        await target.State.SetLifecycleAsync(lifecycleState, cacheAheadRecoveryRequired);
        DocumentCacheAdminCliLifecycleState originalLifecycle = await target.State.ReadLifecycleAsync();
        DocumentCacheAdminCliMutableCounts originalCounts = await target.State.ReadMutableCountsAsync();
        IReadOnlyDictionary<long, string> originalCacheRows =
            await target.State.ReadPostgresqlCachedJsonByDocumentIdAsync();
        IReadOnlyDictionary<long, long> originalWorkRows =
            await target.State.ReadPostgresqlWorkVersionsByDocumentIdAsync();

        await using DocumentCacheAdminCliProcessHarness harness =
            await DocumentCacheAdminCliProcessHarness.CreateAsync(target);

        DocumentCacheAdminCliProcessResult result = await RunWriterFencedCommandAsync(
            harness,
            target,
            commandName,
            confirmation,
            DocumentCacheAdminCommandSurface.OfflineWriterAdmissionClosedAndDrainedOptionValue,
            expectedPhysicalSourceFingerprint: await target.State.ReadPhysicalSourceFingerprintAsync()
        );

        JsonObject commandResult = AssertDownstreamHistoryRejected(result, target, expectedJsonCommand);
        commandResult["lifecycle"]!.GetValue<string>().Should().Be(ToLowerCamelLifecycle(lifecycleState));
        commandResult["cacheAheadRecoveryRequired"]!.GetValue<bool>().Should().Be(cacheAheadRecoveryRequired);
        (await target.State.ReadLifecycleAsync()).Should().Be(originalLifecycle);
        (await target.State.ReadMutableCountsAsync()).Should().Be(originalCounts);
        (await target.State.ReadPostgresqlCachedJsonByDocumentIdAsync())
            .Should()
            .BeEquivalentTo(originalCacheRows);
        (await target.State.ReadPostgresqlWorkVersionsByDocumentIdAsync())
            .Should()
            .BeEquivalentTo(originalWorkRows);
        harness.ConfigurationService.TokenRequestCount.Should().BeGreaterThan(0);
        harness.ConfigurationService.DataStoresRequestCount.Should().BeGreaterThan(0);
        documents.Count.Should().Be((int)originalCounts.DocumentCacheRows);
    }

    private static Task<DocumentCacheAdminCliProcessResult> RunWriterFencedCommandAsync(
        DocumentCacheAdminCliProcessHarness harness,
        DocumentCacheAdminCliTarget target,
        string commandName,
        string confirmation,
        string? offlineWriterAdmission,
        string? expectedPhysicalSourceFingerprint = null
    ) =>
        harness.RunAsync(
            CreateWriterFencedArguments(
                target,
                commandName,
                confirmation,
                offlineWriterAdmission,
                expectedPhysicalSourceFingerprint
            )
        );

    private static string[] CreateWriterFencedArguments(
        DocumentCacheAdminCliTarget target,
        string commandName,
        string confirmation,
        string? offlineWriterAdmission,
        string? expectedPhysicalSourceFingerprint
    )
    {
        List<string> arguments =
        [
            commandName,
            DocumentCacheAdminCommandSurface.DataStoreIdOptionName,
            target.DataStoreId.ToString(),
            DocumentCacheAdminCommandSurface.ConfirmOptionName,
            confirmation,
            DocumentCacheAdminCommandSurface.JsonOptionName,
            DocumentCacheAdminCommandSurface.CommandTimeoutSecondsOptionName,
            "30",
        ];

        if (offlineWriterAdmission is not null)
        {
            arguments.Add(DocumentCacheAdminCommandSurface.OfflineWriterAdmissionOptionName);
            arguments.Add(offlineWriterAdmission);
        }

        if (expectedPhysicalSourceFingerprint is not null)
        {
            arguments.Add(DocumentCacheAdminCommandSurface.ExpectedPhysicalSourceFingerprintOptionName);
            arguments.Add(expectedPhysicalSourceFingerprint);
        }

        return [.. arguments];
    }

    private static JsonObject AssertDownstreamHistoryRejected(
        DocumentCacheAdminCliProcessResult result,
        DocumentCacheAdminCliTarget target,
        string expectedJsonCommand
    )
    {
        JsonObject commandResult = PostgresqlCommandResultAssertions.AssertCommandResult(
            result,
            target,
            DocumentCacheAdminExitCodes.RejectedNoMutation
        );
        commandResult["command"]!.GetValue<string>().Should().Be(expectedJsonCommand);
        commandResult["status"]!.GetValue<string>().Should().Be("rejectedNoMutation");
        commandResult["classification"]!.GetValue<string>().Should().Be("downstreamHistoryPresentOrUnknown");
        commandResult["mutated"]!.GetValue<bool>().Should().BeFalse();
        return commandResult;
    }

    private static string ToLowerCamelLifecycle(string lifecycleState) =>
        char.ToLowerInvariant(lifecycleState[0]) + lifecycleState[1..];
}

[TestFixture]
[NonParallelizable]
[Category("PostgresqlIntegration")]
[Category("CacheAheadRecovery")]
public sealed class Given_DocumentCacheAdminPostgresqlCacheAheadRecovery
{
    [TestCase(null, TestName = "recover-cache-ahead missing admission")]
    [TestCase("ClosedAndDrained", TestName = "recover-cache-ahead wrong admission")]
    public async Task It_rejects_missing_or_wrong_offline_writer_admission_as_argument_errors(
        string? offlineWriterAdmission
    )
    {
        await using DocumentCacheAdminCliTarget target =
            await DocumentCacheAdminCliTarget.CreatePostgresqlAsync();
        await target.State.SetLifecycleAsync("Tracking", cacheAheadRecoveryRequired: true);
        DocumentCacheAdminCliLifecycleState originalLifecycle = await target.State.ReadLifecycleAsync();

        await using DocumentCacheAdminCliProcessHarness harness =
            await DocumentCacheAdminCliProcessHarness.CreateAsync(target);

        DocumentCacheAdminCliProcessResult result = await harness.RunAsync(
            CreateRecoveryArguments(target, offlineWriterAdmission)
        );

        result.ExitCode.Should().Be(DocumentCacheAdminExitCodes.ArgumentError);
        result
            .StandardOutput.Should()
            .Contain(DocumentCacheAdminCommandSurface.OfflineWriterAdmissionOptionName);
        result.StandardError.Should().NotContain(target.ConnectionString);
        harness.ConfigurationService.TokenRequestCount.Should().Be(0);
        harness.ConfigurationService.DataStoresRequestCount.Should().Be(0);
        (await target.State.ReadLifecycleAsync()).Should().Be(originalLifecycle);
    }

    [Test]
    public async Task It_rejects_production_default_unknown_downstream_history_without_mutation()
    {
        await using DocumentCacheAdminCliTarget target =
            await DocumentCacheAdminCliTarget.CreatePostgresqlAsync();
        IReadOnlyList<DocumentCacheAdminCliSeededDocument> documents =
            await PostgresqlOfflineWorkflowTestHelpers.InsertProjectedDescriptorRowsAsync(
                target,
                "RecoverCacheAheadUnknown",
                documentCount: 2
            );
        await target.State.SetLifecycleAsync("Tracking", cacheAheadRecoveryRequired: true);
        DocumentCacheAdminCliLifecycleState originalLifecycle = await target.State.ReadLifecycleAsync();
        DocumentCacheAdminCliMutableCounts originalCounts = await target.State.ReadMutableCountsAsync();
        IReadOnlyDictionary<long, string> originalCacheRows =
            await target.State.ReadPostgresqlCachedJsonByDocumentIdAsync();
        IReadOnlyDictionary<long, long> originalWorkRows =
            await target.State.ReadPostgresqlWorkVersionsByDocumentIdAsync();

        await using DocumentCacheAdminCliProcessHarness harness =
            await DocumentCacheAdminCliProcessHarness.CreateAsync(target);

        DocumentCacheAdminCliProcessResult result = await harness.RunAsync(
            CreateRecoveryArguments(
                target,
                DocumentCacheAdminCommandSurface.OfflineWriterAdmissionClosedAndDrainedOptionValue,
                expectedPhysicalSourceFingerprint: await target.State.ReadPhysicalSourceFingerprintAsync()
            )
        );

        JsonObject commandResult = PostgresqlCommandResultAssertions.AssertCommandResult(
            result,
            target,
            DocumentCacheAdminExitCodes.RejectedNoMutation
        );
        commandResult["command"]!.GetValue<string>().Should().Be("internalOnlyCacheAheadRecovery");
        commandResult["status"]!.GetValue<string>().Should().Be("rejectedNoMutation");
        commandResult["classification"]!.GetValue<string>().Should().Be("downstreamHistoryPresentOrUnknown");
        commandResult["mutated"]!.GetValue<bool>().Should().BeFalse();
        commandResult["lifecycle"]!.GetValue<string>().Should().Be("tracking");
        commandResult["cacheAheadRecoveryRequired"]!.GetValue<bool>().Should().BeTrue();
        (await target.State.ReadLifecycleAsync()).Should().Be(originalLifecycle);
        (await target.State.ReadMutableCountsAsync()).Should().Be(originalCounts);
        (await target.State.ReadPostgresqlCachedJsonByDocumentIdAsync())
            .Should()
            .BeEquivalentTo(originalCacheRows);
        (await target.State.ReadPostgresqlWorkVersionsByDocumentIdAsync())
            .Should()
            .BeEquivalentTo(originalWorkRows);
        documents.Count.Should().Be((int)originalCounts.DocumentCacheRows);
    }

    private static string[] CreateRecoveryArguments(
        DocumentCacheAdminCliTarget target,
        string? offlineWriterAdmission,
        string? expectedPhysicalSourceFingerprint = null
    )
    {
        List<string> arguments =
        [
            DocumentCacheAdminCommandSurface.RecoverCacheAheadCommandName,
            DocumentCacheAdminCommandSurface.DataStoreIdOptionName,
            target.DataStoreId.ToString(),
            DocumentCacheAdminCommandSurface.ConfirmOptionName,
            "internalCacheAheadRecovery",
            DocumentCacheAdminCommandSurface.JsonOptionName,
            DocumentCacheAdminCommandSurface.CommandTimeoutSecondsOptionName,
            "30",
        ];

        if (offlineWriterAdmission is not null)
        {
            arguments.Add(DocumentCacheAdminCommandSurface.OfflineWriterAdmissionOptionName);
            arguments.Add(offlineWriterAdmission);
        }

        if (expectedPhysicalSourceFingerprint is not null)
        {
            arguments.Add(DocumentCacheAdminCommandSurface.ExpectedPhysicalSourceFingerprintOptionName);
            arguments.Add(expectedPhysicalSourceFingerprint);
        }

        return [.. arguments];
    }
}

internal static class PostgresqlOfflineWorkflowTestHelpers
{
    public static async Task<
        IReadOnlyList<DocumentCacheAdminCliSeededDocument>
    > InsertProjectedDescriptorRowsAsync(
        DocumentCacheAdminCliTarget target,
        string codeValuePrefix,
        int documentCount
    )
    {
        List<DocumentCacheAdminCliSeededDocument> documents = [];
        for (var index = 0; index < documentCount; index++)
        {
            DocumentCacheAdminCliSeededDocument document =
                await target.State.InsertPostgresqlDescriptorDocumentAsync(
                    $"{codeValuePrefix}-{index}",
                    contentVersion: 10 + index
                );
            await target.State.InsertPostgresqlDocumentCacheAsync(
                document,
                documentJson: $$"""{"value":"projected-{{index}}"}"""
            );
            await target.State.InsertPostgresqlProjectionWorkAsync(document);
            documents.Add(document);
        }

        return documents;
    }
}
