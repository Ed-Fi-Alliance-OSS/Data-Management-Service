// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Ddl.Tests.Unit;

[TestFixture]
public class Given_CdcProviderSetupService_Dispatch
{
    [Test]
    public async Task It_should_dispatch_only_to_the_selected_provider()
    {
        var postgresqlStep = new RecordingStep();
        var sqlServerStep = new RecordingStep();
        var postgresqlProvider = new TestProvider(
            CdcProvider.Postgresql,
            [postgresqlStep.ToSetupStep(CdcProviderArtifactKind.PostgresqlPublication)]
        );
        var sqlServerProvider = new TestProvider(
            CdcProvider.SqlServer,
            [sqlServerStep.ToSetupStep(CdcProviderArtifactKind.SqlServerCaptureInstance)]
        );
        var service = new CdcProviderSetupService([sqlServerProvider, postgresqlProvider]);

        var result = await service.SetupAsync(CdcProviderSetupContractTestData.BuildPostgresqlRequest());

        result.Provider.Should().Be(CdcProvider.Postgresql);
        postgresqlProvider.BuildCount.Should().Be(1);
        postgresqlStep.ExecutionCount.Should().Be(1);
        sqlServerProvider.BuildCount.Should().Be(0);
        sqlServerStep.ExecutionCount.Should().Be(0);
    }

    [Test]
    public async Task It_should_return_a_fail_closed_diagnostic_when_no_provider_is_registered()
    {
        var service = new CdcProviderSetupService([]);

        var result = await service.SetupAsync(CdcProviderSetupContractTestData.BuildPostgresqlRequest());

        result.Outcome.Should().Be(CdcProviderSetupOutcome.Failed);
        result
            .Diagnostics.Should()
            .ContainSingle(diagnostic => diagnostic.Code == "CDC_PROVIDER_SETUP_PROVIDER_MISSING")
            .Which.Classification.Should()
            .Be(CdcProviderRetryContinuityClassification.FailClosed);
    }
}

[TestFixture]
public class Given_CdcProviderSetupService_Setup_Modes
{
    [Test]
    public async Task It_should_allow_create_or_exact_match_only_for_steps_that_opt_in_during_initial_setup()
    {
        var sourceInspectionStep = new RecordingStep();
        var publicationStep = new RecordingStep();
        var provider = new TestProvider(
            CdcProvider.Postgresql,
            [
                sourceInspectionStep.ToSetupStep(
                    CdcProviderArtifactKind.SourceTable,
                    canCreateInInitialSetup: false
                ),
                publicationStep.ToSetupStep(
                    CdcProviderArtifactKind.PostgresqlPublication,
                    canCreateInInitialSetup: true
                ),
            ]
        );
        var service = new CdcProviderSetupService([provider]);

        await service.SetupAsync(CdcProviderSetupContractTestData.BuildPostgresqlRequest());

        sourceInspectionStep.ExecutedModes.Should().Equal(CdcProviderSetupStepMode.ExactMatchOnly);
        publicationStep.ExecutedModes.Should().Equal(CdcProviderSetupStepMode.CreateOrExactMatch);
    }

    [Test]
    public async Task It_should_force_every_step_to_exact_match_only_in_validate_only_mode()
    {
        var publicationStep = new RecordingStep();
        var service = new CdcProviderSetupService([
            new TestProvider(
                CdcProvider.Postgresql,
                [
                    publicationStep.ToSetupStep(
                        CdcProviderArtifactKind.PostgresqlPublication,
                        canCreateInInitialSetup: true
                    ),
                ]
            ),
        ]);

        await service.SetupAsync(
            CdcProviderSetupContractTestData.BuildPostgresqlRequest(mode: CdcProviderSetupMode.ValidateOnly)
        );

        publicationStep.ExecutedModes.Should().Equal(CdcProviderSetupStepMode.ExactMatchOnly);
    }

