// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.CommandLine;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.DocumentCache;
using EdFi.DataManagementService.DocumentCacheAdmin;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace EdFi.DataManagementService.DocumentCacheAdmin.Tests.Unit;

[TestFixture]
[Parallelizable]
[Category("JsonRequest")]
[Category("Serialization")]
public sealed class Given_DocumentCacheAdminJsonRequestAndSerializationContracts
{
    private const string Fingerprint =
        "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Test]
    public void It_accepts_the_status_target_only_request_shape()
    {
        var parseResult = ParseCommand(
            DocumentCacheAdminCommandSurface.StatusCommandName,
            DocumentCacheAdminCommandSurface.RequestJsonOptionName,
            "-"
        );

        bool parsed = DocumentCacheAdminInvocationTargetParser.TryParse(
            parseResult,
            _ => """{"targetKey":{"tenantKey":"","dataStoreId":1}}""",
            out DocumentCacheAdminInvocationTarget? invocationTarget,
            out string? failure
        );

        parsed.Should().BeTrue(failure);
        invocationTarget!.TargetKey.Should().Be(DocumentCacheTargetKey.Create("", 1));
        invocationTarget.JsonRequest.Should().NotBeNull();
        invocationTarget.JsonRequest!.Request.Should().BeOfType<DocumentCacheAdminStatusRequest>();
    }

    [Test]
    public void It_deserializes_mutating_request_json_into_the_selected_shared_request_dto()
    {
        var parseResult = ParseCommand(
            DocumentCacheAdminCommandSurface.RebuildOnlineCommandName,
            DocumentCacheAdminCommandSurface.RequestJsonOptionName,
            "-"
        );

        bool parsed = DocumentCacheAdminInvocationTargetParser.TryParse(
            parseResult,
            _ =>
                $$"""
                {
                  "targetKey": { "tenantKey": "TenantA", "dataStoreId": 7 },
                  "confirmation": "onlineCacheRebuild",
                  "expectedPhysicalSourceFingerprint": "{{Fingerprint}}"
                }
                """,
            out DocumentCacheAdminInvocationTarget? invocationTarget,
            out string? failure
        );

        parsed.Should().BeTrue(failure);
        DocumentCacheOnlineCacheRebuildRequest request = invocationTarget!
            .JsonRequest!.Request.Should()
            .BeOfType<DocumentCacheOnlineCacheRebuildRequest>()
            .Subject;
        request.TargetKey.TargetKey.Should().Be(DocumentCacheTargetKey.Create("TenantA", 7));
        request.Confirmation.Should().Be(DocumentCacheAdministrativeCommandConfirmation.OnlineCacheRebuild);
        request.ExpectedPhysicalSourceFingerprint!.Value.Should().Be(Fingerprint);
    }

    [Test]
    public void It_accepts_writer_fenced_json_request_fields_for_writer_fenced_commands()
    {
        var parseResult = ParseCommand(
            DocumentCacheAdminCommandSurface.ActivateOfflineCommandName,
            DocumentCacheAdminCommandSurface.RequestJsonOptionName,
            "-"
        );

        bool parsed = DocumentCacheAdminInvocationTargetParser.TryParse(
            parseResult,
            _ =>
                $$"""
                {
                  "targetKey": { "tenantKey": "", "dataStoreId": 1 },
                  "confirmation": "offlineActivation",
                  "expectedPhysicalSourceFingerprint": "{{Fingerprint}}",
                  "offlineWriterAdmission": {
                    "confirmed": true,
                    "confirmation": "offlineActivationWritersClosedAndDrained"
                  }
                }
                """,
            out DocumentCacheAdminInvocationTarget? invocationTarget,
            out string? failure
        );

        parsed.Should().BeTrue(failure);
        DocumentCacheOfflineActivationRequest request = invocationTarget!
            .JsonRequest!.Request.Should()
            .BeOfType<DocumentCacheOfflineActivationRequest>()
            .Subject;
        request.Confirmation.Should().Be(DocumentCacheAdministrativeCommandConfirmation.OfflineActivation);
        request
            .OfflineWriterAdmission!.Confirmation.Should()
            .Be(DocumentCacheOfflineWriterAdmissionConfirmation.OfflineActivationWritersClosedAndDrained);
    }

