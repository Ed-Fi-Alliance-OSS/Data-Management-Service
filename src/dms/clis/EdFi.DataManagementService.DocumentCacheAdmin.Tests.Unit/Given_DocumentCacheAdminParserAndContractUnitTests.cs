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
[Category("Contract")]
public sealed class Given_DocumentCacheAdminParserAndContractUnitTests
{
    private const string Fingerprint =
        "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private static readonly DateTimeOffset ObservedAt = new(
        2026,
        8,
        16,
        7,
        34,
        56,
        789,
        TimeSpan.FromHours(-5)
    );

    [Test]
    public void It_pins_the_public_parser_surface()
    {
        RootCommand rootCommand = DocumentCacheAdminCommandSurface.CreateRootCommand();

        rootCommand
            .Subcommands.Select(command => command.Name)
            .Should()
            .Equal(
                DocumentCacheAdminCommandSurface.StatusCommandName,
                DocumentCacheAdminCommandSurface.ActivateNewEmptyCommandName,
                DocumentCacheAdminCommandSurface.ActivateOfflineCommandName,
                DocumentCacheAdminCommandSurface.DeactivateOfflineCommandName,
                DocumentCacheAdminCommandSurface.RebuildOnlineCommandName,
                DocumentCacheAdminCommandSurface.ScrubCommandName,
                DocumentCacheAdminCommandSurface.RecoverCacheAheadCommandName
            );
        OptionNames(rootCommand)
            .Should()
            .Equal(
                "--help",
                "--version",
                DocumentCacheAdminCommandSurface.JsonOptionName,
                DocumentCacheAdminCommandSurface.VerboseOptionName,
                DocumentCacheAdminCommandSurface.SettingsOptionName,
                DocumentCacheAdminCommandSurface.EnvironmentOptionName,
                DocumentCacheAdminCommandSurface.DatastoreOptionName
            );

        OptionNames(CommandByName(rootCommand, DocumentCacheAdminCommandSurface.StatusCommandName))
            .Should()
            .Equal(
                DocumentCacheAdminCommandSurface.DataStoreIdOptionName,
                DocumentCacheAdminCommandSurface.TenantKeyOptionName,
                DocumentCacheAdminCommandSurface.RequestJsonOptionName,
                DocumentCacheAdminCommandSurface.StatusObservationTimeoutSecondsOptionName,
                DocumentCacheAdminCommandSurface.StatusTimeoutSecondsOptionName
            );

        foreach (Command command in MutatingCommands(rootCommand))
        {
            string[] expectedOptions = DocumentCacheAdminCommandSurface.RequiresOfflineWriterAdmission(
                command.Name
            )
                ?
                [
                    DocumentCacheAdminCommandSurface.DataStoreIdOptionName,
                    DocumentCacheAdminCommandSurface.TenantKeyOptionName,
                    DocumentCacheAdminCommandSurface.RequestJsonOptionName,
                    DocumentCacheAdminCommandSurface.ConfirmOptionName,
                    DocumentCacheAdminCommandSurface.ExpectedPhysicalSourceFingerprintOptionName,
                    DocumentCacheAdminCommandSurface.CommandTimeoutSecondsOptionName,
                    DocumentCacheAdminCommandSurface.OfflineWriterAdmissionOptionName,
                ]
                :
                [
                    DocumentCacheAdminCommandSurface.DataStoreIdOptionName,
                    DocumentCacheAdminCommandSurface.TenantKeyOptionName,
                    DocumentCacheAdminCommandSurface.RequestJsonOptionName,
                    DocumentCacheAdminCommandSurface.ConfirmOptionName,
                    DocumentCacheAdminCommandSurface.ExpectedPhysicalSourceFingerprintOptionName,
                    DocumentCacheAdminCommandSurface.CommandTimeoutSecondsOptionName,
                ];

            OptionNames(command).Should().Equal(expectedOptions, command.Name);
        }

        rootCommand
            .Parse([
                DocumentCacheAdminCommandSurface.StatusCommandName,
                DocumentCacheAdminCommandSurface.DataStoreIdOptionName,
                "1",
                DocumentCacheAdminCommandSurface.JsonOptionName,
                "-v",
                DocumentCacheAdminCommandSurface.SettingsOptionName,
                "appsettings.json",
                DocumentCacheAdminCommandSurface.EnvironmentOptionName,
                "Development",
                DocumentCacheAdminCommandSurface.DatastoreOptionName,
                DocumentCacheAdminCommandSurface.PostgresqlDatastoreOptionValue,
            ])
            .Errors.Should()
            .BeEmpty();
    }

    [TestCase(
        DocumentCacheAdminCommandSurface.StatusCommandName,
        "--status-observation-timeout-seconds",
        "0"
    )]
    [TestCase(DocumentCacheAdminCommandSurface.StatusCommandName, "--status-timeout-seconds", "-1")]
    [TestCase(DocumentCacheAdminCommandSurface.RebuildOnlineCommandName, "--command-timeout-seconds", "abc")]
    [TestCase(DocumentCacheAdminCommandSurface.RebuildOnlineCommandName, "--confirm", "true")]
    public void It_pins_argument_validation_to_the_stable_argument_exit_code(
        string commandName,
        string optionName,
        string optionValue
    )
    {
        RootCommand rootCommand = DocumentCacheAdminCommandSurface.CreateRootCommand();

        rootCommand.Parse(ArgsFor(commandName, optionName, optionValue)).Errors.Should().NotBeEmpty();
        DocumentCacheAdminExitCodes.ArgumentError.Should().Be(64);
    }

    [TestCaseSource(nameof(MutatingJsonRequestCases))]
    public void It_parses_each_mutating_request_json_as_the_shared_command_dto(
        string commandName,
        string requestJson,
        Type expectedRequestType
    )
    {
        ParseResult parseResult = ParseCommand(
            commandName,
            DocumentCacheAdminCommandSurface.RequestJsonOptionName,
            "-"
        );

        bool parsed = DocumentCacheAdminInvocationTargetParser.TryParse(
            parseResult,
            _ => requestJson,
            out DocumentCacheAdminInvocationTarget? invocationTarget,
            out string? failure
        );

        parsed.Should().BeTrue(failure);
        invocationTarget!.JsonRequest.Should().NotBeNull();
        invocationTarget.JsonRequest!.SharedRequest.Should().BeOfType(expectedRequestType);
        invocationTarget.TargetKey.Should().Be(DocumentCacheTargetKey.Create("", 1));
    }

    [TestCase(
        """{"targetKey":{"tenantKey":"","dataStoreId":1},"targetKey":{"tenantKey":"","dataStoreId":2}}"""
    )]
    [TestCase("""{"targetKey":{"tenantKey":"","tenantKey":"TenantA","dataStoreId":1}}""")]
    public void It_rejects_duplicate_status_request_json_fields(string requestJson)
    {
        ParseResult parseResult = ParseCommand(
            DocumentCacheAdminCommandSurface.StatusCommandName,
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
        failure.Should().Contain("duplicated");
    }

    [Test]
    public void It_rejects_numeric_enum_values_in_request_json()
    {
        ParseResult parseResult = ParseCommand(
            DocumentCacheAdminCommandSurface.RebuildOnlineCommandName,
            DocumentCacheAdminCommandSurface.RequestJsonOptionName,
            "-"
        );

        bool parsed = DocumentCacheAdminInvocationTargetParser.TryParse(
            parseResult,
            _ =>
                """
                {
                  "targetKey": { "tenantKey": "", "dataStoreId": 1 },
                  "confirmation": 1
                }
                """,
            out DocumentCacheAdminInvocationTarget? invocationTarget,
            out string? failure
        );

        parsed.Should().BeFalse();
        invocationTarget.Should().BeNull();
        failure.Should().Contain("confirmation").And.Contain("must be the string value 'onlineCacheRebuild'");
    }

    [Test]
    public void It_serializes_administrative_result_enums_as_lower_camel_strings()
    {
        DocumentCacheAdministrativeCommandResult result = new(
            DocumentCacheAdministrativeCommand.OnlineCacheRebuild,
            new DocumentCacheAdministrativeTargetKey("", 1),
            DocumentCacheAdministrativeCommandStatus.RejectedNoMutation,
            DocumentCacheAdministrativeCommandClassification.CacheAheadLatchSet,
            mutated: false
        );

        string json = DocumentCacheAdminJsonSerializer.SerializeContract(
            result,
            typeof(DocumentCacheAdministrativeCommandResult)
        );
        JsonObject root = JsonNode.Parse(json)!.AsObject();

        root["command"]!.GetValue<string>().Should().Be("onlineCacheRebuild");
        root["status"]!.GetValue<string>().Should().Be("rejectedNoMutation");
        root["classification"]!.GetValue<string>().Should().Be("cacheAheadLatchSet");
        root["status"]!.ToJsonString().Should().Be("\"rejectedNoMutation\"");
        root["classification"]!.ToJsonString().Should().Be("\"cacheAheadLatchSet\"");
    }

    [Test]
    public async Task It_writes_the_complete_one_target_status_contract_from_the_cli_executor()
    {
        await using ServiceProvider serviceProvider = new ServiceCollection()
            .AddSingleton<IDocumentCacheStatusService>(
                new StaticStatusService(RepresentativeStatusResponse())
            )
            .BuildServiceProvider();
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        int exitCode = await DocumentCacheAdminCommandExecutor.ExecuteAsync(
            ParseCommand(
                DocumentCacheAdminCommandSurface.StatusCommandName,
                DocumentCacheAdminCommandSurface.DataStoreIdOptionName,
                "1",
                DocumentCacheAdminCommandSurface.JsonOptionName
            ),
            new DocumentCacheAdminInvocationTarget(DocumentCacheTargetKey.Create("", 1)),
            serviceProvider,
            stdout,
            stderr
        );

        exitCode.Should().Be(DocumentCacheAdminExitCodes.Success);
        stderr.ToString().Should().BeEmpty();

        string json = stdout.ToString();
        json.TrimEnd().Should().NotContain("\n");
        JsonObject root = JsonNode.Parse(json)!.AsObject();
        PropertyNames(root).Should().Equal("contractVersion", "observedAt", "targets");
        root["contractVersion"]!.GetValue<int>().Should().Be(1);
        root["observedAt"]!.GetValue<string>().Should().Be("2026-08-16T12:34:56.789Z");

        JsonObject target = root["targets"]!.AsArray().Should().ContainSingle().Subject!.AsObject();
        PropertyNames(target)
            .Should()
            .Equal(
                "targetKey",
                "targetGeneration",
                "processObservedAt",
                "durableObservedAt",
                "provider",
                "physicalSourceFingerprint",
                "resolution",
                "eligibility",
                "inventory",
                "providerPrerequisites",
                "lifecycle",
                "cacheAhead",
                "operationalHealth",
                "caughtUp",
                "queueSummary",
                "executionState",
                "activeCommand",
                "lastEndedDiagnostic",
                "targetDiagnostics",
                "documentDiagnostics",
                "poisonTraversalDiagnostics",
                "effectiveSettings",
                "enqueueFailures"
            );

        target["targetKey"]!["tenantKey"]!.GetValue<string>().Should().Be("");
        target["targetKey"]!["dataStoreId"]!.GetValue<long>().Should().Be(1);
        target["resolution"]!["status"]!.GetValue<string>().Should().Be("resolved");
        target["lifecycle"]!["state"]!.GetValue<string>().Should().Be("tracking");
        target["queueSummary"]!["oldestWorkAgeSeconds"]!.GetValue<double>().Should().Be(12.5);
        target["executionState"]!["status"]!.GetValue<string>().Should().Be("active");

        JsonObject activeCommand = target["activeCommand"]!.AsObject();
        activeCommand["command"]!.GetValue<string>().Should().Be("offlineActivation");
        activeCommand["phase"]!.GetValue<string>().Should().Be("drainWork");
        activeCommand["status"]!.GetValue<string>().Should().Be("running");
        activeCommand["phaseDiagnostics"]![0]!["diagnosticCategory"]!
            .GetValue<string>()
            .Should()
            .Be("providerCommandTimeout");
        target["lastEndedDiagnostic"]!["command"]!.GetValue<string>().Should().Be("onlineCacheRebuild");
        target["lastEndedDiagnostic"]!["outcome"]!.GetValue<string>().Should().Be("timedOut");

        target["targetDiagnostics"]!["recentEvents"]![0]!["category"]!
            .GetValue<string>()
            .Should()
            .Be("statusObservationTimeout");
        target["documentDiagnostics"]!["recentEvents"]![0]!["category"]!
            .GetValue<string>()
            .Should()
            .Be("poisonRetryScheduled");
        target["poisonTraversalDiagnostics"]!["recentEvents"]![0]!["category"]!
            .GetValue<string>()
            .Should()
            .Be("pageCapacityExhausted");

        JsonObject effectiveSettings = target["effectiveSettings"]!.AsObject();
        PropertyNames(effectiveSettings).Should().Equal("projector", "readAcceleration", "status");
        effectiveSettings["projector"]!["pollIntervalSeconds"]!.GetValue<double>().Should().Be(5);
        effectiveSettings["readAcceleration"]!["directFillTimeoutSeconds"]!
            .GetValue<double>()
            .Should()
            .Be(2.5);
        effectiveSettings["status"]!["endpointTimeoutSeconds"]!.GetValue<double>().Should().Be(30);
        effectiveSettings.Should().NotContainKey("administration");

        target["enqueueFailures"]!["recentEvents"]![0]!["category"]!
            .GetValue<string>()
            .Should()
            .Be("workPersistenceFailed");
        target["enqueueFailures"]!["recentEvents"]![0]!["canonicalOperation"]!
            .GetValue<string>()
            .Should()
            .Be("insert");
        target["enqueueFailures"]!["byCategory"]![0]!["category"]!
            .GetValue<string>()
            .Should()
            .Be("workPersistenceFailed");
    }

    private static ParseResult ParseCommand(string commandName, params string[] args) =>
        DocumentCacheAdminCommandSurface.CreateRootCommand().Parse([commandName, .. args]);

    private static IEnumerable<Command> MutatingCommands(RootCommand rootCommand) =>
        rootCommand.Subcommands.Where(command =>
            DocumentCacheAdminCommandSurface.IsMutatingCommand(command.Name)
        );

    private static Command CommandByName(RootCommand rootCommand, string commandName) =>
        rootCommand.Subcommands.Single(command =>
            string.Equals(command.Name, commandName, StringComparison.Ordinal)
        );

    private static IEnumerable<string> OptionNames(Command command) =>
        command.Options.Select(option => option.Name);

    private static IEnumerable<string> PropertyNames(JsonObject jsonObject) =>
        jsonObject.Select(property => property.Key);

    private static string[] ArgsFor(string commandName, string optionName, string optionValue)
    {
        if (!DocumentCacheAdminCommandSurface.IsMutatingCommand(commandName))
        {
            return
            [
                commandName,
                DocumentCacheAdminCommandSurface.DataStoreIdOptionName,
                "1",
                optionName,
                optionValue,
            ];
        }

        if (
            string.Equals(
                optionName,
                DocumentCacheAdminCommandSurface.ConfirmOptionName,
                StringComparison.Ordinal
            )
        )
        {
            return
            [
                commandName,
                DocumentCacheAdminCommandSurface.DataStoreIdOptionName,
                "1",
                optionName,
                optionValue,
            ];
        }

        return
        [
            commandName,
            DocumentCacheAdminCommandSurface.DataStoreIdOptionName,
            "1",
            DocumentCacheAdminCommandSurface.ConfirmOptionName,
            DocumentCacheAdminTestCommandContracts.ExpectedConfirmationJsonValue(commandName),
            optionName,
            optionValue,
        ];
    }

    private static IEnumerable<TestCaseData> MutatingJsonRequestCases()
    {
        yield return new TestCaseData(
            DocumentCacheAdminCommandSurface.ActivateNewEmptyCommandName,
            MutatingRequestJson("newEmptyActivation"),
            typeof(DocumentCacheGuardedNewEmptyActivationRequest)
        );
        yield return new TestCaseData(
            DocumentCacheAdminCommandSurface.ActivateOfflineCommandName,
            MutatingRequestJson("offlineActivation", includeOfflineWriterAdmission: true),
            typeof(DocumentCacheOfflineActivationRequest)
        );
        yield return new TestCaseData(
            DocumentCacheAdminCommandSurface.DeactivateOfflineCommandName,
            MutatingRequestJson("offlineDeactivation", includeOfflineWriterAdmission: true),
            typeof(DocumentCacheOfflineDeactivationRequest)
        );
        yield return new TestCaseData(
            DocumentCacheAdminCommandSurface.RebuildOnlineCommandName,
            MutatingRequestJson("onlineCacheRebuild"),
            typeof(DocumentCacheOnlineCacheRebuildRequest)
        );
        yield return new TestCaseData(
            DocumentCacheAdminCommandSurface.ScrubCommandName,
            MutatingRequestJson("integrityScrub"),
            typeof(DocumentCacheExplicitIntegrityScrubRequest)
        );
        yield return new TestCaseData(
            DocumentCacheAdminCommandSurface.RecoverCacheAheadCommandName,
            MutatingRequestJson("internalCacheAheadRecovery", includeOfflineWriterAdmission: true),
            typeof(DocumentCacheInternalOnlyCacheAheadRecoveryRequest)
        );
    }

    private static string MutatingRequestJson(string confirmation, bool includeOfflineWriterAdmission = false)
    {
        string offlineWriterAdmissionJson = includeOfflineWriterAdmission
            ? """
                ,
                  "offlineWriterAdmission": "closedAndDrained"
                """
            : string.Empty;

        return $$"""
            {
              "targetKey": { "tenantKey": "", "dataStoreId": 1 },
              "confirmation": "{{confirmation}}",
              "expectedPhysicalSourceFingerprint": "{{Fingerprint}}"{{offlineWriterAdmissionJson}}
            }
            """;
    }

    private static DocumentCacheStatusResponse RepresentativeStatusResponse() =>
        new(
            ObservedAt,
            [
                new DocumentCacheStatusTarget(
                    new DocumentCacheStatusTargetKey("", 1),
                    targetGeneration: 3,
                    processObservedAt: ObservedAt,
                    durableObservedAt: At(second: 57),
                    provider: "postgresql",
                    physicalSourceFingerprint: Fingerprint,
                    new DocumentCacheStatusResolutionComponent(
                        DocumentCacheStatusResolutionStatus.Resolved,
                        DocumentCacheStatusResolutionReason.None,
                        At(second: 55),
                        message: null
                    ),
                    new DocumentCacheStatusEligibilityComponent(
                        DocumentCacheStatusEligibilityStatus.Eligible,
                        DocumentCacheStatusReason.None,
                        message: null
                    ),
                    new DocumentCacheStatusInventoryComponentGroup(
                        At(second: 55),
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
                        DocumentCacheStatusProviderPrerequisiteStatus.Satisfied,
                        DocumentCacheStatusProviderPrerequisiteReason.None,
                        At(second: 55),
                        NotApplicableProviderPrerequisite(),
                        NotApplicableProviderPrerequisite()
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
                        DocumentCacheOperationalHealthStatus.Operational,
                        DocumentCacheStatusReason.None,
                        message: null
                    ),
                    new DocumentCacheCaughtUpComponent(
                        DocumentCacheCaughtUpStatus.CaughtUp,
                        DocumentCacheStatusReason.None,
                        message: null
                    ),
                    new DocumentCacheStatusQueueSummary(
                        DocumentCacheStatusQueuePresence.NotEmpty,
                        oldestWorkFirstEnqueuedAt: At(second: 44),
                        oldestWorkAgeSeconds: 12.5,
                        DocumentCacheStatusBacklogEstimate.Unavailable
                    ),
                    new DocumentCacheStatusExecutionStateComponent(
                        DocumentCacheStatusExecutionState.Active,
                        At(second: 56),
                        activeWorkers: 1,
                        concurrencySlotsUsed: 1,
                        targetBackoffUntil: null,
                        lastSuccessfulWorkAt: At(second: 40),
                        lastFailureAt: null,
                        message: null
                    ),
                    new DocumentCacheStatusActiveCommand(
                        DocumentCacheAdministrativeCommand.OfflineActivation,
                        DocumentCacheAdministrativeCommandPhase.DrainWork,
                        DocumentCacheStatusActiveCommandStatus.Running,
                        startedAt: At(minute: 30, second: 0),
                        observedAt: At(second: 56),
                        message: "running",
                        phaseDiagnostics:
                        [
                            new DocumentCacheAdministrativePhaseDiagnostic(
                                DocumentCacheAdministrativeCommandPhase.DrainWork,
                                DocumentCacheAdministrativeCommandPhase.SeedBaseline,
                                retryable: true,
                                DocumentCacheAdministrativeDiagnosticCategory.ProviderCommandTimeout,
                                affectedDocumentIds: [99],
                                "provider timeout"
                            ),
                        ]
                    ),
                    new DocumentCacheStatusLastEndedDiagnostic(
                        DocumentCacheAdministrativeCommand.OnlineCacheRebuild,
                        DocumentCacheAdministrativeCommandPhase.EnterTracking,
                        DocumentCacheStatusEndedCommandOutcome.TimedOut,
                        startedAt: At(minute: 20, second: 0),
                        endedAt: At(minute: 25, second: 0),
                        observedAt: At(minute: 25, second: 1),
                        message: "timed out"
                    ),
                    new DocumentCacheStatusDiagnosticWindow<DocumentCacheStatusTargetDiagnosticEvent>(
                        [
                            new DocumentCacheStatusTargetDiagnosticEvent(
                                At(second: 56),
                                DocumentCacheStatusTargetDiagnosticCategory.StatusObservationTimeout,
                                "status observation timeout"
                            ),
                        ],
                        evictedCount: 1
                    ),
                    new DocumentCacheStatusDiagnosticWindow<DocumentCacheStatusDocumentDiagnosticEvent>(
                        [
                            new DocumentCacheStatusDocumentDiagnosticEvent(
                                documentId: 101,
                                At(second: 56),
                                DocumentCacheStatusDocumentDiagnosticCategory.PoisonRetryScheduled,
                                nextRetryAt: At(minute: 35, second: 0),
                                "poison retry scheduled"
                            ),
                        ],
                        evictedCount: 2
                    ),
                    new DocumentCacheStatusDiagnosticWindow<DocumentCacheStatusPoisonTraversalDiagnosticEvent>(
                        [
                            new DocumentCacheStatusPoisonTraversalDiagnosticEvent(
                                documentId: 102,
                                At(second: 56),
                                DocumentCacheStatusPoisonTraversalDiagnosticCategory.PageCapacityExhausted,
                                nextRetryAt: null,
                                "page capacity exhausted"
                            ),
                        ],
                        evictedCount: 3
                    ),
                    new DocumentCacheStatusEffectiveSettings(
                        new DocumentCacheStatusProjectorEffectiveSettings(
                            pollIntervalSeconds: 5,
                            pageSize: 100,
                            maxConcurrentTargets: 4,
                            failureBackoffSeconds: 30,
                            baselineHighWaterMark: 10000
                        ),
                        new DocumentCacheStatusReadAccelerationEffectiveSettings(
                            enabled: true,
                            directFillTimeoutSeconds: 2.5
                        ),
                        new DocumentCacheStatusTimingEffectiveSettings(
                            statusObservationTimeoutSeconds: 5,
                            endpointTimeoutSeconds: 30
                        )
                    ),
                    new DocumentCacheStatusEnqueueFailures(
                        [
                            new DocumentCacheStatusEnqueueFailureEvent(
                                At(second: 56),
                                DocumentCacheStatusEnqueueFailureCategory.WorkPersistenceFailed,
                                DocumentCacheStatusCanonicalOperation.Insert,
                                DocumentCacheStatusResourceKind.Descriptor,
                                "work persistence failed"
                            ),
                        ],
                        [
                            new DocumentCacheStatusEnqueueFailureCategoryCount(
                                DocumentCacheStatusEnqueueFailureCategory.WorkPersistenceFailed,
                                count: 1
                            ),
                        ],
                        evictedCount: 4
                    )
                ),
            ]
        );

    private static DateTimeOffset At(int minute = 34, int second = 56) =>
        new(2026, 8, 16, 7, minute, second, TimeSpan.FromHours(-5));

    private static DocumentCacheStatusInventoryComponent ValidInventory() =>
        new(DocumentCacheStatusInventoryStatus.Valid, DocumentCacheStatusInventoryReason.None, message: null);

    private static DocumentCacheStatusProviderPrerequisiteComponent NotApplicableProviderPrerequisite() =>
        new(
            DocumentCacheStatusProviderPrerequisiteStatus.NotApplicable,
            DocumentCacheStatusProviderPrerequisiteReason.None,
            message: null
        );

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