    [Test]
    public async Task It_should_fail_closed_if_a_provider_reports_creation_in_exact_match_only_mode()
    {
        var service = new CdcProviderSetupService([
            new TestProvider(
                CdcProvider.Postgresql,
                [
                    RecordingStep.Create(
                        CdcProviderArtifactKind.PostgresqlPublication,
                        artifactState: CdcProviderArtifactState.Created,
                        canCreateInInitialSetup: true
                    ),
                ]
            ),
        ]);

        var result = await service.SetupAsync(
            CdcProviderSetupContractTestData.BuildPostgresqlRequest(mode: CdcProviderSetupMode.ValidateOnly)
        );

        result.Outcome.Should().Be(CdcProviderSetupOutcome.Failed);
        result
            .Diagnostics.Should()
            .ContainSingle(diagnostic => diagnostic.Code == "CDC_PROVIDER_SETUP_UNEXPECTED_CREATE")
            .Which.Should()
            .Match<CdcProviderDiagnostic>(diagnostic =>
                diagnostic.ExpectedValue == "exact-match-only"
                && diagnostic.ObservedValue == "created"
                && diagnostic.Classification == CdcProviderRetryContinuityClassification.FailClosed
            );
    }
}

[TestFixture]
public class Given_CdcProviderSetupService_Source_Inventory
{
    [Test]
    public async Task It_should_validate_observed_source_inventory_before_later_create_steps()
    {
        var createStep = new RecordingStep();
        var observedWithoutHeartbeat = CdcProviderSetupContractTestData
            .BuildRequiredSourceInventory()
            .Where(table => table.TableKind != CdcSourceTableKind.CdcHeartbeat)
            .ToArray();
        var service = new CdcProviderSetupService([
            new TestProvider(
                CdcProvider.Postgresql,
                [
                    RecordingStep.Create(
                        CdcProviderArtifactKind.SourceTable,
                        sourceTableInventory: observedWithoutHeartbeat,
                        canCreateInInitialSetup: false
                    ),
                    createStep.ToSetupStep(
                        CdcProviderArtifactKind.PostgresqlPublication,
                        canCreateInInitialSetup: true
                    ),
                ]
            ),
        ]);

        var result = await service.SetupAsync(CdcProviderSetupContractTestData.BuildPostgresqlRequest());

        result.Outcome.Should().Be(CdcProviderSetupOutcome.Failed);
        result
            .Diagnostics.Should()
            .ContainSingle(diagnostic => diagnostic.Code == "CDC_SOURCE_TABLE_MISSING")
            .Which.Category.Should()
            .Be(CdcProviderDiagnosticCategory.MissingRequiredSourceObject);
        createStep.ExecutionCount.Should().Be(0);
    }
}

[TestFixture]
public class Given_CdcProviderSetupService_Retry
{
    [Test]
    public async Task It_should_return_matched_and_created_artifact_metadata_for_initial_retry()
    {
        var service = new CdcProviderSetupService([
            new TestProvider(
                CdcProvider.Postgresql,
                [
                    RecordingStep.Create(
                        CdcProviderArtifactKind.PostgresqlReplicationSlot,
                        new CdcSafeName("dms_binding_slot"),
                        CdcProviderArtifactState.Matched,
                        canCreateInInitialSetup: true
                    ),
                    RecordingStep.Create(
                        CdcProviderArtifactKind.PostgresqlPublication,
                        new CdcSafeName("dms_binding_publication"),
                        CdcProviderArtifactState.Created,
                        canCreateInInitialSetup: true
                    ),
                ]
            ),
        ]);

        var result = await service.SetupAsync(CdcProviderSetupContractTestData.BuildPostgresqlRequest());

        result.Outcome.Should().Be(CdcProviderSetupOutcome.CreatedOrMatched);
        result.Diagnostics.Should().BeEmpty();
        result
            .ArtifactInventory.Select(observation => (observation.ArtifactKind, observation.State))
            .Should()
            .Equal(
                (CdcProviderArtifactKind.PostgresqlReplicationSlot, CdcProviderArtifactState.Matched),
                (CdcProviderArtifactKind.PostgresqlPublication, CdcProviderArtifactState.Created)
            );
    }
}

[TestFixture]
public class Given_CdcProviderSetupService_Fail_Closed_Validation
{
    [Test]
    public async Task It_should_fail_closed_on_mismatched_artifacts_without_running_later_steps()
    {
        var laterStep = new RecordingStep();
        var service = new CdcProviderSetupService([
            new TestProvider(
                CdcProvider.Postgresql,
                [
                    RecordingStep.Create(
                        CdcProviderArtifactKind.PostgresqlPublication,
                        artifactState: CdcProviderArtifactState.Mismatched,
                        canCreateInInitialSetup: true
                    ),
                    laterStep.ToSetupStep(
                        CdcProviderArtifactKind.PostgresqlReplicationSlot,
                        canCreateInInitialSetup: true
                    ),
                ]
            ),
        ]);

        var result = await service.SetupAsync(CdcProviderSetupContractTestData.BuildPostgresqlRequest());

        result.Outcome.Should().Be(CdcProviderSetupOutcome.Failed);
        result
            .Diagnostics.Should()
            .ContainSingle(diagnostic => diagnostic.Code == "CDC_PROVIDER_ARTIFACT_MISMATCH")
            .Which.Classification.Should()
            .Be(CdcProviderRetryContinuityClassification.FailClosed);
        laterStep.ExecutionCount.Should().Be(0);
    }