    [TestCase(DocumentCacheAdminCommandSurface.ConfirmOptionName, "onlineCacheRebuild")]
    [TestCase(DocumentCacheAdminCommandSurface.ExpectedPhysicalSourceFingerprintOptionName, Fingerprint)]
    public void It_rejects_command_specific_duplicate_options_when_request_json_is_present(
        string optionName,
        string optionValue
    )
    {
        var parseResult = ParseCommand(
            DocumentCacheAdminCommandSurface.RebuildOnlineCommandName,
            DocumentCacheAdminCommandSurface.RequestJsonOptionName,
            "-",
            optionName,
            optionValue
        );

        bool parsed = DocumentCacheAdminInvocationTargetParser.TryParse(
            parseResult,
            _ =>
                $$"""
                {
                  "targetKey": { "tenantKey": "", "dataStoreId": 1 },
                  "confirmation": "onlineCacheRebuild",
                  "expectedPhysicalSourceFingerprint": "{{Fingerprint}}"
                }
                """,
            out DocumentCacheAdminInvocationTarget? invocationTarget,
            out string? failure
        );

        parsed.Should().BeFalse();
        invocationTarget.Should().BeNull();
        failure.Should().Contain("cannot be supplied");
    }

    [Test]
    public void It_rejects_offline_writer_admission_options_when_request_json_is_present()
    {
        var parseResult = ParseCommand(
            DocumentCacheAdminCommandSurface.ActivateOfflineCommandName,
            DocumentCacheAdminCommandSurface.RequestJsonOptionName,
            "-",
            DocumentCacheAdminCommandSurface.OfflineWriterAdmissionOptionName,
            "closedAndDrained"
        );

        bool parsed = DocumentCacheAdminInvocationTargetParser.TryParse(
            parseResult,
            _ =>
                """
                {
                  "targetKey": { "tenantKey": "", "dataStoreId": 1 },
                  "confirmation": "offlineActivation",
                  "offlineWriterAdmission": {
                    "confirmed": true,
                    "confirmation": "offlineActivationWritersClosedAndDrained"
                  }
                }
                """,
            out DocumentCacheAdminInvocationTarget? invocationTarget,
            out string? failure
        );

        parsed.Should().BeFalse();
        invocationTarget.Should().BeNull();
        failure.Should().Contain("cannot be supplied");
    }

    [TestCase(
        """{"targetKey":{"tenantKey":"","dataStoreId":1},"confirmation":"onlineCacheRebuild","extra":true}"""
    )]
    [TestCase("""{"targetKey":{"tenantKey":"","dataStoreId":1},"confirmation":"integrityScrub"}""")]
    [TestCase("""{"targetKey":{"tenantKey":"","dataStoreId":1},"confirmation":1}""")]
    [TestCase("""{"confirmation":"onlineCacheRebuild"}""")]
    public void It_rejects_invalid_mutating_request_json(string requestJson)
    {
        var parseResult = ParseCommand(
            DocumentCacheAdminCommandSurface.RebuildOnlineCommandName,
            DocumentCacheAdminCommandSurface.RequestJsonOptionName,
            "-"
        );

        bool parsed = DocumentCacheAdminInvocationTargetParser.TryParse(
            parseResult,
            _ => requestJson,
            out DocumentCacheAdminInvocationTarget? invocationTarget,
            out string? failure
        );

        parsed.Should().BeFalse();
        invocationTarget.Should().BeNull();
        failure.Should().NotBeNullOrWhiteSpace();
    }

    [Test]
    public void It_rejects_unknown_fields_inside_offline_writer_admission_json()
    {
        var parseResult = ParseCommand(
            DocumentCacheAdminCommandSurface.ActivateOfflineCommandName,
            DocumentCacheAdminCommandSurface.RequestJsonOptionName,
            "-"
        );

        bool parsed = DocumentCacheAdminInvocationTargetParser.TryParse(
            parseResult,
            _ =>
                """
                {
                  "targetKey": { "tenantKey": "", "dataStoreId": 1 },
                  "confirmation": "offlineActivation",
                  "offlineWriterAdmission": {
                    "confirmed": true,
                    "confirmation": "offlineActivationWritersClosedAndDrained",
                    "extra": true
                  }
                }
                """,
            out DocumentCacheAdminInvocationTarget? invocationTarget,
            out string? failure
        );

        parsed.Should().BeFalse();
        invocationTarget.Should().BeNull();
        failure.Should().Contain("not supported");
    }

    [Test]
    public void It_serializes_shared_administrative_results_without_a_cli_wrapper()
    {
        DocumentCacheAdministrativeCommandResult result = new(
            DocumentCacheAdministrativeCommand.OnlineCacheRebuild,
            new DocumentCacheAdministrativeTargetKey("", 1),
            DocumentCacheAdministrativeCommandStatus.Completed,
            DocumentCacheAdministrativeCommandClassification.Succeeded,
            mutated: true
        );

        string json = DocumentCacheAdminJsonSerializer.SerializeContract(
            result,
            typeof(DocumentCacheAdministrativeCommandResult)
        );
        JsonObject root = JsonNode.Parse(json)!.AsObject();

        root.Select(property => property.Key).Should().StartWith("command", "targetKey", "status");
        root.Should().NotContainKey("result");
        root["command"]!.GetValue<string>().Should().Be("onlineCacheRebuild");
        root["classification"]!.GetValue<string>().Should().Be("succeeded");
    }

    [Test]
    public void It_serializes_shared_status_results_with_utc_timestamps_and_no_administration_settings()
    {
        DocumentCacheStatusResponse response = new(
            new DateTimeOffset(2026, 8, 16, 7, 34, 56, 789, TimeSpan.FromHours(-5)),
            []
        );

        string json = DocumentCacheAdminJsonSerializer.SerializeContract(
            response,
            typeof(DocumentCacheStatusResponse)
        );
        JsonObject root = JsonNode.Parse(json)!.AsObject();

        root["observedAt"]!.GetValue<string>().Should().Be("2026-08-16T12:34:56.789Z");
        root.Should().ContainKey("targets");
        json.Should().NotContain("administration");
        json.Should().NotContain("workflowTimeout");
    }

    [Test]
    public async Task It_writes_exactly_one_shared_status_json_document_to_stdout()
    {
        var parseResult = ParseCommand(
            DocumentCacheAdminCommandSurface.StatusCommandName,
            DocumentCacheAdminCommandSurface.DataStoreIdOptionName,
            "1",
            DocumentCacheAdminCommandSurface.JsonOptionName
        );
        await using ServiceProvider serviceProvider = new ServiceCollection()
            .AddSingleton<IDocumentCacheStatusService>(new StaticStatusService(StatusResponse()))
            .BuildServiceProvider();
        using var output = new StringWriter();

        int exitCode = await DocumentCacheAdminCommandExecutor.ExecuteAsync(
            parseResult,
            new DocumentCacheAdminInvocationTarget(
                DocumentCacheTargetKey.Create("", 1),
                DocumentCacheAdminInvocationTargetSource.Options
            ),
            serviceProvider,
            output
        );

        exitCode.Should().Be(DocumentCacheAdminExitCodes.Success);
        string stdout = output.ToString();
        stdout.TrimEnd().Should().NotContain("\n");
        JsonObject root = JsonNode.Parse(stdout)!.AsObject();
        root.Should().ContainKey("contractVersion");
        root.Should().ContainKey("targets");
        root.Should().NotContainKey("statusResponse");
        root["targets"]!.AsArray().Should().ContainSingle();
        root["targets"]![0]!["targetKey"]!["dataStoreId"]!.GetValue<long>().Should().Be(1);
    }

    private static ParseResult ParseCommand(params string[] args) =>
        DocumentCacheAdminCommandSurface.CreateRootCommand().Parse(args);