    [Test]
    public async Task It_should_preserve_provider_supplied_diagnostics()
    {
        var service = new CdcProviderSetupService([
            new TestProvider(
                CdcProvider.Postgresql,
                [
                    RecordingStep.Create(
                        CdcProviderArtifactKind.PostgresqlReplicationSlot,
                        diagnostics:
                        [
                            new CdcProviderDiagnostic(
                                Code: "CDC_PROVIDER_HISTORY_UNAVAILABLE",
                                Category: CdcProviderDiagnosticCategory.ProviderHistoryUnavailable,
                                Severity: CdcProviderDiagnosticSeverity.Error,
                                PrincipalKind: CdcPrincipalKind.SetupPrincipal,
                                ArtifactKind: CdcProviderArtifactKind.PostgresqlReplicationSlot,
                                SafeName: new CdcSafeName("dms_binding_slot"),
                                ExpectedValue: "readable",
                                ObservedValue: "permission-denied",
                                ProviderErrorClass: "InsufficientPrivilege",
                                Classification: CdcProviderRetryContinuityClassification.SourceHistoryUnknown
                            ),
                        ]
                    ),
                ]
            ),
        ]);

        var result = await service.SetupAsync(CdcProviderSetupContractTestData.BuildPostgresqlRequest());

        result.Outcome.Should().Be(CdcProviderSetupOutcome.Failed);
        result
            .Diagnostics.Should()
            .ContainSingle(diagnostic => diagnostic.Code == "CDC_PROVIDER_HISTORY_UNAVAILABLE")
            .Which.ProviderErrorClass.Should()
            .Be("InsufficientPrivilege");
    }
}

[TestFixture]
public class Given_CdcBindingAwareValidation
{
    [Test]
    public async Task It_should_fail_closed_on_source_fingerprint_mismatch_before_artifact_creation()
    {
        var laterStep = new RecordingStep();
        var service = new CdcProviderSetupService([
            new TestProvider(
                CdcProvider.Postgresql,
                [
                    RecordingStep.Create(
                        CdcProviderArtifactKind.SourceFingerprint,
                        CdcSourceFingerprintMetadata.SafeArtifactName,
                        canCreateInInitialSetup: false,
                        observedSourceFingerprint: CdcProviderSetupContractTestData.OtherPostgresqlSourceFingerprint
                    ),
                    laterStep.ToSetupStep(
                        CdcProviderArtifactKind.PostgresqlPublication,
                        canCreateInInitialSetup: true
                    ),
                ]
            ),
        ]);

        var result = await service.SetupAsync(CdcProviderSetupContractTestData.BuildPostgresqlRequest());

        result.Outcome.Should().Be(CdcProviderSetupOutcome.Failed);
        result
            .Diagnostics.Should()
            .ContainSingle(diagnostic => diagnostic.Code == "CDC_BINDING_SOURCE_FINGERPRINT_MISMATCH")
            .Which.Should()
            .Match<CdcProviderDiagnostic>(diagnostic =>
                diagnostic.ExpectedValue!.Contains(
                    CdcProviderSetupContractTestData.PostgresqlSourceFingerprint.Value.Replace(':', '_'),
                    StringComparison.Ordinal
                )
                && diagnostic.ObservedValue!.Contains(
                    CdcProviderSetupContractTestData.OtherPostgresqlSourceFingerprint.Value.Replace(':', '_'),
                    StringComparison.Ordinal
                )
                && diagnostic.Classification == CdcProviderRetryContinuityClassification.FailClosed
            );
        laterStep.ExecutionCount.Should().Be(0);
    }

    [Test]
    public async Task It_should_fail_closed_when_a_provider_reports_a_substituted_binding_artifact_name()
    {
        var service = new CdcProviderSetupService([
            new TestProvider(
                CdcProvider.Postgresql,
                [
                    RecordingStep.Create(
                        CdcProviderArtifactKind.SourceFingerprint,
                        CdcSourceFingerprintMetadata.SafeArtifactName,
                        canCreateInInitialSetup: false,
                        observedSourceFingerprint: CdcProviderSetupContractTestData.PostgresqlSourceFingerprint
                    ),
                    RecordingStep.Create(
                        CdcProviderArtifactKind.PostgresqlPublication,
                        new CdcSafeName("derived_from_database_name")
                    ),
                ]
            ),
        ]);

        var result = await service.SetupAsync(CdcProviderSetupContractTestData.BuildPostgresqlRequest());

        result.Outcome.Should().Be(CdcProviderSetupOutcome.Failed);
        result
            .Diagnostics.Should()
            .ContainSingle(diagnostic => diagnostic.Code == "CDC_BINDING_ARTIFACT_NAME_MISMATCH")
            .Which.Should()
            .Match<CdcProviderDiagnostic>(diagnostic =>
                diagnostic.ArtifactKind == CdcProviderArtifactKind.PostgresqlPublication
                && diagnostic.ExpectedValue == "dms_binding_publication"
                && diagnostic.ObservedValue == "derived_from_database_name"
            );
    }

    [Test]
    public async Task It_should_fail_closed_when_expected_message_keys_do_not_match_the_binding_contract()
    {
        var service = new CdcProviderSetupService([
            new TestProvider(
                CdcProvider.Postgresql,
                [
                    RecordingStep.Create(
                        CdcProviderArtifactKind.SourceFingerprint,
                        CdcSourceFingerprintMetadata.SafeArtifactName,
                        canCreateInInitialSetup: false,
                        observedSourceFingerprint: CdcProviderSetupContractTestData.PostgresqlSourceFingerprint
                    ),
                    RecordingStep.Create(
                        CdcProviderArtifactKind.HeartbeatTable,
                        new CdcSafeName("dms.CdcHeartbeat"),
                        heartbeatActionQuery: new CdcHeartbeatActionQuery("UPDATE dms.CdcHeartbeat", "hash")
                    ),
                    RecordingStep.Create(
                        CdcProviderArtifactKind.PostgresqlPublication,
                        new CdcSafeName("dms_binding_publication"),
                        expectedMessageKeyColumns:
                        [
                            new CdcExpectedMessageKeyColumns(
                                CdcSourceTableKind.Document,
                                [new DbColumnName("DocumentId")]
                            ),
                            new CdcExpectedMessageKeyColumns(
                                CdcSourceTableKind.DocumentCache,
                                [new DbColumnName("DocumentUuid")]
                            ),
                        ]
                    ),
                    RecordingStep.Create(
                        CdcProviderArtifactKind.PostgresqlReplicationSlot,
                        new CdcSafeName("dms_binding_slot")
                    ),
                ]
            ),
        ]);

        var result = await service.SetupAsync(CdcProviderSetupContractTestData.BuildPostgresqlRequest());

        result.Outcome.Should().Be(CdcProviderSetupOutcome.Failed);
        result
            .Diagnostics.Should()
            .ContainSingle(diagnostic =>
                diagnostic.Code == "CDC_BINDING_MESSAGE_KEY_COLUMNS_MISMATCH"
                && diagnostic.SafeName.Value == "dms.Document"
            )
            .Which.Should()
            .Match<CdcProviderDiagnostic>(diagnostic =>
                diagnostic.ExpectedValue == "DocumentUuid" && diagnostic.ObservedValue == "DocumentId"
            );
    }

    [Test]
    public async Task It_should_fail_closed_when_connector_grants_include_the_projection_work_table()
    {
        var service = new CdcProviderSetupService([
            new TestProvider(
                CdcProvider.Postgresql,
                [
                    RecordingStep.Create(
                        CdcProviderArtifactKind.SourceFingerprint,
                        CdcSourceFingerprintMetadata.SafeArtifactName,
                        canCreateInInitialSetup: false,
                        observedSourceFingerprint: CdcProviderSetupContractTestData.PostgresqlSourceFingerprint,
                        grantInventory:
                        [
                            new CdcGrantObservation(
                                CdcPrincipalKind.ConnectorPrincipal,
                                new CdcSafeName("connector_principal"),
                                CdcProviderArtifactKind.Grant,
                                new CdcSafeName("dms.DocumentProjectionWork"),
                                ["SELECT"],
                                []
                            ),
                        ]
                    ),
                ]
            ),
        ]);

        var result = await service.SetupAsync(CdcProviderSetupContractTestData.BuildPostgresqlRequest());

        result.Outcome.Should().Be(CdcProviderSetupOutcome.Failed);
        result
            .Diagnostics.Should()
            .ContainSingle(diagnostic => diagnostic.Code == "CDC_BINDING_WORK_TABLE_GRANT_FORBIDDEN")
            .Which.Category.Should()
            .Be(CdcProviderDiagnosticCategory.WorkTableGrantViolation);
    }