    private static DocumentCacheStatusResponse StatusResponse() =>
        new(
            new DateTimeOffset(2026, 8, 20, 12, 30, 0, TimeSpan.Zero),
            [
                new DocumentCacheStatusTarget(
                    DocumentCacheStatusTargetKey.FromTargetKey(DocumentCacheTargetKey.Create("", 1)),
                    targetGeneration: 3,
                    processObservedAt: new DateTimeOffset(2026, 8, 20, 12, 30, 0, TimeSpan.Zero),
                    durableObservedAt: new DateTimeOffset(2026, 8, 20, 12, 30, 1, TimeSpan.Zero),
                    provider: "postgresql",
                    physicalSourceFingerprint: Fingerprint,
                    new DocumentCacheStatusResolutionComponent(
                        DocumentCacheStatusResolutionStatus.Resolved,
                        DocumentCacheStatusResolutionReason.None,
                        new DateTimeOffset(2026, 8, 20, 12, 30, 0, TimeSpan.Zero),
                        message: null
                    ),
                    new DocumentCacheStatusEligibilityComponent(
                        DocumentCacheStatusEligibilityStatus.Unknown,
                        DocumentCacheStatusReason.RuntimeNotObserved,
                        "Current-generation DocumentCache projection runtime has not been observed."
                    ),
                    new DocumentCacheStatusInventoryComponentGroup(
                        new DateTimeOffset(2026, 8, 20, 12, 30, 0, TimeSpan.Zero),
                        ValidInventory(),
                        ValidInventory(),
                        ValidInventory(),
                        ValidInventory(),
                        new DocumentCacheStatusEnqueueTriggerComponent(
                            DocumentCacheStatusEnqueueTriggerStatus.Enabled,
                            DocumentCacheStatusInventoryReason.None,
                            message: null
                        )
                    ),
                    new DocumentCacheStatusProviderPrerequisitesComponent(
                        DocumentCacheStatusProviderPrerequisiteStatus.NotApplicable,
                        DocumentCacheStatusProviderPrerequisiteReason.None,
                        observedAt: null,
                        new DocumentCacheStatusProviderPrerequisiteComponent(
                            DocumentCacheStatusProviderPrerequisiteStatus.NotApplicable,
                            DocumentCacheStatusProviderPrerequisiteReason.None,
                            message: null
                        ),
                        new DocumentCacheStatusProviderPrerequisiteComponent(
                            DocumentCacheStatusProviderPrerequisiteStatus.NotApplicable,
                            DocumentCacheStatusProviderPrerequisiteReason.None,
                            message: null
                        )
                    ),
                    new DocumentCacheStatusLifecycleComponent(
                        DocumentCacheStatusLifecycleState.Tracking,
                        DocumentCacheStatusAvailability.Available,
                        message: null
                    ),
                    new DocumentCacheStatusCacheAheadComponent(
                        DocumentCacheStatusCacheAheadState.Clear,
                        recoveryRequired: false,
                        message: null
                    ),
                    new DocumentCacheOperationalHealthComponent(
                        DocumentCacheOperationalHealthStatus.Unknown,
                        DocumentCacheStatusReason.RuntimeNotObserved,
                        "Current-generation DocumentCache projection runtime has not been observed."
                    ),
                    new DocumentCacheCaughtUpComponent(
                        DocumentCacheCaughtUpStatus.Unknown,
                        DocumentCacheStatusReason.RuntimeNotObserved,
                        "Current-generation DocumentCache projection runtime has not been observed."
                    ),
                    new DocumentCacheStatusQueueSummary(
                        DocumentCacheStatusQueuePresence.Empty,
                        oldestWorkFirstEnqueuedAt: null,
                        oldestWorkAgeSeconds: null,
                        DocumentCacheStatusBacklogEstimate.Unavailable
                    ),
                    new DocumentCacheStatusExecutionStateComponent(
                        DocumentCacheStatusExecutionState.NotObserved,
                        observedAt: null,
                        activeWorkers: null,
                        concurrencySlotsUsed: null,
                        targetBackoffUntil: null,
                        lastSuccessfulWorkAt: null,
                        lastFailureAt: null,
                        message: null
                    ),
                    activeCommand: null,
                    lastEndedDiagnostic: null,
                    new DocumentCacheStatusDiagnosticWindow<DocumentCacheStatusTargetDiagnosticEvent>(),
                    new DocumentCacheStatusDiagnosticWindow<DocumentCacheStatusDocumentDiagnosticEvent>(),
                    new DocumentCacheStatusDiagnosticWindow<DocumentCacheStatusPoisonTraversalDiagnosticEvent>(),
                    DocumentCacheStatusEffectiveSettings.FromEffectiveSettings(
                        DocumentCacheTargetEffectiveSettings.FromOptions(new DocumentCacheOptions())
                    ),
                    new DocumentCacheStatusEnqueueFailures()
                ),
            ]
        );

    private static DocumentCacheStatusInventoryComponent ValidInventory() =>
        new(DocumentCacheStatusInventoryStatus.Valid, DocumentCacheStatusInventoryReason.None, message: null);

    private sealed class StaticStatusService(DocumentCacheStatusResponse response)
        : IDocumentCacheStatusService
    {
        public Task<DocumentCacheStatusResponse> GetStatusAsync(
            CancellationToken cancellationToken = default,
            DocumentCacheStatusEvaluationMode evaluationMode =
                DocumentCacheStatusEvaluationMode.RuntimeEndpoint
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            evaluationMode.Should().Be(DocumentCacheStatusEvaluationMode.StandaloneDirectObservation);
            return Task.FromResult(response);
        }
    }
}