    [Test]
    public async Task It_should_keep_work_table_grant_diagnostic_when_the_grant_principal_is_wrong()
    {
        var service = new CdcProviderSetupService([
            new TestProvider(
                CdcProvider.Postgresql,
                [
                    RecordingStep.Create(
                        CdcProviderArtifactKind.SourceFingerprint,
                        CdcSourceFingerprintMetadata.SafeArtifactName,
                        canCreateInInitialSetup: false,
                        observedSourceFingerprint: CdcProviderSetupContractTestData.PostgresqlSourceFingerprint,
                        grantInventory:
                        [
                            new CdcGrantObservation(
                                CdcPrincipalKind.ConnectorPrincipal,
                                new CdcSafeName("other_principal"),
                                CdcProviderArtifactKind.Grant,
                                new CdcSafeName("dms.DocumentProjectionWork"),
                                ["SELECT"],
                                []
                            ),
                        ]
                    ),
                ]
            ),
        ]);

        var result = await service.SetupAsync(CdcProviderSetupContractTestData.BuildPostgresqlRequest());

        result.Outcome.Should().Be(CdcProviderSetupOutcome.Failed);
        result
            .Diagnostics.Where(diagnostic => diagnostic.SafeName.Value == "dms.DocumentProjectionWork")
            .Select(diagnostic => diagnostic.Code)
            .Should()
            .Contain("CDC_BINDING_CONNECTOR_PRINCIPAL_MISMATCH", "CDC_BINDING_WORK_TABLE_GRANT_FORBIDDEN");
        result
            .Diagnostics.Should()
            .ContainSingle(diagnostic =>
                diagnostic.Category == CdcProviderDiagnosticCategory.WorkTableGrantViolation
            );
    }
}

[TestFixture]
public class Given_CdcProviderSetupService_Registration
{
    [Test]
    public async Task It_should_register_the_provider_neutral_orchestrator()
    {
        var provider = new TestProvider(
            CdcProvider.Postgresql,
            [RecordingStep.Create(CdcProviderArtifactKind.PostgresqlPublication)]
        );
        ServiceCollection services = [];
        services.AddSingleton<ICdcProviderSetupProvider>(provider);
        services.AddCdcProviderSetupService();

        using var serviceProvider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true }
        );
        await using var scope = serviceProvider.CreateAsyncScope();

        var service = scope.ServiceProvider.GetRequiredService<ICdcProviderSetupService>();
        var result = await service.SetupAsync(CdcProviderSetupContractTestData.BuildPostgresqlRequest());

        result.Outcome.Should().Be(CdcProviderSetupOutcome.ExactMatch);
    }
}

[TestFixture]
public class Given_CdcProviderSetupService_Result_Surface
{
    [Test]
    public void It_should_not_expose_connector_registration_or_ordinary_schema_fingerprint_state()
    {
        typeof(CdcProviderSetupResult)
            .GetProperties()
            .Select(property => property.Name)
            .Should()
            .NotContain([
                "ConnectorJson",
                "KafkaTopic",
                "KafkaConnectRegistration",
                "BindingState",
                "EffectiveSchemaHash",
                "ResourceKeySeedHash",
                "RelationalMappingVersion",
                "MappingPack",
            ]);
    }
}

internal sealed class TestProvider(CdcProvider provider, IReadOnlyList<CdcProviderSetupStep> steps)
    : ICdcProviderSetupProvider
{
    public int BuildCount { get; private set; }

    public CdcProvider Provider { get; } = provider;

    public IReadOnlyList<CdcProviderSetupStep> BuildSetupSteps(CdcProviderSetupRequest request)
    {
        BuildCount++;
        return steps;
    }
}

internal sealed class RecordingStep
{
    private const string DefaultSafeName = "dms_binding_artifact";
    private readonly CdcProviderArtifactState _artifactState;
    private readonly IReadOnlyList<CdcSourceTableInventory> _sourceTableInventory;
    private readonly CdcSourceFingerprint? _observedSourceFingerprint;
    private readonly IReadOnlyList<CdcGrantObservation> _grantInventory;
    private readonly IReadOnlyList<CdcExpectedMessageKeyColumns> _expectedMessageKeyColumns;
    private readonly CdcHeartbeatActionQuery? _heartbeatActionQuery;
    private readonly IReadOnlyList<CdcProviderHistoryObservation> _providerHistoryObservations;
    private readonly IReadOnlyList<CdcProviderDiagnostic> _diagnostics;
    private readonly CdcSafeName _safeName;

    public RecordingStep(
        CdcProviderArtifactState artifactState = CdcProviderArtifactState.Matched,
        CdcSafeName? safeName = null,
        IReadOnlyList<CdcSourceTableInventory>? sourceTableInventory = null,
        CdcSourceFingerprint? observedSourceFingerprint = null,
        IReadOnlyList<CdcGrantObservation>? grantInventory = null,
        IReadOnlyList<CdcExpectedMessageKeyColumns>? expectedMessageKeyColumns = null,
        CdcHeartbeatActionQuery? heartbeatActionQuery = null,
        IReadOnlyList<CdcProviderHistoryObservation>? providerHistoryObservations = null,
        IReadOnlyList<CdcProviderDiagnostic>? diagnostics = null
    )
    {
        _artifactState = artifactState;
        _sourceTableInventory = sourceTableInventory ?? [];
        _observedSourceFingerprint = observedSourceFingerprint;
        _grantInventory = grantInventory ?? [];
        _expectedMessageKeyColumns = expectedMessageKeyColumns ?? [];
        _heartbeatActionQuery = heartbeatActionQuery;
        _providerHistoryObservations = providerHistoryObservations ?? [];
        _diagnostics = diagnostics ?? [];
        _safeName = safeName ?? new CdcSafeName(DefaultSafeName);
    }

    public IReadOnlyList<CdcProviderSetupStepMode> ExecutedModes => _executedModes;

    public int ExecutionCount => _executedModes.Count;

    private readonly List<CdcProviderSetupStepMode> _executedModes = [];

    public static CdcProviderSetupStep Create(
        CdcProviderArtifactKind artifactKind,
        CdcSafeName? safeName = null,
        CdcProviderArtifactState artifactState = CdcProviderArtifactState.Matched,
        bool canCreateInInitialSetup = true,
        IReadOnlyList<CdcSourceTableInventory>? sourceTableInventory = null,
        CdcSourceFingerprint? observedSourceFingerprint = null,
        IReadOnlyList<CdcGrantObservation>? grantInventory = null,
        IReadOnlyList<CdcExpectedMessageKeyColumns>? expectedMessageKeyColumns = null,
        CdcHeartbeatActionQuery? heartbeatActionQuery = null,
        IReadOnlyList<CdcProviderHistoryObservation>? providerHistoryObservations = null,
        IReadOnlyList<CdcProviderDiagnostic>? diagnostics = null
    ) =>
        new RecordingStep(
            artifactState,
            safeName,
            sourceTableInventory,
            observedSourceFingerprint,
            grantInventory,
            expectedMessageKeyColumns,
            heartbeatActionQuery,
            providerHistoryObservations,
            diagnostics
        ).ToSetupStep(artifactKind, canCreateInInitialSetup);

    public CdcProviderSetupStep ToSetupStep(
        CdcProviderArtifactKind artifactKind,
        bool canCreateInInitialSetup = true
    ) =>
        new(
            artifactKind,
            _safeName,
            canCreateInInitialSetup,
            (context, _) =>
            {
                _executedModes.Add(context.Mode);
                return Task.FromResult(
                    new CdcProviderSetupStepResult(
                        observedSourceFingerprint: _observedSourceFingerprint,
                        artifactInventory:
                        [
                            new CdcProviderArtifactObservation(
                                artifactKind,
                                _safeName,
                                _artifactState,
                                new Dictionary<string, string> { ["state"] = _artifactState.ToString() }
                            ),
                        ],
                        grantInventory: _grantInventory,
                        sourceTableInventory: _sourceTableInventory,
                        expectedMessageKeyColumns: _expectedMessageKeyColumns,
                        heartbeatActionQuery: _heartbeatActionQuery,
                        providerHistoryObservations: _providerHistoryObservations,
                        diagnostics: _diagnostics
                    )
                );
            }
        );
}
